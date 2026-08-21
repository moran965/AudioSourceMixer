# Audio Source Mixer 1.0.0 release verification

This document records the release gates for the 1.0.0 candidate. Historical release notes remain in `CHANGELOG.md`; current instructions describe the installer-only distribution.

## Why the UI smoke is real

The old background-only smoke path could exit before `MainWindow.Show()` and before an `ItemsControl` generated an item container, so a failing `DataTemplate` binding could return exit code 0. The current `--ui-smoke-test` creates deterministic Windows and browser sources, calls the real `Show()` path, waits through Loaded, binding, layout, Render/ApplicationIdle, verifies at least one generated container and instantiated EQ/peak templates, audits effective binding modes, changes the peak value, and checks the rendered indicator width. Dispatcher, XAML, binding, unobserved-task, and asynchronous failures produce a nonzero exit code; cleanup restores audio and closes the process.

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
- fresh Chinese and English install, default/space/Chinese paths, repair, injected rollback, startup on/off, background tray, Native Messaging registration, no-argument localized uninstaller, running-app uninstall, preserve/delete user data, and 0.2.2 → 1.0.0 settings-preserving upgrade.

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

Final verification was completed on 2026-08-22 on Windows 11 x64:

- Release restore/build: exit 0, 0 warnings, 0 errors.
- .NET tests: 151/151 passed (Core 93, Native Host 2, Windows Audio 15, Desktop/WPF 29, Installer 12). Test projects run serially so independent WPF hosts cannot steal keyboard focus from one another.
- browser-extension Node tests: 48/48 passed. Chrome and Edge Web Audio EQ checks passed (`volumeRatio=0.5`, `leftLeakRatio=0`); each browser completed four authorization operations with zero runtime exceptions, log errors, unhandled rejections, or service-worker errors.
- source Release UI smoke: exit 0. Its real WaveOut meter run collected 71 samples, reached a raw/smoothed peak of 0.3662 and a 49.33-DIP indicator, then returned to zero.
- bilingual screenshot capture: 13 final PNGs per language in `artifacts/ui-1.0.0-zh-CN` and `artifacts/ui-1.0.0-en-US`; real mouse/keyboard hide, restore, reorder, drag, auto-scroll, Drop, and Escape runs produced eight captures per language in the matching `ui-interaction-1.0.0-*-installed` directories.
- installer matrix: exit 0. All 24 recorded gates passed, including fresh zh-CN/en-US installs, default/space/Chinese paths, same-version repair, injected rollback, startup/background modes, localized no-argument and silent uninstall, preserve/delete user data, running-app uninstall, browser setup, and 0.2.2 → 1.0.0 migration.
- normal installed launch used a controlled WaveOut source and completed with `WindowShown=True; Sources=13; MaterializedItems=13`; the installed live meter collected 72 samples, reached 0.3662/49.33 DIP, returned to zero, and exited through the normal audio-restore path.
- Release publish, installer payload, and installed `AudioSourceMixer.exe` SHA-256: `DEEEA7F9959B91AC8EFC3A0599A75A623773E6D7945FC46CC4BBFA1672EC932A` (all equal). The installed runtime allowlist contains 33 files.
- final setup: `artifacts/AudioSourceMixer-1.0.0-win-x64-setup.exe`, 257,293,921 bytes, SHA-256 `F76CF0018B0D951CE36FE76942494451E2F0A4395588C1600F08DA82713021E7`.

The machine-readable source of truth is `artifacts/AudioSourceMixer-1.0.0-build-manifest.json`. It contains the publish/payload/installed inventories and each installer verification result.
