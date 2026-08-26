# Audio Source Mixer 1.0.0 release verification

This document records the release gates for the 1.0.0 candidate. Historical release notes remain in `CHANGELOG.md`; current instructions describe the installer-only distribution.

## Why the UI smoke is real

The old background-only smoke path could exit before `MainWindow.Show()` and before an `ItemsControl` generated an item container, so a failing `DataTemplate` binding could return exit code 0. The current `--ui-smoke-test` creates deterministic Windows and browser sources, calls the real `Show()` path, waits through Loaded, binding, layout, Render/ApplicationIdle, verifies at least one generated container and instantiated EQ/peak templates, audits effective binding modes, changes the peak value, and checks the rendered indicator width. Its diagnostic audio boundary is deterministic as well, so hosted Windows runners without a physical default endpoint still test the real WPF path instead of failing during unrelated Core Audio discovery. Dispatcher, XAML, binding, unobserved-task, and asynchronous failures produce a nonzero exit code; cleanup restores audio and closes the process.

## Automated commands

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release
.\scripts\package-installer.ps1 -Configuration Release
.\scripts\verify-installer.ps1 -BaselineInstallerPath .\artifacts\AudioSourceMixer-0.2.2-win-x64-setup.exe
.\scripts\verify-browser-runtime.ps1 -Browser Both
```

`scripts/build-all.ps1` composes those gates. It produces no standalone portable directory or archive.

The automated suite covers:

- centralized version/file-version assertions and settings schema 8 migration;
- exact, non-empty zh-CN/en-US resource parity and no key/placeholder leakage;
- C#/XAML hard-coded user-text audit, explicit WPF binding modes, DataTemplate creation, peak rendering, and runtime language switching without audio-service replacement;
- Core Audio/session identity, routing state, rollback journal, real WaveOut session meter, and Native Host protocol tests;
- MV3 locale references, real icon files, no inline/remote scripts, stable protocol codes, authorization transactions, mapping storage, EQ, offscreen routing, service-worker recovery, and no unhandled rejections;
- strict Release publish → installer runtime payload → installed file allowlist and SHA-256 equality;
- fresh Chinese and English install, default/space/Chinese paths, repair, injected rollback, startup registration/cleanup, Native Messaging registration, no-argument localized uninstaller, preserve/delete user data, and 0.2.2 → 1.0.0 settings-preserving upgrade. Real-audio background tray startup, explicit browser-setup launch, running-app graceful uninstall, ordinary visible launch, and the installed live meter run only when `AUDIO_SOURCE_MIXER_HARDWARE_TEST=1`; endpoint-less hosted runners record those gates as not executed instead of misreporting the expected audio-startup error path as a product failure or a pass.

## Maintainer-only browser and audio tools

These tools are intentionally kept out of CI because they require a visible desktop, installed browsers, user gestures, or specific physical endpoints. Their reports belong under the ignored `artifacts/` directory and must not contain raw device identifiers.

The live default-endpoint/session probe in `AudioSourceMixer.WindowsAudio.Tests` and the hardware-dependent installer gates follow the same rule: set `AUDIO_SOURCE_MIXER_HARDWARE_TEST=1` only on a Windows machine with an active default render endpoint. Hosted CI still runs all deterministic Windows Audio and installed WPF UI checks, but does not treat the absence of physical audio hardware as a product failure. A Release workflow may record the maintainer's separately completed hardware acceptance as attested; attestation never relabels an unexecuted hosted-runner check as `passed`.

- `tools/browser-route-matrix/server.mjs` serves two user-started deterministic tone tabs for manual independent-tab routing checks: `node .\tools\browser-route-matrix\server.mjs 8765`, then open `http://127.0.0.1:8765/?label=A&frequency=440` and `http://127.0.0.1:8765/?label=B&frequency=880`.
- `scripts/browser-sink-hardware-probe.mjs` exercises the extension's strict authorization test-tone path through an isolated Chromium debugging port. Pass only local label fragments on the command line and publish only hashed endpoint evidence.
- `scripts/verify-browser-management-pages.ps1` invokes the real WPF Chrome/Edge setup buttons with Windows UI Automation and confirms the actual management-page addresses. It is a desktop interaction check, not a headless unit test.
- `AudioSourceMixer.CapabilityProbe` supplies the controlled WaveOut source and Core Audio endpoint-meter sampling used by live-meter and installer verification. It is part of the solution and must remain buildable.
- `tests/audio/short-loop.wav` is the only checked-in audio fixture. It is deterministic synthetic PCM used by `scripts/common.ps1` and `scripts/verify-installer.ps1`; regenerate it with `scripts/generate-test-audio.ps1`.

