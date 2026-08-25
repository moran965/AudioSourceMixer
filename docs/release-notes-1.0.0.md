# Audio Source Mixer 1.0.0 / 正式版说明

<!-- SIGNATURE_NOTICE_START -->
> **Unsigned installer warning / 未签名安装程序警告**
>
> The initial v1.0.0 installer has no Authenticode publisher. Windows may show an unknown-publisher or SmartScreen warning. Download it only from this repository's v1.0.0 Release, verify the accompanying `SHA256SUMS.txt`, and verify GitHub Artifact Attestation before running it. GitHub provenance is not Authenticode. Free trusted signing through SignPath Foundation is being prepared and has not been approved for this binary.
>
> 首次 v1.0.0 安装程序没有 Authenticode 发布者。Windows 可能显示“未知发布者”或 SmartScreen 提示。请只从本仓库 v1.0.0 Release 下载，运行前核对同一 Release 的 `SHA256SUMS.txt` 并验证 GitHub Artifact Attestation。GitHub 来源证明不是 Authenticode。SignPath Foundation 免费可信签名正在准备中，尚未批准用于此二进制文件。
<!-- SIGNATURE_NOTICE_END -->

## English

1.0.0 introduces per-application Windows audio controls, user-enabled Chrome/Edge tab mixing, balance, live metering, ten-band browser EQ, output routing, profiles, tray operation, and complete Simplified Chinese/English localization.

Pre-release fixes make browser test playback fail closed unless the selected physical device remains available and the effective sink exactly matches, and make the closed language selector show its current value. Upgrading from 0.2.2 preserves compatible user settings; uninstall retains user data unless removal is explicitly selected.

Known limits: the unpacked browser extension requires user gestures; DRM, drivers, Bluetooth reconnects, and stream restarts vary by environment. This v1.0.0 fallback is expected to report `NotSigned`; verify the SHA-256 in the same Release's `SHA256SUMS.txt` and verify GitHub provenance.

This project was built through vibe coding and iterative AI-assisted development under human product direction, review, hands-on validation, and real-hardware testing.

## 简体中文

1.0.0 提供 Windows 按应用音频控制、用户主动启用的 Chrome/Edge 标签页混音、平衡、实时电平、浏览器十段 EQ、输出路由、配置记忆、托盘，以及完整简体中文/英文界面。

发布前修复使浏览器试听只在所选物理设备仍可用且有效 sink 严格匹配时播放，并修复语言下拉栏关闭状态不显示当前值。由 0.2.2 升级会保留兼容设置；卸载默认保留用户数据，只有明确选择才删除。

已知限制：已解压扩展需要用户手势；DRM、驱动、蓝牙重连和音频流重启因环境而异。此 v1.0.0 回退版本的预期状态为 `NotSigned`；请核对同一 Release 的 `SHA256SUMS.txt` 并验证 GitHub 来源证明。

本项目通过 vibe coding（氛围编程）和迭代式 AI 辅助开发构建，产品方向、审查、实际验收和真实硬件测试由人工主导。
