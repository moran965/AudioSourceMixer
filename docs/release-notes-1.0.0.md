# Audio Source Mixer 1.0.0 / 正式版说明

## English

1.0.0 introduces per-application Windows audio controls, user-enabled Chrome/Edge tab mixing, balance, live metering, ten-band browser EQ, output routing, profiles, tray operation, and complete Simplified Chinese/English localization.

Pre-release fixes make browser test playback fail closed unless the selected physical device remains available and the effective sink exactly matches, and make the closed language selector show its current value. Upgrading from 0.2.2 preserves compatible user settings; uninstall retains user data unless removal is explicitly selected.

Known limits: the unpacked browser extension requires user gestures; DRM, drivers, Bluetooth reconnects, and stream restarts vary by environment. Verify the trusted Authenticode publisher and the SHA-256 in `SHA256SUMS.txt`.

This project was built through vibe coding and iterative AI-assisted development under human product direction, review, hands-on validation, and real-hardware testing.

## 简体中文

1.0.0 提供 Windows 按应用音频控制、用户主动启用的 Chrome/Edge 标签页混音、平衡、实时电平、浏览器十段 EQ、输出路由、配置记忆、托盘，以及完整简体中文/英文界面。

发布前修复使浏览器试听只在所选物理设备仍可用且有效 sink 严格匹配时播放，并修复语言下拉栏关闭状态不显示当前值。由 0.2.2 升级会保留兼容设置；卸载默认保留用户数据，只有明确选择才删除。

已知限制：已解压扩展需要用户手势；DRM、驱动、蓝牙重连和音频流重启因环境而异。请验证受信 Authenticode 发布者以及 `SHA256SUMS.txt` 中的 SHA-256。

本项目通过 vibe coding（氛围编程）和迭代式 AI 辅助开发构建，产品方向、审查、实际验收和真实硬件测试由人工主导。
