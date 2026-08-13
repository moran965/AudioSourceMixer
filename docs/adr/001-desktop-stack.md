# ADR 001：桌面技术栈

状态：接受。

选择 .NET 8 LTS、C# 与 WPF，目标 `net8.0-windows` / win-x64。理由是本机已有受支持 SDK、WPF 适合托盘型本地工具、可 self-contained 发布，且无需 Electron/网页运行时。UI 只持有服务接口和不可变快照，不持有 COM 对象。

托盘使用框架内置 WinForms `NotifyIcon`，不引入第三方 UI 包。
