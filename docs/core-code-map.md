# Audio Source Mixer 0.2.0 核心代码图

## 桌面、设置和启动

| 能力 | 入口 | 关键行为 |
|---|---|---|
| 混音器 / 设置页 | `Desktop/MainWindow.xaml` | 顶部页签；全局选项从混音器移入设置页；所有绑定显式声明方向 |
| 设置状态与命令 | `Desktop/ViewModels/MainViewModel.cs` | 串行保存；浏览器管理/清空；清除配置和恢复默认确认；版本/部署类型 |
| 设置 schema | `Core/Persistence/ProfileKeys.cs` | `ApplicationSettings` schema 2；反序列化保留 0.1.2 四个原字段 |
| 开机启动 | `Desktop/Services/StartupRegistrationService.cs` | 安装版实际读取/写入 HKCU Run；只删除指向当前产品路径的值 |
| 后台启动 | `Desktop/App.xaml.cs` | `--background` 完成托盘、桥接和音频初始化但不 `Show()` 主窗口 |
| 优雅安装退出 | `Desktop/App.xaml.cs` | `Local\AudioSourceMixer.Exit` 事件触发同一恢复/清理路径 |
| 平衡中心吸附 | `Desktop/Controls/CenterDetentSlider.cs` | 用户输入进入 ±5、离开 ±8；程序化 Value 不吸附 |
| UI 回归 | `Desktop/Diagnostics/UiSmokeVerifier.cs` | 显示真实窗口、物化源模板、峰值更新、切换并物化设置页、审计绑定 |

## Windows 音频

| 能力 | 入口 |
|---|---|
| 多 endpoint 会话发现与通知 | `WindowsAudio/WindowsAudioService.cs`, `EndpointContext.cs` |
| 原生音量、静音、平衡、峰值 | `WindowsAudio/SessionHandle.cs` |
| 应用路由 generation/cancellation/last-write-wins | `Core/Infrastructure/ApplicationRouteCoordinator.cs`, `Desktop/ViewModels/MainViewModel.cs` |
| AudioPolicyConfig 写入、读回、回滚 | `WindowsAudio/WindowsAppRoutingBackend.cs` |
| 配置和崩溃恢复 | `Core/Persistence/JsonStores.cs`, `JsonRollbackJournal` |

普通会话继续限制为 100%；只有用户主动捕获的浏览器标签页可使用 200% Web Audio 增益。

## 浏览器授权和 Native Messaging 生命周期

| 能力 | 入口 | 关键行为 |
|---|---|---|
| 授权/管理页面 | `BrowserExtension/output-authorization/authorize.html`, `authorize.js`, `authorize.css` | 用户手势打开系统选择器；候选不持久化；试听后确认；修改/删除/清空 |
| 候选与测试音 | `authorization-workflow.js` | 名称规范化辅助提示；临时 AudioContext + setSinkId；结束必关闭 |
| 映射存储与迁移 | `mappings.js` | `outputMappingsV3` / schema 3；旧映射迁移为 unverified；浏览器+endpoint 隔离 |
| service worker | `service-worker/service-worker.js` | 顶层监听器；session 状态和锁；无活动图恢复不 connectNative；活动图共用一条 port |
| offscreen 音频图 | `offscreen/offscreen.js` | 每标签 Web Audio 图；严格核对 sinkId；失败不伪装为默认设备成功 |
| Native Host | `NativeHost/NativeHostRunner.cs` | 仅桥接 stdio 和命名管道；800 ms 超时报告并退出，绝不启动桌面程序 |
| 桌面桥接协议 | `Core/Browser/BrowserProtocol.cs`, `BrowserBridgeServer.cs` | 协议 2；扩展浏览器/版本状态；打开 options、清空映射、标签控制与确认 |

扩展求值、安装、Reload 和浏览器启动只恢复状态；只有用户操作或确有仍活动的 capture graph 才连接 Native Host。

## 安装、卸载和安装路径

| 能力 | 入口 | 关键行为 |
|---|---|---|
| 安装模式和 UI | `Installer/Program.cs` 的 `InstallerForm` | 路径、浏览、快捷方式、启动/后台选项；默认启动关闭 |
| 路径安全 | `NormalizeAndValidateInstallPath` | 规范绝对路径；拒绝根目录和受保护根；写入探针；非产品非空目录拒绝覆盖 |
| 原子提交/回滚 | `Install` | 同卷 sibling staging/backup；本次 payload；故障恢复旧目录 |
| 产品身份 | `install-identity.json` + 卸载注册表 `InstallLocation` | 卸载递归删除前双重验证实际目录 |
| 卸载模式 | `UninstallerForm`, `Uninstall` | installed 文件名无参数即卸载 UI；可选删除用户数据，默认保留 |
| 注册 | `RegisterNativeHost`, `RegisterUninstaller`, `WriteStartup`, `CreateShortcut` | 全部使用最终真实安装路径 |
| 自删除 | `ScheduleSelfRemoval` | 验证后路径、隐藏 PowerShell helper、临时安装器日志 |

自动矩阵位于 `scripts/verify-installer.ps1`；打包总入口为 `scripts/build-all.ps1`。
