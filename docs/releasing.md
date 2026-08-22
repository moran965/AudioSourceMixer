# Release process

Only a human maintainer may approve a release. The version is centralized in `Directory.Build.props`; the desktop file version, extension manifest, installer display version, changelog, and artifact names must agree.

## Required gates

1. Clean, reviewed tag commit and complete reachable-history secret scan.
2. Release restore/build with zero warnings and errors; all .NET and Node tests pass.
3. Bilingual WPF UI smoke and installer/uninstaller matrices pass, including default/custom paths, repair, running-app uninstall, Native Messaging cleanup, and the supported previous-version upgrade.
4. Chrome and Edge authorization pages have no console/service-worker/unhandled errors.
5. Hands-on hardware matrix proves default headphones → non-default speakers without changing the Windows default, disconnect/reconnect and reauthorization, saved mapping retest, and independent multi-tab routes. API `sinkId` equality is necessary but is not physical-output proof.
6. Repository, license, privacy, runtime allowlist, and secret audits pass.
7. Every installed PE and the final setup are signed by a trusted Authenticode publisher with SHA-256 and an RFC 3161 timestamp; signatures are rechecked after installation.

If trusted signing credentials are unavailable, the source repository may be public and a Draft Release may be prepared, but no unsigned setup is uploaded or published as an official 1.0.0 binary.

## GitHub workflow

`.github/workflows/release.yml` runs only for `v*` tags or an explicit manual dispatch. It fails closed when the signing environment is not configured. The signing job uses OIDC and Azure Artifact Signing, signs internal desktop/native-host executables before packaging, signs the final installer afterwards, verifies signatures, creates `SHA256SUMS.txt`, generates an SPDX SBOM, creates GitHub build provenance, and uploads only release-approved files.

The release environment should require human approval. Protect `main` and `v*` tags, forbid force pushes/deletion, and require CI. Fork pull requests never receive signing credentials.

## Release assets

- `AudioSourceMixer-<version>-win-x64-setup.exe`
- `SHA256SUMS.txt`
- SPDX JSON SBOM
- GitHub Artifact Attestation / provenance
- bilingual release notes

Do not attach a portable ZIP, PDBs, tests, browser profiles, logs, certificates, or user data. GitHub source archives are source only.

The publisher's legal name, security/support contact, and Artifact Signing account/profile are maintainer-supplied release prerequisites; do not invent them. A new trusted publisher may still show a SmartScreen reputation warning.
