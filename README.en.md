# DualSense Wired for Windows

[Русский](README.md)

A DualSense connected over **Bluetooth** appears to games as connected **by cable**: the
**controller speaker**, **HD haptics** and **adaptive triggers** work in games that only enable
them for a wired controller (tested with Marvel's Spider-Man Remastered, Miles Morales and Helldivers 2).

This is the Windows version of the Steam Deck plugin
[decky-dualsense-wired](https://github.com/mrFunkill/decky-dualsense-wired).

## Features

- A virtual wired DualSense with the same audio device as the real controller over USB. Games
  send speaker sounds and HD haptics to it by themselves.
- Audio and haptics go to the controller over Bluetooth, as do adaptive triggers, the lightbar
  and regular rumble.
- The Bluetooth controller is hidden from Steam and games (HidHide), so there is only one controller.
- Settings: speaker volume, haptics strength, audio buffer (delay vs. robustness), adaptive
  triggers on/off.
- Layout: remap or disable buttons, swap sticks, dead zones, inverted right stick Y.
- Battery level, low battery warning, tray icon, start with Windows.
- English and Russian interface (follows Windows), light and dark theme.

## Requirements

- Windows 11 or Windows 10 2004+ (tested on Windows 11), x64.
- Bluetooth on the PC, a DualSense (DualSense Edge should work but is untested).
- Games that support DualSense speaker/HD haptics over a cable.

## Installation

1. Download `DualSenseWired-X.Y.Z-setup.exe` from
   [Releases](https://github.com/mrFunkill/DualSenseWired/releases) and run it.
2. If missing, the setup installs:
   - [usbip-win2](https://github.com/vadimgrn/usbip-win2) 0.9.8.1, the virtual USB device driver
     (older versions are replaced: 0.9.7.8 has a bug that causes a BSOD);
   - [HidHide](https://github.com/nefarius/HidHide), which hides the Bluetooth controller from games.

   A restart may be needed after the drivers are installed.
3. Pair the DualSense in Windows Bluetooth settings (hold **Create + PS** until the light blinks fast).
4. Start DualSense Wired. Steam should show a single DualSense, connected "over USB".

Keep your speakers or headphones as the default Windows output device: games find the
controller's speaker ("DualSense Wireless Controller") on their own.

If DSX is running, close it: two apps cannot drive the controller at the same time.

## How it works

- The app runs a USB/IP server with a virtual DualSense (the real USB descriptors: HID + 4-channel
  48 kHz audio), and usbip-win2 attaches it to Windows as a USB device.
- Reports of the Bluetooth controller are translated to the USB format and back.
- Audio that a game plays to the controller is re-encoded: channels 1-2 (speaker) to Opus,
  channels 3-4 (actuators) to the 3 kHz haptics stream, and sent over Bluetooth.
- Independent implementation: the protocol was studied from the behaviour of the author's own
  controller and public sources; no code of third-party closed-source programs was used.

## Limitations

- One controller at a time.
- The controller's microphone and headphone jack are not supported over Bluetooth.
- Keyboard and mouse actions in the layout are planned.

## Uninstalling

Settings → Apps → DualSense Wired → Uninstall. The app stops and gives the Bluetooth controller
back to Steam. Remove usbip-win2 and HidHide separately if you no longer need them.

## Building from source

Requires the .NET 8 SDK and Inno Setup 6.

```powershell
dotnet build DualSenseWired\DualSenseWired.csproj        # the app (running it needs administrator rights)
# the setup: put the usbip-win2 and HidHide installers into installer\deps\ (see installer\build.ps1)
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

Log: `%LOCALAPPDATA%\DualSenseWired\DualSenseWired.log`, settings:
`%APPDATA%\DualSenseWired\settings.json`.

## License

[BSD 3-Clause](LICENSE), by mrFunkill. Third-party components (Concentus, WebView2 SDK, .NET,
usbip-win2, HidHide): see [installer/license.txt](installer/license.txt).

DualSense is a trademark of Sony Interactive Entertainment Inc. This project is not affiliated with Sony.
