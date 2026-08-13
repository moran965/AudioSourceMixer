# Windows 音频能力报告

测试环境：Windows 11 build 26200 x64；WH-1000XM5 与 Realtek(R) Audio 均为 active render endpoint。

## 发现与原生控制

服务为每个活动 endpoint 建立 `EndpointContext`，可同时发现多个端点上的 sibling session。普通 Windows 来源只公开原生 0–100% 主音量、静音、可验证双声道平衡和峰值；不捕获或复制 PCM。PID 0、系统声音、受保护/独占模式若不能安全控制会报告限制。

## 应用路由

当前 build 可激活 Windows21H2 AudioPolicyConfig ABI。三个 role 以事务写入并读回；同应用的活动 endpoint 集合决定状态。现代应用可能保持已打开的共享 WASAPI 流，此时策略已持久化但状态为 `PendingStreamRestart`，暂停/恢复或重开应用后再由后台观察提升为 `Applied`，不会因固定超时回滚。

真机矩阵和最终结果记录在 `docs/testing.md` 与构建 manifest。自动化不会把“策略写入”或单个匹配 session 冒充“全部声音已从目标设备发出”。声学输出、独占模式、DRM、5.1/7.1、空间音频和蓝牙断连听感仍需要人工验证。

## 浏览器标签页

浏览器增强仍支持 0–200%，并可在同一真实 tabCapture Web Audio 图内提供逐标签页 EQ。主音量、静音、平衡、EQ 和输出 sink 是彼此独立的参数；停止增强时整个图被断开并关闭。API 页面或设置页临时 Context 的 `setSinkId()` 只能作为能力诊断，不能替代真实 tabCapture 验收。

普通 Windows 会话的 `ISimpleAudioVolume`、`IChannelAudioVolume` 和 AudioPolicyConfig 不提供任意逐会话 PCM EQ。`ProcessLoopbackProbe` 仅保留为显式开发研究工具，不参与常规构建或发行；v0.2.1 不引入捕获重放 helper、虚拟驱动或全局设备 EQ。
