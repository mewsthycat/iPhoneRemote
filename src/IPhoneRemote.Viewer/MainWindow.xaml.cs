using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using IPhoneRemote.Core.Wda;

namespace IPhoneRemote.Viewer;

public partial class MainWindow : Window
{
    // Gesture thresholds, in iOS points / milliseconds.
    private const double TapSlop = 8;
    private const int LongPressMs = 500;
    private const double WheelPointsPerNotch = 120;
    // iPhone display corners are ~12% of the screen width.
    private const double CornerRatio = 0.12;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly WdaLauncher _launcher;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _connection;
    private CancellationTokenSource? _retryWait;
    private DeviceSession? _session;

    // Frame hand-off: the decoder thread overwrites _latestFrame; the UI thread only ever
    // renders the newest one, so a slow UI drops frames instead of building latency.
    private BitmapSource? _latestFrame;
    private int _renderQueued;
    private int _framesThisSecond;
    private double _fps;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

    private Point _downPoints;
    private Point _downPixels;
    private readonly Stopwatch _downClock = new();
    private Line? _dragLine;

    private readonly StringBuilder _pendingText = new();
    private bool _textFlushQueued;
    private string _lastInput = "";
    private bool _loadingSettings;

    public MainWindow()
    {
        _launcher = new WdaLauncher(_settings);
        InitializeComponent();
        ApplySettingsToUi();
    }

