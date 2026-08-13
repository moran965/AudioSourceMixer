# 实施计划与完成状态

1. 环境、版本库、Skills 检查：完成。
2. Microsoft Core Audio / Chrome 官方文档核对：完成。
3. Core Audio x64 Release 探针：完成；PotPlayer 与确定性普通播放器各执行默认/POST_VOLUME × 正常/volume0/mute 六组矩阵并保留原始日志。
4. 核心模型、会话控制、回滚事务、偏好：完成。
5. WPF UI、峰值、托盘、全活动 endpoint `EndpointContext` 会话合并与防抖动态刷新：完成。
6. Native Messaging、Named Pipe、MV3 offscreen Web Audio、可见输出授权页与持久 browser deviceId 映射：完成代码与自动化验证。
7. 单元/集成测试、测试音频、隐私与限制文档：完成。
8. self-contained 便携包与每用户安装器：由统一脚本生成并验证。
9. 真实 Chrome/Edge 标签页矩阵：Edge 扩展页与 Chrome 引擎页的 WH/Realtek 非空 deviceId 和 `context.sinkId` 精确读回已完成；必须由人在 action/授权页点击的 tabCapture、150%/200% 听感和双 sink 声学切换尚未执行，Chrome 扩展仍需开发者模式手工加载，明确记录为限制。

P0 不依赖浏览器扩展。Windows 会话控制错误不会回退为系统主音量控制或假 UI。
