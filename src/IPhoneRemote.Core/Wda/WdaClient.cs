using System.Text.Json;
using System.Text.Json.Nodes;

namespace IPhoneRemote.Core.Wda;

/// <summary>
/// Minimal WebDriverAgent client. WDA is an XCTest runner app on the iPhone that exposes a
/// W3C WebDriver HTTP API (default device port 8100) and an MJPEG screen stream (9100).
/// All coordinates are in iOS *points* (e.g. 393 x 852), not pixels; see GetWindowSizeAsync.
/// Measured on iOS 18.6: a tap round-trips in ~0.6 s (longer while busy apps such as video
/// feeds are in front, because WDA snapshots the app first); Home takes ~20 ms.
/// </summary>
public sealed class WdaClient(Uri baseUri) : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http = new() { BaseAddress = baseUri, Timeout = RequestTimeout };
    private string? _sessionId;

    public async Task<JsonNode?> StatusAsync(CancellationToken ct = default) =>
        (await GetAsync("/status", ct))?["value"];

    public async Task<string> CreateSessionAsync(CancellationToken ct = default)
    {
        var res = await PostAsync("/session", new
        {
            capabilities = new { alwaysMatch = new { shouldWaitForQuiescence = false } },
        }, ct);
        // Leave WDA's idle-wait and snapshot settings at their defaults. Measured on iOS 18.6:
        // waitForIdleTimeout=0 made taps slower in busy apps and caused 60 s hangs, a shallow
        // snapshotMaxDepth made them slower, and pinning defaultActiveApplication broke taps entirely.
        _sessionId = res?["sessionId"]?.GetValue<string>() ?? res?["value"]?["sessionId"]?.GetValue<string>()
                     ?? throw new IOException("WDA did not return a sessionId");
        return _sessionId;
    }

    public Task UpdateSettingsAsync(IDictionary<string, object> settings, CancellationToken ct = default) =>
        PostAsync($"/session/{Sid}/appium/settings", new { settings }, ct);

    /// <summary>Tune the MJPEG stream. Lower scaling/quality = lower latency.</summary>
    public Task ConfigureStreamAsync(int framerate = 30, int scalingFactor = 50, int quality = 60, CancellationToken ct = default) =>
        UpdateSettingsAsync(new Dictionary<string, object>
        {
            ["mjpegServerFramerate"] = framerate,
            ["mjpegScalingFactor"] = scalingFactor,
            ["mjpegServerScreenshotQuality"] = quality,
        }, ct);

    public async Task<(double Width, double Height)> GetWindowSizeAsync(CancellationToken ct = default)
    {
        // /wda/screen doesn't depend on resolving the foreground app, unlike /window/size.
        var v = (await GetAsync("/wda/screen", ct))?["value"]?["screenSize"];
        return (v!["width"]!.GetValue<double>(), v["height"]!.GetValue<double>());
    }

    // A press with no hold registers fine on iOS and is ~50 ms quicker than holding.
    public Task TapAsync(double x, double y, CancellationToken ct = default) =>
        PerformTouchAsync([
            Move(x, y, 0), Down(), Up(),
        ], ct);

    /// <summary>Swipe up from the bottom edge and pause, which opens the app switcher.</summary>
    public Task AppSwitcherAsync(double screenWidth, double screenHeight, CancellationToken ct = default) =>
        PerformTouchAsync([
            Move(screenWidth / 2, screenHeight - 2, 0), Down(),
            Move(screenWidth / 2, screenHeight * 0.62, 250), Pause(400), Up(),
        ], ct);

    public Task SwipeAsync(double x1, double y1, double x2, double y2, int durationMs = 250, CancellationToken ct = default) =>
        PerformTouchAsync([
            Move(x1, y1, 0), Down(), Move(x2, y2, durationMs), Up(),
        ], ct);

    public Task LongPressAsync(double x, double y, int durationMs = 800, CancellationToken ct = default) =>
        PerformTouchAsync([
            Move(x, y, 0), Down(), Pause(durationMs), Up(),
        ], ct);

    /// <summary>Types into the currently focused field.</summary>
    public Task TypeAsync(string text, CancellationToken ct = default) =>
        PostAsync($"/session/{Sid}/wda/keys", new { value = text.Select(c => c.ToString()).ToArray() }, ct);

    public Task HomeAsync(CancellationToken ct = default) => PostAsync("/wda/homescreen", new { }, ct);

    /// <summary>Hardware buttons: "home", "volumeUp", "volumeDown".</summary>
    public Task PressButtonAsync(string name, CancellationToken ct = default) =>
        PostAsync($"/session/{Sid}/wda/pressButton", new { name }, ct);

    public Task LockAsync(CancellationToken ct = default) => PostAsync("/wda/lock", new { }, ct);

    /// <summary>Wakes the screen and swipes past the lock screen (the passcode, if any, still has to be entered).</summary>
    public Task UnlockAsync(CancellationToken ct = default) => PostAsync("/wda/unlock", new { }, ct);

    public async Task<bool> IsLockedAsync(CancellationToken ct = default) =>
        (await GetAsync("/wda/locked", ct))?["value"]?.GetValue<bool>() ?? false;

    /// <summary>Full-resolution PNG of the screen.</summary>
    public async Task<byte[]> ScreenshotPngAsync(CancellationToken ct = default)
    {
        var b64 = (await GetAsync("/screenshot", ct))?["value"]?.GetValue<string>()
                  ?? throw new IOException("WDA returned no screenshot");
        return Convert.FromBase64String(b64);
    }

    /// <summary>Opens a URL or deep link (https://…, maps://…, tel:…) on the phone.</summary>
    public Task OpenUrlAsync(string url, CancellationToken ct = default) =>
        PostAsync($"/session/{Sid}/url", new { url }, ct);

    private Task PerformTouchAsync(object[] steps, CancellationToken ct) =>
        PostAsync($"/session/{Sid}/actions", new
        {
            actions = new[]
            {
                new { type = "pointer", id = "finger1", parameters = new { pointerType = "touch" }, actions = steps },
            },
        }, ct);

    private static object Move(double x, double y, int ms) => new { type = "pointerMove", duration = ms, x, y };
    private static object Down() => new { type = "pointerDown", button = 0 };
    private static object Up() => new { type = "pointerUp", button = 0 };
    private static object Pause(int ms) => new { type = "pause", duration = ms };

    private string Sid => _sessionId ?? throw new InvalidOperationException("Call CreateSessionAsync first.");

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct);
        return await ReadAsync(res, ct);
    }

    private async Task<JsonNode?> PostAsync(string path, object body, CancellationToken ct)
    {
        // WDA rejects chunked bodies ("Transfer-Encoding is not supported"), and JsonContent
        // streams without a Content-Length, so serialize up front.
        using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync(path, content, ct);
        return await ReadAsync(res, ct);
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new IOException($"WDA {(int)res.StatusCode}: {json}");
        return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
    }

    public void Dispose() => _http.Dispose();
}
