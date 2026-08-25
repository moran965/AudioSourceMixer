# Security policy

## Supported version

Security fixes are provided for the latest published release and the current `main` branch.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting feature: **Security → Advisories → Report a vulnerability**. Do not open a public issue for a suspected vulnerability. If private reporting is not enabled, open a minimal issue asking the maintainer to enable a private channel without including exploit details or personal data.

Include the affected version/commit, impact, reproduction prerequisites, and a minimal redacted proof. Do not attach credentials, browser profiles, full logs, device identifiers, page titles/URLs, user-directory paths, audio recordings, or third-party personal data.

The maintainer will acknowledge a report when available, investigate it, and coordinate disclosure. No fixed response time or bounty is promised. Do not test against systems or accounts you do not own or have permission to use.

Binary releases disclose their actual Authenticode state and publish SHA-256 plus GitHub Artifact Attestation. The initial v1.0.0 release may use the explicitly approved unsigned fallback and therefore show `NotSigned`/unknown publisher; this must be stated prominently on the matching Release page. GitHub provenance is not Authenticode. Report any signature state, publisher, hash, or provenance that differs from the Release description before running the file.
