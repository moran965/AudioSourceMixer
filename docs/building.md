# Building from source

## Requirements

- Windows 11 x64
- .NET 8 SDK version compatible with `global.json`
- Node.js with the built-in test runner
- PowerShell 5.1 or later
- Chrome and Edge for browser runtime gates

No production NuGet or npm packages are required. Test-only NuGet dependencies are documented in `THIRD_PARTY_NOTICES.md`.

## Commands

```powershell
dotnet restore .\AudioSourceMixer.sln
dotnet build .\AudioSourceMixer.sln --configuration Release --no-restore
dotnet test .\AudioSourceMixer.sln --configuration Release --no-build --no-restore
Push-Location .\src\AudioSourceMixer.BrowserExtension
npm test
Pop-Location
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\audit-repository.ps1
```

Build a fresh installer with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-installer.ps1 -Configuration Release
```

Generated files stay under ignored `artifacts/` and staging directories. The installer payload is assembled from new self-contained publishes and `packaging/runtime-allowlist.json`; it does not use a pre-existing installer merely because one exists.

Do not commit `bin`, `obj`, `artifacts`, browser profiles, logs, certificates, PDB files, or installed payloads. The release process is Windows-specific because it exercises real WPF, Core Audio, registry, Native Messaging, installer, and uninstaller behavior.
