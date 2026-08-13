# Third-Party Notices

运行时产品代码不依赖第三方 NuGet 库；Windows Audio COM interop 为本项目定义，桌面与安装器使用 .NET 8 框架库。

开发/测试依赖：

- .NET / WPF / Windows Forms — Microsoft，MIT License（部分 Windows 组件受对应系统许可约束）。
- xUnit.net 2.5.3 — .NET Foundation contributors，Apache License 2.0。
- Microsoft.NET.Test.Sdk 17.8.0 — Microsoft，MIT License。
- coverlet.collector 6.0.0 — Toni Solarin-Sodara 等贡献者，MIT License。

开发过程中采用了 GoogleChrome `modern-web-guidance` 仓库的 `chrome-extensions` agent skill（Apache License 2.0）作为实现指南。该 skill 不包含在发布包运行时中。

## EarTrumpet reference

The isolated `Windows.Media.Internal.AudioPolicyConfig` activation class, current/downlevel interface identifiers,
and persisted endpoint HSTRING layout were derived from the current EarTrumpet source at commit
`aa894e51c22f5f9a939b31b224c4d2d3e163416e` (File-New-Project/EarTrumpet). EarTrumpet is licensed under the
MIT License except for the named entities excluded by its repository license; Audio Source Mixer does not use
those excluded names, logos, or branding. The production interop in this project is an independently isolated,
minimal implementation with build selection, explicit HSTRING ownership, transaction rollback, and runtime
session migration verification.
