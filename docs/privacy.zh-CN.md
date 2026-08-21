# Audio Source Mixer 隐私说明

[English](privacy.md) · 最后更新：2026-08-22

Audio Source Mixer 不会向开发者、广告商、分析服务或任何第三方服务器发送个人数据、浏览记录、网页内容或音频。

扩展只在用户点击工具栏图标后处理对应标签页。为了让同一台电脑上的桌面混音器显示和控制该来源，扩展会在本机处理标签页标题、不含路径/查询参数的站点来源、控制状态、输出选择和实时电平。音频只在内存中处理，不会录制、保存或上传。

输出映射、首次引导状态和扩展语言保存在当前浏览器 profile 的 `chrome.storage.local`；活动标签页的短期状态保存在 `chrome.storage.session`。Native Messaging 只允许明确配置的扩展 ID，并且只与已安装的本机 Host 通信。

桌面程序把设置、来源配置、回滚恢复状态和日志保存在 `%LocalAppData%\AudioSourceMixer`。卸载默认保留这些数据，只有用户明确选择删除时才会移除。

本项目不使用 Cookie、分析、遥测、广告、远程脚本、`eval` 或 `chrome.storage.sync`。用户可在扩展授权页清除映射、删除浏览器扩展数据，或在卸载桌面程序时选择删除用户数据。
