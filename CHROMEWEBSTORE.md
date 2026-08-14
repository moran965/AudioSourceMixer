# Chrome Web Store Listing — Audio Source Mixer 标签页增强

> Last Updated: 2026-08-14 · Draft only; v0.2.2 is not published in Chrome Web Store or Edge Add-ons.

## Store Listing

**Extension Name**  
Audio Source Mixer 标签页增强

**Short Description**  
在你点击工具栏图标后，将当前标签页加入 Audio Source Mixer，分别控制音量、声道、均衡器和输出设备。

**Detailed Description**

Audio Source Mixer 标签页增强让正在播放声音的 Chrome 或 Edge 标签页出现在桌面混音器中。

功能包括标签页独立音量（最高 200%）、左右声道平衡、十段均衡器、静音和输出设备选择。扩展只在你点击工具栏图标后处理当前标签页，再次点击即可停止增强。

使用方法：先运行 Audio Source Mixer；打开正在播放声音的网页；点击扩展图标；返回桌面混音器调节。首次选择非默认输出设备时，请在浏览器授权页选择对应设备、试听并确认。

音频始终留在本机，不会录制、保存或上传。扩展不包含广告、分析、远程代码或第三方服务。标签页标题、站点来源和控制状态只在当前设备的扩展与 Audio Source Mixer 桌面程序之间处理。

v0.2.2：增加首次使用指南，修复输出设备确认与存储更新并发错误，并将各增强标签页的独立实时电平提升到 10Hz、快速上升和约 350ms 平滑衰减。

**Category**  
Productivity

**Single Purpose**  
把用户明确启用的浏览器标签页音频加入本机 Audio Source Mixer，以便独立控制声音。

**Primary Language**  
Chinese (Simplified)

## Graphics & Assets

| Asset | Dimensions | Status | Filename |
|---|---:|---|---|
| Store Icon | 128×128 PNG | Ready | `assets/icon-128.png` |
| Screenshot 1 | 1280×800 or 640×400 | Needed before submission | 浏览器标签页来源 |
| Screenshot 2 | 1280×800 or 640×400 | Needed before submission | 输出设备授权页 |
| Screenshot 3 | 1280×800 or 640×400 | Needed before submission | 扩展欢迎页 |
| Small Promo Tile | 440×280 | Optional / not created | — |

当前仓库内的桌面 UI 验收截图不是商店素材，不会被扩展包携带。

## Permissions Justification

| Permission | Type | Justification |
|---|---|---|
| `activeTab` | permissions | 仅在用户点击工具栏图标时访问当前标签页以启动该标签页的音频增强。 |
| `tabs` | permissions | 读取被用户启用标签页的标题/来源、跟踪关闭事件，并复用已打开的扩展内引导或授权页。 |
| `tabCapture` | permissions | 在用户直接点击扩展图标后取得当前标签页音频流；不进行后台自动捕获。 |
| `offscreen` | permissions | 在 MV3 后台休眠期间维持用户已启用的本机音频处理链。 |
| `nativeMessaging` | permissions | 与本机 Audio Source Mixer 桌面桥交换控制状态；只允许清单中的明确受信扩展 ID。 |
| `storage` | permissions | 在本机保存输出设备映射、首次使用完成状态和短期运行状态。未使用 sync。 |

不声明 `host_permissions`，也不请求 `<all_urls>`、cookies、history、webRequest、identity 或远程脚本权限。

## Privacy & Data Use

扩展处理当前被用户启用标签页的标题、站点 origin、音量/声道/EQ/输出选择与实时电平。这些信息仅保留在浏览器本地存储、会话存储或传给同一台电脑上的 Audio Source Mixer Native Host；不会发送到互联网、开发者服务器或第三方。

- [x] 数据不出售给第三方。
- [x] 数据不用于扩展单一目的以外的用途。
- [x] 数据不用于信用或借贷用途。
- [x] 不使用分析、广告、遥测或第三方服务。

**Privacy Policy URL**  
发布前必须为 [docs/privacy.md](docs/privacy.md) 配置稳定的公开 HTTPS 地址；目前尚未提交商店，因此没有伪造 URL。

## Distribution

**Visibility:** Unlisted（计划；尚未发布）  
**Regions:** All regions（计划）

## Developer Info

**Publisher Name:** Audio Source Mixer contributors  
**Contact Email:** 发布前填写并验证  
**Support URL:** 发布前填写

## Version History

| Version | Date | Changes | Status |
|---|---|---|---|
| 0.2.2 | 2026-08-14 | 首次使用指南、授权竞态修复、输出映射运行验证、每标签页独立 10Hz 实时电平 | Draft |

## Review Notes

- v0.2.2 只提供随桌面安装包分发的本地加载流程；没有声称商店版已发布。
- 捕获必须由工具栏图标的用户手势触发。
- service worker 空闲恢复不会连接 Native Host，单独启动浏览器不会启动桌面程序。
- 所有脚本随扩展打包；无 `eval`、远程代码或 CDN。
- 发布阻塞项：公开隐私政策 URL、有效联系邮箱、商店尺寸截图和实际商店 ID。
