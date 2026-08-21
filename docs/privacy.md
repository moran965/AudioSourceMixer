# Audio Source Mixer Privacy Notice

[简体中文](privacy.zh-CN.md) · Last updated: 2026-08-22

Audio Source Mixer does not send personal data, browsing history, page content, or audio to the developers, advertisers, analytics providers, or any third-party server.

The extension processes a tab only after the user clicks its toolbar action. On the same computer it handles the tab title, site origin without path/query, control state, output selection, and live level so the desktop mixer can display and control that source. Audio is processed in memory and is not recorded, saved, or uploaded.

Output mappings, onboarding state, and the extension language are stored in `chrome.storage.local` for the current browser profile. Active short-lived tab state is stored in `chrome.storage.session`. Native Messaging is restricted to explicitly configured extension IDs and communicates only with the installed local host.

The desktop app stores settings, source profiles, rollback recovery state, and logs under `%LocalAppData%\AudioSourceMixer`. Uninstall retains this data unless the user explicitly requests deletion.

The project uses no cookies, analytics, telemetry, advertising, remote scripts, `eval`, or `chrome.storage.sync`. Users can clear mappings from the extension authorization page, remove browser extension data, or select user-data removal during desktop uninstall.
