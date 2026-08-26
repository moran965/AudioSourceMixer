# Release process

Only a human maintainer may approve a release. The version is centralized in `Directory.Build.props`; the desktop file version, extension manifest, installer display version, changelog, and artifact names must agree.

## Required gates

1. Clean, reviewed tag commit and complete reachable-history secret scan.
2. Release restore/build with zero warnings and errors; all .NET and Node tests pass.
3. Bilingual WPF UI smoke and installer/uninstaller matrices pass, including default/custom paths, repair, running-app uninstall, Native Messaging cleanup, and the supported previous-version upgrade.
4. Chrome and Edge authorization pages have no console/service-worker/unhandled errors.
5. Maintainer acceptance records the exact physical-output scope tested. API `sinkId` equality is necessary but is not physical-output proof, and untested disconnect/reconnect, DRM, lifecycle, driver, and hardware combinations remain disclosed limitations.
6. Repository, license, privacy, runtime allowlist, and secret audits pass.
7. The selected signing mode is verified exactly: trusted modes require every installed PE and the final setup to be `Valid` with a real signer and RFC 3161 timestamp; the manually approved unsigned fallback requires every relevant PE to be `NotSigned` and prominent bilingual risk disclosure.

SignPath Foundation is the preferred free trusted-signing path. Its OSS conditions require an already released project and human application/approval, so it must not indefinitely block the first release. If no free trusted signing is immediately available, v1.0.0 may be published unsigned only through the workflow's explicit `unsigned` selection, boolean opt-in, exact risk-confirmation text, and maintainer-approved `release` environment. The release must warn that Windows may show an unknown publisher or SmartScreen prompt, and that GitHub Artifact Attestation is not Authenticode. A later trusted binary must use a new patch version rather than silently replacing v1.0.0 assets.

The OSS application was submitted on 2026-08-27 and is awaiting SignPath Foundation review. Do not generate a SignPath API token or populate repository signing variables until the Foundation has approved the project and provisioned the real project, artifact configurations, certificate, and production signing policies. Approval must be integrated in a new patch release; the published v1.0.0 assets remain immutable and unsigned.

The public [Code signing policy](../CODE_SIGNING_POLICY.md) identifies the actual GitHub account roles, privacy links, automated-build boundary, and human approval rules required by the SignPath Foundation OSS program.

## GitHub workflow

`.github/workflows/release.yml` runs only by explicit manual dispatch for an existing annotated tag. Inputs bind the tag, hardware attestation, publish decision, signing mode, unsigned opt-in, and an exact risk-confirmation phrase. `signpath` is enabled only after a real SignPath Foundation configuration exists; `azure` remains an optional future OIDC path and must not create paid resources; `unsigned` fails unless its acknowledgements match exactly. Every mode rebuilds and tests the tag, scans the complete history, creates `SHA256SUMS.txt`, an SPDX JSON SBOM and GitHub build provenance, and dynamically records the actual signature state and final hash.

The release environment should require human approval. Protect `main` and `v*` tags, forbid force pushes/deletion, and require CI. Fork pull requests never receive signing credentials.

## Release assets

- `AudioSourceMixer-<version>-win-x64-setup.exe`
- `SHA256SUMS.txt`
- SPDX JSON SBOM
- GitHub Artifact Attestation / provenance
- bilingual release notes

Do not attach a portable ZIP, PDBs, tests, browser profiles, logs, certificates, or user data. GitHub source archives are source only.

The publisher's legal name, security/support contact, and Artifact Signing account/profile are maintainer-supplied release prerequisites; do not invent them. A new trusted publisher may still show a SmartScreen reputation warning.