## Bilingual UI capture

Run the installed Release executable with the diagnostic source set:

```powershell
& $exe --ui-smoke-test --language zh-CN --ui-screenshot-dir .\artifacts\ui-1.0.0-zh-CN
& $exe --ui-smoke-test --language en-US --ui-screenshot-dir .\artifacts\ui-1.0.0-en-US
.\scripts\verify-ui-interactions.ps1 -Executable $exe -Language zh-CN
.\scripts\verify-ui-interactions.ps1 -Executable $exe -Language en-US
```

Each language capture includes ordinary and long browser sources, expanded EQ, browser setup, settings at 880×600/1240×820/1600×900/1920×1080, 100/125/150/200% rendering, a maximized window, and the minimum window. The interaction run covers hide/restore popup, ordering menu, real mouse drag, adjacent animation, edge auto-scroll, Drop, and Escape cleanup. Tray labels are additionally exercised by localization/WPF tests and normal installed launch.

## Release result

Local QA verification was completed on 2026-08-22 on Windows 11 x64. These values identify that local candidate only; they are not permanent hashes for files rebuilt by GitHub Actions. The sole checksum authority for a published download is `SHA256SUMS.txt` attached to the same GitHub Release.

- Release restore/build: exit 0, 0 warnings, 0 errors.
- .NET tests: 151/151 passed (Core 93, Native Host 2, Windows Audio 15, Desktop/WPF 29, Installer 12). Test projects run serially so independent WPF hosts cannot steal keyboard focus from one another.
- browser-extension Node tests: 56/56 passed. Chrome and Edge Web Audio EQ checks passed (`volumeRatio=0.5`, `leftLeakRatio=0`); each browser completed four authorization operations with zero runtime exceptions, log errors, unhandled rejections, or service-worker errors.
- source Release UI smoke: exit 0. Its real WaveOut meter run collected 71 samples, reached a raw/smoothed peak of 0.3662 and a 49.33-DIP indicator, then returned to zero.
- bilingual screenshot capture: 13 final PNGs per language in `artifacts/ui-1.0.0-zh-CN` and `artifacts/ui-1.0.0-en-US`; real mouse/keyboard hide, restore, reorder, drag, auto-scroll, Drop, and Escape runs produced eight captures per language in the matching `ui-interaction-1.0.0-*-installed` directories.
- installer matrix: exit 0. All 24 recorded gates passed, including fresh zh-CN/en-US installs, default/space/Chinese paths, same-version repair, injected rollback, startup/background modes, localized no-argument and silent uninstall, preserve/delete user data, running-app uninstall, browser setup, and 0.2.2 → 1.0.0 migration.
- normal installed launch used a controlled WaveOut source and completed with `WindowShown=True; Sources=13; MaterializedItems=13`; the installed live meter collected 72 samples, reached 0.3662/49.33 DIP, returned to zero, and exited through the normal audio-restore path.
- Local Release publish, installer payload, and installed `AudioSourceMixer.exe` SHA-256: `23FFE8CF79FBF09033CE4E2AA8015C01A83D25446566D0D57FA94FFA2E8A1EBF` (all equal). The installed runtime allowlist contains 33 files.
- Local QA setup: `artifacts/AudioSourceMixer-1.0.0-win-x64-setup.exe`, 257,298,017 bytes, SHA-256 `3C9E1E88920E76F1D71B5D6FF465C52264F9DF09A2F8033F960445F1524B92F2`, Authenticode `NotSigned`.

The local machine-readable evidence is `artifacts/AudioSourceMixer-1.0.0-build-manifest.json`. It contains the publish/payload/installed inventories and each installer verification result, but it is intentionally not committed or uploaded as a release asset.

## Maintainer hands-on acceptance

On 2026-08-25 the maintainer separately confirmed that the current browser-enhancement mode works in normal use. This acceptance covers the enabled extension's user-facing mixing and output-routing flow on the maintainer's current setup. It does not retroactively change `humanListeningConfirmed: false` in the 2026-08-22 automated endpoint-meter reports, and it does not claim exhaustive disconnect/reconnect, DRM, driver, browser-lifecycle, or hardware-combination coverage.