    // ---------- Window ----------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Rounded Win11 corners for the borderless window (ignored on Windows 10).
        var hwnd = new WindowInteropHelper(this).Handle;
        var round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));

        if (_settings is { Left: { } l, Top: { } t, Width: { } w, Height: { } h })
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(new Rect(l, t, w, h)))
            {
                Left = l; Top = t; Width = w; Height = h;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ConnectionLoopAsync();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Left = Left; _settings.Top = Top; _settings.Width = Width; _settings.Height = Height;
        }
        _settings.Save();
        _closing.Cancel();
        _ = _session?.DisposeAsync();
        _launcher.Dispose(); // stops the tunnel / WDA processes this window started
    }

    // ---------- Connection ----------

    private enum State { Connecting, Live, Failed }

    private async Task ConnectionLoopAsync()
    {
        var progress = new Progress<string>(msg => ShowOverlay(State.Connecting, "Connecting", msg));
        while (!_closing.IsCancellationRequested)
        {
            _connection = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            var ct = _connection.Token;
            try
            {
                ShowOverlay(State.Connecting, "Connecting", "Looking for your iPhone…");
                var profile = StreamProfile.For(_settings.Quality);
                _session = await Task.Run(() => DeviceSession.ConnectAsync(_launcher, profile, progress, ct), ct);
                _settings.Save(); // remember the WDA bundle id once it's known
                _session.InputCompleted += OnInputCompleted;
                TitleText.Text = _session.DeviceName;
                _lastInput = "";

                await Task.Run(() => StreamLoopAsync(_session, ct), ct);
                ShowOverlay(State.Connecting, "Reconnecting", "The screen stream stopped.");
                await Task.Delay(1000, _closing.Token);
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Reconnect requested (F5 / Reconnect button): loop straight back round.
            }
            catch (Exception ex)
            {
                ShowOverlay(State.Failed, "Can't connect", ex.Message);
                await WaitForRetryAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                var old = _session;
                _session = null;
                Screen.Source = null;
                if (old != null) await old.DisposeAsync();
                _connection.Dispose();
            }
        }
    }

    private async Task WaitForRetryAsync(TimeSpan delay)
    {
        var wait = _retryWait = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        try { await Task.Delay(delay, wait.Token); }
        catch (OperationCanceledException) { }
        // Each attempt registers on _closing; dispose so failed retries don't accumulate.
        _retryWait = null;
        wait.Dispose();
    }

    private void OnRetry(object sender, RoutedEventArgs e) => Reconnect();

    private void Reconnect()
    {
        SettingsPopup.IsOpen = false;
        _retryWait?.Cancel();
        _connection?.Cancel();
    }

    private void ShowOverlay(State state, string title, string detail)
    {
        Overlay.Visibility = Visibility.Visible;
        Bezel.Visibility = Visibility.Hidden;
        OverlayTitle.Text = title;
        OverlayDetail.Text = detail;
        OverlayIcon.Text = state == State.Failed ? "" : "";
        RetryButton.Visibility = state == State.Failed ? Visibility.Visible : Visibility.Collapsed;
        SetState(state);
    }

    private void SetState(State state)
    {
        StateDot.Fill = (Brush)FindResource(state switch
        {
            State.Live => "Live",
            State.Failed => "Fail",
            _ => "Busy",
        });
        if (state != State.Live) StatsText.Text = "";
    }

    // ---------- Video ----------

    private async Task StreamLoopAsync(DeviceSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var jpeg in session.FramesAsync(ct))
            {
                var frame = Decode(jpeg);
                if (frame == null) continue;
                Volatile.Write(ref _latestFrame, frame);
                if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
                    _ = Dispatcher.BeginInvoke(RenderLatestFrame, DispatcherPriority.Render);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { } // stream dropped → reconnect
    }

    /// <summary>Decodes synchronously (OnLoad), so the reader may reuse the buffer afterwards.</summary>
    private static BitmapSource? Decode(ReadOnlyMemory<byte> jpeg)
    {
        if (!MemoryMarshal.TryGetArray(jpeg, out var segment)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
            bmp.EndInit();
            bmp.Freeze(); // allows handing it to the UI thread
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
        {
            return null; // corrupted frame; skip it
        }
    }

    private void RenderLatestFrame()
    {
        Interlocked.Exchange(ref _renderQueued, 0);
        if (_session == null) return;
        Screen.Source = Volatile.Read(ref _latestFrame);
        if (Overlay.Visibility == Visibility.Visible)
        {
            Overlay.Visibility = Visibility.Collapsed;
            Bezel.Visibility = Visibility.Visible;
            SetState(State.Live);
        }

        _framesThisSecond++;
        if (_fpsClock.ElapsedMilliseconds >= 1000)
        {
            _fps = _framesThisSecond * 1000.0 / _fpsClock.ElapsedMilliseconds;
            _framesThisSecond = 0;
            _fpsClock.Restart();
            UpdateStats();
        }
    }

    private void OnScreenSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var r = e.NewSize.Width * CornerRatio;
        var clip = new RectangleGeometry(new Rect(e.NewSize), r, r);
        clip.Freeze();
        Screen.Clip = clip;
        TouchLayer.Clip = clip;
        Bezel.CornerRadius = new CornerRadius(r + Bezel.BorderThickness.Left);
    }

    // ---------- Mouse → touch ----------

    /// <summary>Window position over the image → iOS points.</summary>
    private Point ToPoints(Point p)
    {
        var s = _session!;
        var x = Math.Clamp(p.X / Screen.ActualWidth * s.PointsWidth, 0, s.PointsWidth - 1);
        var y = Math.Clamp(p.Y / Screen.ActualHeight * s.PointsHeight, 0, s.PointsHeight - 1);
        return new Point(Math.Round(x), Math.Round(y));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_session == null || Screen.Source == null) return;
        _downPixels = e.GetPosition(Screen);
        _downPoints = ToPoints(_downPixels);
        _downClock.Restart();
        Screen.CaptureMouse();
        ShowTouch(_downPixels);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!Screen.IsMouseCaptured || !_settings.ShowTouches) return;
        var p = e.GetPosition(Screen);
        if ((p - _downPixels).Length < 6) return;
        if (_dragLine == null)
        {
            _dragLine = new Line
            {
                Stroke = DragBrush,
                StrokeThickness = 6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                X1 = _downPixels.X, Y1 = _downPixels.Y,
            };
            TouchLayer.Children.Add(_dragLine);
        }
        _dragLine.X2 = p.X;
        _dragLine.Y2 = p.Y;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!Screen.IsMouseCaptured) return;
        Screen.ReleaseMouseCapture(); // even if the connection dropped mid-drag
        FadeOut(_dragLine);
        _dragLine = null;
        if (_session == null) return;

        var up = ToPoints(e.GetPosition(Screen));
        var heldMs = (int)_downClock.ElapsedMilliseconds;
        var start = _downPoints;
        var moved = (up - start).Length;

        if (moved <= TapSlop && heldMs < LongPressMs)
            _session.Enqueue("tap", w => w.TapAsync(start.X, start.Y));
        else if (moved <= TapSlop)
            _session.Enqueue("long press", w => w.LongPressAsync(start.X, start.Y, heldMs));
        else
        {
            // Replay the drag at the speed it was made: quick flicks keep momentum, slow drags don't.
            var duration = Math.Clamp(heldMs, 80, 1500);
            _session.Enqueue("swipe", w => w.SwipeAsync(start.X, start.Y, up.X, up.Y, duration));
        }
        e.Handled = true;
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_session == null) return;
        var px = e.GetPosition(Screen);
        var p = ToPoints(px);
        ShowTouch(px);
        _session.Enqueue("long press", w => w.LongPressAsync(p.X, p.Y));
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_session == null) return;
        var p = ToPoints(e.GetPosition(Screen));
        // Wheel up = see content above = finger moves down.
        var dy = e.Delta / 120.0 * WheelPointsPerNotch;
        var endY = Math.Clamp(p.Y + dy, 0, _session.PointsHeight - 1);
        _session.Enqueue("scroll", w => w.SwipeAsync(p.X, p.Y, p.X, endY, 150));
        e.Handled = true;
    }

    // Shared, frozen brushes: frozen brushes are thread-safe and skip change tracking.
    private static readonly Brush DragBrush = Frozen(Color.FromArgb(150, 255, 255, 255));
    private static readonly Brush TouchFill = Frozen(Color.FromArgb(90, 255, 255, 255));
    private static readonly Brush TouchStroke = Frozen(Color.FromArgb(200, 255, 255, 255));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>A soft ripple where the click landed, so taps feel instant despite WDA's ~0.6 s round trip.</summary>
    private void ShowTouch(Point at)
    {
        if (!_settings.ShowTouches) return;
        const double size = 34;
        var dot = new Ellipse
        {
            Width = size, Height = size,
            Fill = TouchFill,
            Stroke = TouchStroke, StrokeThickness = 1.5,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.6, 0.6),
        };
        Canvas.SetLeft(dot, at.X - size / 2);
        Canvas.SetTop(dot, at.Y - size / 2);
        TouchLayer.Children.Add(dot);
        var grow = new DoubleAnimation(0.6, 1.3, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase() };
        dot.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        dot.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        FadeOut(dot, 420);
    }

    private void FadeOut(UIElement? element, int ms = 250)
    {
        if (element == null) return;
        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(ms));
        fade.Completed += (_, _) => TouchLayer.Children.Remove(element);
        element.BeginAnimation(OpacityProperty, fade);
    }

    // ---------- Keyboard ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (UrlPopup.IsOpen) return; // typing a URL
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var handled = true;
        switch (e.Key)
        {
            case Key.F5: Reconnect(); break;
            case Key.T when ctrl: TogglePin(); break;
            case Key.O when ctrl: OnOpenUrl(this, e); break;
            default: handled = _session != null && HandleDeviceKey(e.Key, ctrl); break;
        }
        e.Handled = handled;
    }

    private bool HandleDeviceKey(Key key, bool ctrl)
    {
        switch (key)
        {
            case Key.Escape: OnHome(this, new RoutedEventArgs()); break;
            case Key.PageUp: OnVolumeUp(this, new RoutedEventArgs()); break;
            case Key.PageDown: OnVolumeDown(this, new RoutedEventArgs()); break;
            case Key.E when ctrl: OnAppSwitcher(this, new RoutedEventArgs()); break;
            case Key.N when ctrl: OnNotifications(this, new RoutedEventArgs()); break;
            case Key.K when ctrl: OnControlCenter(this, new RoutedEventArgs()); break;
            case Key.L when ctrl: OnLock(this, new RoutedEventArgs()); break;
            case Key.S when ctrl: OnScreenshot(this, new RoutedEventArgs()); break;
            case Key.V when ctrl: OnPaste(this, new RoutedEventArgs()); break;
            case Key.Back: QueueText("\b"); break;
            case Key.Enter: QueueText("\n"); break;
            case Key.Tab: QueueText("\t"); break;
            default: return false;
        }
        return true;
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (UrlPopup.IsOpen || _session == null || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        QueueText(e.Text);
        e.Handled = true;
    }

    /// <summary>
    /// Typing is batched: characters typed while a previous /wda/keys call is in flight go out
    /// together in the next call, so fast typing doesn't queue up one HTTP request per key.
    /// </summary>
    private void QueueText(string text)
    {
        _pendingText.Append(text);
        if (_textFlushQueued) return;
        _textFlushQueued = true;
        _session!.Enqueue("type", async w =>
        {
            var batch = await Dispatcher.InvokeAsync(() =>
            {
                var s = _pendingText.ToString();
                _pendingText.Clear();
                _textFlushQueued = false;
                return s;
            });
            if (batch.Length > 0) await w.TypeAsync(batch);
        });
    }

    // ---------- Toolbar ----------

    private void Send(string label, Func<WdaClient, Task> action)
    {
        if (_session == null) { ShowToast("Not connected yet"); return; }
        _session.Enqueue(label, action);
    }

    private void OnHome(object sender, RoutedEventArgs e) => Send("home", w => w.HomeAsync());
    private void OnVolumeUp(object sender, RoutedEventArgs e) => Send("volume up", w => w.PressButtonAsync("volumeUp"));
    private void OnVolumeDown(object sender, RoutedEventArgs e) => Send("volume down", w => w.PressButtonAsync("volumeDown"));

    private void OnAppSwitcher(object sender, RoutedEventArgs e)
    {
        if (_session is not { } s) return;
        Send("app switcher", w => w.AppSwitcherAsync(s.PointsWidth, s.PointsHeight));
    }

    // Edge swipes: top-left corner pulls down Notification Center, top-right Control Center.
    private void OnNotifications(object sender, RoutedEventArgs e)
    {
        if (_session is not { } s) return;
        Send("notifications", w => w.SwipeAsync(s.PointsWidth * 0.2, 1, s.PointsWidth * 0.2, s.PointsHeight * 0.55, 250));
    }

    private void OnControlCenter(object sender, RoutedEventArgs e)
    {
        if (_session is not { } s) return;
        Send("control center", w => w.SwipeAsync(s.PointsWidth * 0.88, 1, s.PointsWidth * 0.88, s.PointsHeight * 0.45, 250));
    }

    private void OnLock(object sender, RoutedEventArgs e) => Send("lock", async w =>
    {
        if (await w.IsLockedAsync()) await w.UnlockAsync();
        else await w.LockAsync();
    });

    private void OnScreenshot(object sender, RoutedEventArgs e) => Send("screenshot", async w =>
    {
        var png = await w.ScreenshotPngAsync();
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iPhone Remote");
        Directory.CreateDirectory(dir);
        var stem = System.IO.Path.Combine(dir, $"iPhone {DateTime.Now:yyyy-MM-dd HH.mm.ss}");
        var file = stem + ".png";
        for (var n = 2; File.Exists(file); n++) file = $"{stem} ({n}).png"; // two shots in one second
        await File.WriteAllBytesAsync(file, png);
        await Dispatcher.InvokeAsync(() =>
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(png);
            img.EndInit();
            img.Freeze();
            try
            {
                Clipboard.SetImage(img);
                ShowToast("Screenshot saved to Pictures\\iPhone Remote and copied");
            }
            catch (COMException)
            {
                // Another app has the clipboard open; the file is saved regardless.
                ShowToast("Screenshot saved to Pictures\\iPhone Remote (clipboard was busy)");
            }
        });
    });

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText()) { ShowToast("Clipboard has no text"); return; }
            text = Clipboard.GetText();
        }
        catch (COMException)
        {
            // Clipboard locked by another app. Unhandled here, this would crash the window.
            ShowToast("The clipboard is busy. Try again.");
            return;
        }
        Send("paste", w => w.TypeAsync(text));
    }

    private void OnOpenUrl(object sender, RoutedEventArgs e) => UrlPopup.IsOpen = true;

    private void OnUrlPopupOpened(object? sender, EventArgs e)
    {
        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnUrlGo(sender, e);
        else if (e.Key == Key.Escape) UrlPopup.IsOpen = false;
    }

    private void OnUrlGo(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0) return;
        if (!url.Contains(':')) url = "https://" + url;
        UrlPopup.IsOpen = false;
        Send("open link", w => w.OpenUrlAsync(url));
    }

    private void OnPinToggled(object sender, RoutedEventArgs e) => SetTopmost(PinButton.IsChecked == true);
    private void OnTopmostSwitchToggled(object sender, RoutedEventArgs e) => SetTopmost(TopmostSwitch.IsChecked == true);
    private void TogglePin() => SetTopmost(!Topmost);

    private void SetTopmost(bool on)
    {
        Topmost = _settings.Topmost = on;
        PinButton.IsChecked = TopmostSwitch.IsChecked = on;
        ShowToast(on ? "Keeping window on top" : "Window no longer on top");
    }

    // ---------- Settings ----------

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

    private void ApplySettingsToUi()
    {
        _loadingSettings = true;
        (_settings.Quality switch
        {
            StreamQuality.Sharp => QualitySharp,
            StreamQuality.Saver => QualitySaver,
            _ => QualitySmooth,
        }).IsChecked = true;
        UpdateQualityHint();
        ShowTouchesSwitch.IsChecked = _settings.ShowTouches;
        Topmost = _settings.Topmost;
        PinButton.IsChecked = TopmostSwitch.IsChecked = _settings.Topmost;
        _loadingSettings = false;
    }

    private void OnQualityChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.Quality = QualitySharp.IsChecked == true ? StreamQuality.Sharp
                          : QualitySaver.IsChecked == true ? StreamQuality.Saver
                          : StreamQuality.Smooth;
        UpdateQualityHint();
        _settings.Save();
        var p = StreamProfile.For(_settings.Quality);
        _session?.Enqueue("quality", w => w.ConfigureStreamAsync(p.Framerate, p.ScalingFactor, p.JpegQuality));
    }

    private void UpdateQualityHint()
    {
        var p = StreamProfile.For(_settings.Quality);
        QualityHint.Text = $"{p.Framerate} fps · {p.ScalingFactor}% resolution";
    }

    private void OnShowTouchesToggled(object sender, RoutedEventArgs e)
    {
        _settings.ShowTouches = ShowTouchesSwitch.IsChecked == true;
        _settings.Save();
    }

    // ---------- Status ----------

    private void OnInputCompleted(string label, long ms, Exception? error) => Dispatcher.BeginInvoke(() =>
    {
        if (error == null)
        {
            _lastInput = $"{label} {ms} ms";
            if (ms > 3000)
                ShowToast($"The phone took {ms / 1000.0:0.#} s to respond. Busy apps like video feeds slow WebDriverAgent down.");
        }
        else
        {
            _lastInput = "";
            ShowToast($"{char.ToUpper(label[0])}{label[1..]} failed: {Shorten(error.Message)}");
        }
        UpdateStats();
    });

    private void UpdateStats()
    {
        if (_session == null) return;
        StatsText.Text = _lastInput.Length > 0 ? $"{_fps:0} fps · {_lastInput}" : $"{_fps:0} fps";
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2700))));
        Toast.BeginAnimation(OpacityProperty, anim);
    }

    private static string Shorten(string message)
    {
        // WDA errors embed JSON with a long traceback; keep just the human-readable message.
        var i = message.IndexOf("\"message\"", StringComparison.Ordinal);
        if (i >= 0)
        {
            var start = message.IndexOf('"', message.IndexOf(':', i) + 1) + 1;
            var end = message.IndexOf('"', start);
            if (start > 0 && end > start) message = message[start..end];
        }
        return message.Length > 90 ? message[..90] + "…" : message;
    }
}
