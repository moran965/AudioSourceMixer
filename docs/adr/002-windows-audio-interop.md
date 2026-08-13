# ADR 002：Windows Audio Interop

状态：接受。

直接定义任务所需的 Core Audio COM 接口，避免为少量接口引入大型音频库。所有对象在专用 MTA 线程获取、调用和释放；会话与设备回调只排队刷新。枚举会话时先调用 `GetCount`，以满足 Microsoft 对新会话通知的启动要求。

实测 `IAudioSessionControl` 对象可 QueryInterface 到 `ISimpleAudioVolume`、`IChannelAudioVolume` 和 `IAudioMeterInformation`。逐声道能力根据实际 QueryInterface 和声道数启用。释放采用每次 COM 获取对应一次 `Marshal.ReleaseComObject`，避免 CLR 复用 RCW 时 `FinalReleaseComObject` 使仍在用的句柄失效。

参考：Microsoft Learn 的 `IAudioSessionManager2`、`RegisterSessionNotification`、`IChannelAudioVolume`、`ISimpleAudioVolume`、`IAudioMeterInformation` 与 `IMMNotificationClient` 文档。
