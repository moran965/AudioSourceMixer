# Audio Source Mixer Tab Enhancement — Store Draft / 商店资料草案

Last updated / 最后更新：2026-08-22 · Version 1.0.0 · Not yet published / 尚未发布

## English listing

**Name:** Audio Source Mixer Tab Enhancement

**Short description:** Give user-selected Chrome or Edge tabs independent volume, balance, EQ, live level, and output-device control through the local Audio Source Mixer app.

**Detailed description:**

Audio Source Mixer Tab Enhancement works with the installed Windows desktop app. After you click the toolbar action, it captures only that current tab's audio and exposes independent 0–200% gain, stereo balance, a ten-band equalizer, live level, and output-device selection in the desktop mixer. Non-default outputs use a visible authorization page with a local test tone and explicit confirmation. Click the action again to stop.

The extension does not record, save, analyze, or upload audio or page content. It contains no ads, analytics, remote code, or broad host access. Output mappings and language choice stay in the current browser profile.

## 中文商店文案

**名称：** Audio Source Mixer 标签页增强

**简短说明：** 通过本机 Audio Source Mixer，为用户主动选择的 Chrome/Edge 标签页提供独立音量、平衡、EQ、实时电平和输出设备控制。

**详细说明：**

Audio Source Mixer 标签页增强与已安装的 Windows 桌面程序配合使用。只有点击工具栏图标后，扩展才捕获当前标签页音频，并在桌面混音器中提供独立 0–200% 增益、左右平衡、十段均衡器、实时电平和输出设备选择。非默认输出会打开可见授权页，由用户在本机试听并明确确认。再次点击图标即可停止。

扩展不录制、不保存、不分析或上传音频与网页内容；不包含广告、分析、远程代码或宽泛网站访问权限。输出映射和语言选择只保存在当前浏览器 profile。

## Permission justifications / 权限理由

| Permission | Justification |
| --- | --- |
| `activeTab` | Limits user-triggered work to the tab the user clicked. / 将用户触发的操作限制在当前标签页。 |
| `tabs` | Reads tab identity/title and tracks lifecycle for active enhanced sources. No page body is read. / 读取标签页身份、标题和生命周期，不读取页面正文。 |
| `tabCapture` | Captures the current tab only after the toolbar action. / 仅在点击图标后捕获当前标签页音频。 |
| `offscreen` | Runs the local Web Audio graph while the service worker sleeps. / 在 service worker 休眠时维持本地 Web Audio 图。 |
| `nativeMessaging` | Exchanges control state with the installed local desktop host. / 与已安装的本机桌面 Host 交换控制状态。 |
| `storage` | Stores onboarding, language, verified device mappings, and recoverable lifecycle state. / 保存引导、语言、已验证设备映射及可恢复生命周期状态。 |

No `host_permissions`, `audioCapture`, remote scripts, inline scripts, `eval`, cookies, analytics, or `storage.sync` are used.

## Privacy disclosure / 隐私披露

Privacy policies: [English](docs/privacy.md) · [简体中文](docs/privacy.zh-CN.md). Before store submission these files must be hosted at a stable public HTTPS URL and a public support/contact address must be supplied.

Data stays local. The tab title and origin without path/query are sent only to the Native Messaging Host on the same machine. Audio remains in memory and is never uploaded. Device mappings and the extension language remain in the current browser profile.

## Screenshot checklist / 截图清单

Create fresh screenshots at store-required dimensions in both languages:

1. Welcome page / 欢迎页。
2. Enhanced browser source card with live level / 带实时电平的浏览器增强来源。
3. Expanded ten-band EQ / 展开的十段 EQ。
4. Output authorization device selection / 输出授权设备选择。
5. Test-tone confirmation and mismatch warning / 试听确认与名称不匹配警告。
6. Verified mapping list and reauthorization controls / 已验证映射与重新授权。

Avoid personal tabs, account names, machine paths, device serials, notifications, and unrelated browser UI.

## Version history / 版本历史

| Version | Date | Summary | Status |
| --- | --- | --- | --- |
| 1.0.0 | 2026-08-22 | Full zh-CN/en-US localization, stable status/error codes, visible profile-local language switch, unchanged MV3 permissions / 完整中英本地化、稳定状态码、当前 profile 语言切换、权限不变 | Draft |
| 0.2.2 | 2026-08-21 | Authorization race fixes, output revalidation, independent 10 Hz tab levels / 授权竞态修复、输出重验证、独立 10 Hz 电平 | Local distribution only |

## Submission gates

- Zip only `src/AudioSourceMixer.BrowserExtension` contents; do not include repository files, tests, or desktop binaries.
- Validate all manifest `__MSG_*__` keys, icon dimensions, CSP behavior, and absence of remote code.
- Run Node tests plus isolated-profile Chrome and Edge onboarding, authorization, offscreen, service-worker, idle, update, and reload checks.
- Publish only after the privacy/support URLs and bilingual screenshots are public and final.
