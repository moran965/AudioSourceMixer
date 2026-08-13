# 隐私说明

- 软件不把音频录制成文件、不持久化 PCM、不上传数据，不包含遥测或联网服务。
- WindowsAudio 只读取 Core Audio 会话元数据、音量、静音、声道值和瞬时峰值。
- 浏览器音频只在本地 offscreen Web Audio 图中流动。IPC 不传 PCM、频谱或网页正文，只传浏览器类型、tab ID、用户可见标题、origin、捕获状态、0–2 增益、静音、平衡、0–1 峰值、10 段 EQ 参数、用户选择的设备显示名称和输出状态。
- origin 只保留 scheme/host/port；查询参数和页面路径不会写入配置或日志。
- 扩展权限及用途见 README。没有 host permissions，也不读取 Cookie、历史、密码、表单或正文。
- 输出选择使用扩展上下文中的 `enumerateDevices()` 与 `AudioContext.setSinkId()`；不把完整设备列表上传或写入遥测。映射保存 Windows endpoint ID/名称和 browser deviceId/label/groupId；日志只记录 browser ID 的截断哈希。
- 普通 Windows 会话只调用 Core Audio 原生控制和应用路由策略，不捕获、复制或重新渲染目标进程 PCM；开发用 `ProcessLoopbackProbe` 不进入产品运行时或交付包。
- 日志只记录版本/设备切换/会话生命周期/API 错误/恢复与桥接状态，默认单文件约 1 MiB 后滚动为一个备份。
- 配置、回滚和日志位于 `%LocalAppData%\AudioSourceMixer`。主界面可清除偏好；完全清理可在退出后删除该目录。
- Native Messaging 注册可运行 `scripts\unregister-native-host.ps1` 删除。
