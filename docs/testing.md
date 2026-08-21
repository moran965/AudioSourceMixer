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

The final counts, artifact size, SHA-256 values, installation matrix results, and screenshot locations are written here after the final build so the report never claims a gate that was not actually executed. The machine-readable source of truth is `artifacts/AudioSourceMixer-1.0.0-build-manifest.json`.
