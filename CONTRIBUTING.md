# Contributing

Thank you for helping improve Audio Source Mixer. By participating, you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before filing an issue

Search existing issues and read [SUPPORT.md](SUPPORT.md). Remove personal data before sharing diagnostics. Never post credentials, full logs, raw Windows endpoint/browser device IDs, browser profiles, page titles or URLs, or user-directory paths.

## Development workflow

1. Fork the repository and create a focused branch.
2. Use Windows 11 x64, the .NET SDK from `global.json`, and a supported Node.js version.
3. Run `dotnet restore AudioSourceMixer.sln`, `dotnet build AudioSourceMixer.sln -c Release`, `dotnet test AudioSourceMixer.sln -c Release --no-build`, and `npm test` in `src/AudioSourceMixer.BrowserExtension`.
4. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/audit-repository.ps1`.
5. Describe behavior changes, tests, privacy impact, and hardware evidence in the pull request.

Keep changes small and preserve rollback behavior. Do not add drivers, remote code, telemetry, or production dependencies without an explicit design and license review. WPF tests that materialize UI must run on STA and capture binding errors. Browser routing changes need strict effective-sink tests and must not change the Windows default device.

AI-assisted contributions are welcome under [AI_DEVELOPMENT.md](AI_DEVELOPMENT.md); the contributor remains accountable for review, licenses, and testing.

The project is MIT-licensed. By submitting a contribution, you agree that it may be distributed under that license and that you have the right to submit it.
