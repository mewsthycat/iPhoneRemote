# iPhone Remote

See and control your iPhone from a Windows PC over USB. The phone's screen shows up in a window
on your PC: click to tap, drag to swipe, and type with your PC keyboard. **No Mac and no jailbreak
needed.**

- Live screen at up to 30 fps over the USB cable
- Mouse → taps, long presses, swipes and scrolling. Keyboard → typing.
- One-click Home, app switcher, Notification Center, Control Center, lock/wake and volume
- Screenshots (saved and copied to the clipboard), paste PC text into the phone, open links on the phone
- Starts everything it needs by itself and reconnects if the cable blips

Tested on an iPhone 14 with iOS 18.6 and Windows 11. It should work on any iPhone running iOS 17 or later.

> **How it works, in one paragraph:** WebDriverAgent (WDA), an open-source test agent built on
> Apple's XCTest framework, runs on the phone. It's the same tool Appium uses for app testing. It streams the screen and
> accepts touch and keyboard commands. This app reaches it over USB through Apple's driver on
> Windows, and starts it using [go-ios](https://github.com/danielpaulus/go-ios).
> You sign WDA once with your own Apple ID, using Sideloadly.

---

## Requirements

On the **PC** (Windows 10 or 11):

| What | Why | Get it |
|---|---|---|
| **.NET 10 SDK** | Builds and runs the app | <https://dotnet.microsoft.com/download> |
| **iTunes** (the version from apple.com, *not* the Microsoft Store one) | Apple's USB driver, plus what Sideloadly needs | <https://www.apple.com/itunes/> |
| **Python 3** + `pymobiledevice3` | Mounts Apple's developer disk image | <https://www.python.org/downloads/>, then `py -m pip install pymobiledevice3` |
| **Sideloadly** | Signs and installs WDA on the phone with your Apple ID | <https://sideloadly.io> |
| **Git** | To download this repo | <https://git-scm.com> |

On the **iPhone**: iOS 17 or later, a USB cable, and an Apple ID. A free one works, with one
catch covered [below](#every-7-days-free-apple-id).

---

## Setup (one time, about 15 minutes)

### 1. Download the project
```powershell
git clone https://github.com/mewsthycat/iPhoneRemote.git
cd iPhoneRemote
```

### 2. Connect and trust the phone
Plug the iPhone in and unlock it. When it asks **"Trust This Computer?"**, tap **Trust** and enter your passcode.

### 3. Download the helper tools and build WDA's app file
In PowerShell, from the project folder:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup-wda.ps1 -Stage tools
powershell -ExecutionPolicy Bypass -File scripts\setup-wda.ps1 -Stage package
```
- `tools` downloads go-ios into `tools\go-ios\`.
- `package` downloads Appium's prebuilt WebDriverAgent and turns it into `tools\WDA.ipa`.

### 4. Install WDA on the phone with Sideloadly
1. Open **Sideloadly** with the iPhone connected.
2. Drag `tools\WDA.ipa` into it, enter your Apple ID, and click **Start**.
3. On the iPhone, open **Settings → General → VPN & Device Management**, tap your Apple ID under
   "Developer App", and tap **Trust**.

### 5. Turn on Developer Mode
On the iPhone: **Settings → Privacy & Security → Developer Mode → On**. The phone restarts.
After it boots, confirm by tapping **Turn On**.

> This option only appears after step 4 has installed a developer-signed app.

### 6. First launch
Double-click **`iPhoneRemote.cmd`**. The first launch builds the app, which takes about 30 seconds.
The app then opens a tunnel to the phone, mounts the developer image, and starts WDA.

### 7. Turn on UI Automation
Now that the developer image is mounted, the iPhone has a new menu: **Settings → Developer**.
Turn on **Enable UI Automation**. Then press **F5** in the app, or click the gear icon → **Reconnect**.

The screen should appear, and clicks should now reach the phone. 🎉

### 8. (Optional) Desktop shortcut
```powershell
.\iPhoneRemote.cmd -CreateShortcut
```

---

## Everyday use
1. Plug in the iPhone and unlock it.
2. Double-click **`iPhoneRemote.cmd`** or the Desktop shortcut.
3. After about 10 seconds, the dot in the title bar turns green and the screen is live.

Closing the window stops everything it started.

### Every 7 days (free Apple ID)
Apple makes apps signed with a free Apple ID expire after 7 days. When that happens, the app
says *"WebDriverAgent didn't start"*. To fix it, open Sideloadly, drag in `tools\WDA.ipa`, and
click **Start** again. That takes about a minute. A paid Apple Developer account ($99/year)
makes the signature last a year.

---

## Controls

| Mouse / keyboard | On the iPhone |
|---|---|
| Click | Tap |
| Click and hold (≥ 0.5 s), or right-click | Long press |
| Drag | Swipe, at the speed you dragged |
| Mouse wheel | Scroll |
| Type, Backspace, Enter, Tab | Types into the focused text field |
| **Esc** | Home |
| **Ctrl+E** | App switcher |
| **Ctrl+N** / **Ctrl+K** | Notification Center / Control Center |
| **Ctrl+L** | Lock, or wake if locked |
| **Page Up** / **Page Down** | Volume up / down |
| **Ctrl+S** | Screenshot: saved to `Pictures\iPhone Remote` and copied to the clipboard |
| **Ctrl+V** | Types the text on your PC clipboard into the phone |
| **Ctrl+O** | Opens a link (or `tel:`, `maps:` and similar) on the phone |
| **Ctrl+T** | Keeps the window on top |
| **F5** | Reconnects |

Every action also has a button in the toolbar on the right. The **gear** opens settings:
- Stream quality: *Smooth* (30 fps, default), *Sharp* (full resolution) or *Saver* (less CPU and battery)
- Show touches
- Keep on top

Settings are saved to `%APPDATA%\iPhoneRemote\settings.json`.

**About latency:** the picture is near real-time, but each tap takes about 0.6 s to reach the
phone. WDA, the tool on the phone that performs the taps, is built for testing, not gaming. Busy
apps like video feeds can be slower still. The app shows a ripple the moment you click, so you
know the click registered.

---

## Troubleshooting

| What you see | Fix |
|---|---|
| "Plug in your iPhone and unlock it." stays up | Use a data cable, not a charge-only one. Unlock the phone. Install iTunes from apple.com so Apple's USB driver is present. |
| "Cannot reach usbmux at 127.0.0.1:27015" | Apple Mobile Device Service isn't running. Install or repair iTunes, or start the service in `services.msc`. |
| "WebDriverAgent isn't installed on the iPhone" | Do [setup step 4](#4-install-wda-on-the-phone-with-sideloadly). |
| "WebDriverAgent didn't start" | Most often the 7-day signature has expired, so re-sign in Sideloadly. Also check that Developer Mode is on. Details are in `tools\logs\runwda.log`. |
| Clicks show a ripple but a toast says *"Not authorized for performing UI testing actions"* | Turn on Settings → Developer → **Enable UI Automation**. iOS sometimes turns it off after an update. |
| "Couldn't mount the developer image" | Run `py -m pip install pymobiledevice3`, then press F5. |
| A toast says "The phone took N s to respond" | The app in front is busy (video feeds, games). Go Home and it speeds up. |
| "go-ios is missing" | Run `scripts\setup-wda.ps1 -Stage tools`. |
| The build fails | Make sure the .NET 10 SDK is installed (`dotnet --list-sdks`). The build log is in `%TEMP%\iphoneremote-build.log`. |

---

## Limitations
- **USB only.** Bluetooth can't carry the video, and Wi-Fi isn't supported yet.
- **Taps take about 0.6 s.** That's fine for apps and browsing, but too slow for games.
- **Portrait only.** In landscape, click positions don't line up yet.
- **A free Apple ID means re-signing WDA every 7 days.**
- **No audio.** WDA doesn't carry sound.

---

## How it works

```
┌──────────────── Windows PC ───────────────────────────────────────────┐
│  iPhone Remote (C# / WPF)                                             │
│   ├─ Viewer: MJPEG frames → newest-frame-only rendering               │
│   ├─ Input: window px → iOS points → one ordered queue → WDA HTTP     │
│   └─ IPhoneRemote.Core                                                │
│        UsbmuxClient ──TCP 127.0.0.1:27015──► Apple Mobile Device Svc  │
│        LockdownClient (62078) · PortForwarder (8100, 9100)            │
│   Started on demand: go-ios (iOS 17+ tunnel, `runwda`),               │
│                      pymobiledevice3 (developer image mount)          │
└─────────────────────────────┬─────────────────────────────────────────┘
                     USB (usbmux: TCP multiplexed over the cable)
┌─────────────────────────────▼──── iPhone ─────────────────────────────┐
│  lockdownd · WebDriverAgentRunner (XCTest): HTTP :8100, MJPEG :9100   │
└───────────────────────────────────────────────────────────────────────┘
```

- **USB on Windows.** There's no `usbmuxd` on Windows. Apple Mobile Device Service, installed with
  iTunes, speaks the same protocol on `127.0.0.1:27015`: a 16-byte header plus an XML plist. A `Connect`
  request turns the socket into a raw TCP pipe to a port on the phone. `PortForwarder` exposes WDA's
  ports 8100 (HTTP) and 9100 (MJPEG) on `localhost` this way.
- **iOS 17+.** Developer services moved behind a CoreDevice tunnel. go-ios runs it as a userspace
  tunnel, so it needs no admin rights and no wintun driver.
- **Developer image.** go-ios can't fetch Apple's signature for the image on Windows: Apple's root CA
  isn't in the Windows certificate store. The app falls back to `pymobiledevice3`, which uses
  plain HTTP for that request. The phone verifies Apple's signature itself.
- **Video.** WDA's MJPEG server streams JPEG frames. The reader scans for JPEG start and end markers
  with vectorized search. Frames are decoded off the UI thread, and only the newest one is drawn.
- **Input.** Mouse positions map to iOS points. Inputs go through a single ordered queue of W3C
  pointer actions. Keystrokes are batched while a request is in flight.

### Project layout
```
iPhoneRemote.cmd               Double-click launcher (calls scripts\launch.ps1)
scripts\launch.ps1             Builds if sources changed, starts the app, -CreateShortcut
scripts\setup-wda.ps1          One-time setup: go-ios download, WDA.ipa packaging
src\IPhoneRemote.Core\         usbmux, lockdown (incl. TLS session), port forwarding, WDA client, MJPEG reader
src\IPhoneRemote.Viewer\       The WPF app: UI, input mapping, WdaLauncher (tunnel / image / runwda)
src\IPhoneRemote.Cli\          `iremote` test tool: list, watch, info, session, forward, wda, tap
Directory.Build.props          Moves bin/obj to a short path when the checkout is deeply nested
tools\                         (not in git) go-ios, WDA.ipa, developer images, logs
```

### Command-line test tool
```powershell
dotnet run --project src/IPhoneRemote.Cli -- list      # devices Apple's USB service can see
dotnet run --project src/IPhoneRemote.Cli -- info      # name, model, iOS version
dotnet run --project src/IPhoneRemote.Cli -- session   # TLS lockdown session (proves the phone trusts this PC)
dotnet run --project src/IPhoneRemote.Cli -- wda       # needs WDA running: status, screen size, stream fps
```

For development, run the viewer with `dotnet run --project src/IPhoneRemote.Viewer`.

---

## Credits
- [WebDriverAgent](https://github.com/appium/WebDriverAgent) (Appium): the on-device agent
- [go-ios](https://github.com/danielpaulus/go-ios): iOS 17+ tunnel and launching WDA from Windows
- [pymobiledevice3](https://github.com/doronz88/pymobiledevice3): developer image mounting
- [plist-cil](https://github.com/claunia/plist-cil): property list parsing
- [Sideloadly](https://sideloadly.io): signing with a regular Apple ID

## License
[MIT](LICENSE). The tools the setup script downloads (go-ios, WebDriverAgent, Apple's developer
disk image) keep their own licenses.

Not affiliated with or endorsed by Apple. iPhone and iOS are trademarks of Apple Inc.
