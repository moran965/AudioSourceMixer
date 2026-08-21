# 浏览器标签页增强：实现与限制

## 已实现

- Manifest V3 action 用户手势启动/停止 tabCapture；没有 popup 和网页 host permission。
- service worker 把按 browser+tab 隔离的捕获状态、generation 和全部待授权 endpoint 队列保存在 `storage.session`；持久 browser 映射保存在 `storage.local`。重启时与 offscreen `audio.list`、`tabCapture.getCapturedTabs()` 握手并重新向 Native Host 注册现存图，不重复申请 stream ID。
- 桌面端发送 Windows endpoint ID/name/default/available catalog、correlation ID、generation 和请求来源，并等待匹配标签页的 Pending/Applied/Failed ACK；旧 correlation/generation 不完成新请求，超时明确失败。
- 可见授权页显示全部待授权 endpoint 和各自等待标签页数；优先在按钮手势中调用 `selectAudioOutput()`。兼容路径只在点击后请求麦克风权限并立即停止所有 track；manifest 不声明无效的 `audioCapture` 权限，不录音、不保存 PCM。
- 映射以 browser + Windows endpoint ID 为主键，并只保存具体物理输出的 browser deviceId、label、groupId；Chromium 虚拟 `default`/`communications` 不能授权或参与重绑定。名称只用于显示和唯一旧映射迁移。
- offscreen 图以 browser+tab 为键，每个图有独立 context/state/generation/串行队列。调用 `setSinkId(deviceId)` 后读取 `context.sinkId`；空值或不相等返回 Failed，不静默切到默认。
- 每个图在 source 后固定连接 10 个 BiquadFilter、独立 headroom、主 Gain、StereoPanner 和 Analyser。EQ 使用短时间平滑更新，关闭时所有滤波 gain 归零且 headroom 为 1；切换 EQ 不重建 capture/context/sink。
- deviceId 失效时先尝试唯一 groupId+标签或唯一标签安全重绑定并更新时间戳；歧义时标记 stale、进入 PendingAuthorization。devicechange 独立重验所有图；停止一个标签页只关闭该图的 MediaStream、AudioNodes 和 AudioContext。

## 自动测试边界

Node 测试验证多 endpoint/多 waiter 授权队列、每标签页 generation、service worker 恢复入口、track 立即停止、200% gain、十段 EQ/预设/headroom、双图隔离、sink 匹配/不匹配、安全重绑定、deviceId 失效和资源关闭。隔离 profile 的 Chrome/Edge 真实引擎测试用 OfflineAudioContext 客观验证频响、0.5 主音量比例和单声道无泄漏。浏览器 API 或离线渲染仍不能代替人耳确认物理声卡实际出声。

0.1.2 曾在 Chrome 151 开发者模式手工加载固定 ID 扩展，并以 440/880 Hz 两个真实标签完成 action/tabCapture、具体 Realtek/WH deviceId、双 sink、独立音量/平衡/暂停、20 次交替路由、停止单图、service worker Reload/重连及蓝牙断连/重连恢复。这是历史 API 证据。v1.0.0 使用隔离临时 profile 和 CDP 验证中英授权页、offscreen 图及 service worker 无运行时错误，但不会接管或改写正在使用的个人浏览器配置。action/tabCapture 物理听音、Realtek/WH 授权与双 sink 仍需用户完成；API 的精确 sinkId 与 Windows 会话 endpoint 读回不能替代真人听感，DRM、受保护内容、页面导航、全屏、休眠/丢弃标签页也仍以人工硬件矩阵为准。
