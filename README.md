# Audio Source Mixer

[简体中文](README.zh-CN.md) · Version **1.0.0**

Audio Source Mixer is a local Windows 11 x64 mixer for independent application and browser-tab audio control. It combines Windows Core Audio sessions with user-enabled Chrome or Edge tabs in one WPF interface.

## Features

- Windows application volume, mute, stereo balance, live peak meter, and per-app output routing.
- User-enabled Chrome/Edge tabs with 0–200% gain, balance, ten-band EQ, live level, and independent output selection.
- Explicit browser output authorization with a test tone, confirmation, reauthorization, and mapping management.
- Manual ordering, animated drag-and-drop, hide/restore, profile memory, automatic profile application, tray mode, and optional startup.
- Immediate Simplified Chinese / English switching across the desktop UI, tray, installer, uninstaller, and extension pages.
- Local-only processing: no analytics, advertising, remote code, or uploaded audio.

## Install

Download `AudioSourceMixer-1.0.0-win-x64-setup.exe` from GitHub Releases and run it. The per-user installer requires no administrator rights and defaults to `%LocalAppData%\Programs\AudioSourceMixer`. It can create a desktop shortcut, enable startup, and open the optional browser setup page.

The installer registers the Native Messaging Host for Chrome and Edge. There is no supported standalone portable distribution.

To uninstall, use Windows Installed apps or run `AudioSourceMixer.Uninstall.exe` in the install directory. User settings are retained by default; select the removal option when you explicitly want them deleted.

## Browser enhancement

Open **Browser enhancement** in the app, open the Chrome or Edge extension management page, enable Developer mode, and choose **Load unpacked**. Select the installed `BrowserExtension` directory shown by the app. The extension is not yet published in either browser store.

Start audio in a tab and click the extension action. Capture is always user initiated. Selecting a non-default output opens a visible authorization page where the physical device must be tested and confirmed.

## Build and test

Requirements: Windows 11 x64, .NET 8 SDK, Node.js, Chrome, and Edge.

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release
.\scripts\package-installer.ps1 -Configuration Release
.\scripts\verify-installer.ps1 -BaselineInstallerPath .\artifacts\AudioSourceMixer-0.2.2-win-x64-setup.exe
```

`scripts/build-all.ps1` runs the release gates and produces only the installer and machine-readable verification manifest. The installer payload is assembled directly from fresh Release publishes and the strict runtime allowlist.

See [User Guide](USER_GUIDE.md), [Testing](docs/testing.md), [Architecture](docs/architecture.md), [Privacy](docs/privacy.md), and [Changelog](CHANGELOG.md).

## Local data

Settings, profiles, rollback state, and logs are stored under `%LocalAppData%\AudioSourceMixer`. Browser output mappings and extension language preferences stay in the current browser profile through `chrome.storage.local`; active transient state uses `chrome.storage.session`.

## Known limits

- Browser enhancement requires Chromium APIs including `tabCapture`, offscreen Web Audio, `setSinkId`, and Native Messaging.
- Protected/DRM media, navigation, suspended tabs, device driver behavior, and Bluetooth reconnect behavior can require manual verification on the actual hardware.
- Windows per-app routing may report that a stream restart is required; pause/resume playback or restart that application.

Licensed under the repository [LICENSE](LICENSE).
