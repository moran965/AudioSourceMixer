# Audio Source Mixer

[简体中文](README.zh-CN.md) · Version **1.0.0** · Windows 11 x64

Audio Source Mixer is an open-source Windows audio utility for per-application and user-enabled browser-tab volume, balance, EQ, metering, and output routing.

> This project was built through vibe coding and iterative AI-assisted development. Product requirements, design decisions, hands-on validation, and real-hardware testing are directed by a human maintainer. AI assistance is not a substitute for review, testing, or maintainer responsibility; see [AI development](AI_DEVELOPMENT.md).

![Audio Source Mixer interface](assets/generated/AudioSourceMixer-page.png)

## Features

- Windows application volume, mute, stereo balance, live peak meter, and per-app output routing.
- User-enabled Chrome/Edge tabs with 0–200% gain, balance, ten-band EQ, live level, and independent output selection.
- Explicit output authorization: the selected physical device must pass a low-volume test and exact effective-sink verification before its mapping can be saved.
- Manual ordering, drag-and-drop, hide/restore, profile memory, tray mode, and optional startup.
- Immediate Simplified Chinese / English switching across the app, tray, installer, uninstaller, and extension pages.
- Local processing with no analytics, advertising, remote code, or uploaded audio.

## Safe installation

Download `AudioSourceMixer-1.0.0-win-x64-setup.exe` only from this repository's [GitHub Releases](../../releases). Do not download repackaged installers from third-party sites. The per-user installer needs no administrator rights and defaults to `%LocalAppData%\Programs\AudioSourceMixer`.

Official binary releases are published only after trusted Authenticode signing. Check the Publisher shown by Windows and verify both signature and SHA-256 as described below. A source-only or Draft release is not an official binary release.

There is no portable edition. GitHub's automatically generated “Source code (zip)” and “Source code (tar.gz)” files are source archives, not runnable portable builds.

## Browser enhancement and output authorization

Open **Browser enhancement** in the app, then load the installed `BrowserExtension` directory as an unpacked extension in Chrome or Edge. The extension is not currently distributed through either browser store.

Start playback in a tab and click the extension action; capture is always user initiated. Choosing a non-default output opens a visible authorization page. A mapping is saved only after the requested device is still enumerated, the browser reports the exact requested effective `sinkId`, the test tone completes, and the same candidate is confirmed. Device changes invalidate stale test results. See the [User Guide](USER_GUIDE.md) and [browser limitations](docs/browser-tab-limitations.md).

## Language

Choose **Settings → Language → 简体中文 / English**. The change applies immediately and is retained. Existing pre-1.0 users without a language preference default to Simplified Chinese.

## Privacy and local data

Audio remains in local Windows/Chromium audio graphs and is never recorded or uploaded. Settings, profiles, recovery state, and logs are under `%LocalAppData%\AudioSourceMixer`. Browser mappings remain in the current browser profile. Read the [privacy notice](docs/privacy.md).

## Verify a download

Compare the installer hash with `SHA256SUMS.txt` from the same Release:

```powershell
Get-FileHash .\AudioSourceMixer-1.0.0-win-x64-setup.exe -Algorithm SHA256
```

Verify Authenticode status, publisher, and timestamp:

```powershell
Get-AuthenticodeSignature .\AudioSourceMixer-1.0.0-win-x64-setup.exe | Format-List Status,SignerCertificate,TimeStamperCertificate
```

The expected status for an official binary Release is `Valid`. A valid signature does not guarantee immediate SmartScreen reputation for a new publisher.

## Build and test

Requirements: Windows 11 x64, .NET 8 SDK specified by `global.json`, Node.js, Chrome, and Edge.

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release
.\scripts\package-installer.ps1 -Configuration Release
```

The final release gate additionally includes bilingual WPF smoke tests, Chrome/Edge runtime tests, real output-device checks, installer/repair/upgrade/uninstall matrices, repository audit, secret scanning, signing verification, SBOM, and provenance. See [building](docs/building.md), [testing](docs/testing.md), and [releasing](docs/releasing.md).

## Known limitations

- Browser enhancement requires Chromium `tabCapture`, offscreen Web Audio, `setSinkId`, and Native Messaging APIs.
- DRM media, navigation, browser suspension, drivers, Bluetooth reconnects, and physical endpoint routing require hands-on verification on the target hardware.
- Windows per-app routing may require the source application to recreate its stream.
- The extension must currently be loaded unpacked.

## Contributing, support, and security

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Use GitHub Issues for non-sensitive support and bugs, following [SUPPORT.md](SUPPORT.md). Do not post secrets, full logs, device IDs, browser profiles, URLs/page titles, or user-directory paths. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

Licensed under the [MIT License](LICENSE). Third-party notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
