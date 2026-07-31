# PulseCast 心率广播

PulseCast 是面向 Windows 11 x64 的 WPF 桌面应用，用于发现 BLE 心率设备，通过标准广播或 GATT 心率服务接收数据，并用置顶悬浮窗显示实时 BPM。

## 功能

- 扫描附近 BLE 设备并展示名称、地址与信号强度。
- 优先解析标准心率广播；支持 GATT `0x180D/0x2A37` 连接回退。
- 连接支持取消、15 秒超时、失败回滚与完整断开。
- 实时、扫描、等待、信号中断和错误状态具有明确文字与语义色。
- 悬浮窗支持置顶、拖动锁定、背景隐藏、点击穿透、字体/颜色/渐变和缩放。
- 自动反色在后台采样当前显示器区域，支持副屏、负坐标与 Per-Monitor V2 DPI。
- 设置使用串行原子保存；损坏配置会备份并恢复默认值。
- 诊断默认关闭；启用后对设备标识脱敏、限速并限制容量。

## 系统要求

- Windows 10 1809 或更高版本；推荐 Windows 11。
- x64 处理器。
- 支持 BLE 的蓝牙适配器。
- .NET 8 SDK（源码构建）或项目发布包（直接运行）。

## 快速开始

```powershell
git clone https://github.com/lappland22233/PulseCast
cd HeartRateBroadcast
dotnet restore
dotnet build HeartRateBroadcast.sln -c Release
dotnet test HeartRateBroadcast.sln -c Release
dotnet run --project src/HeartRateBroadcast.App/HeartRateBroadcast.App.csproj -c Release
```

启动前请开启 Windows 蓝牙，并在手环或运动应用中启用“心率广播/共享心率”。如果设备只声明标准心率服务但广播不含 BPM，请在设备列表中选择设备并建立 GATT 连接。

## 项目结构

```text
.
├── src/
│   └── HeartRateBroadcast.App/       # WPF 应用、BLE/GATT、悬浮窗和设置服务
├── tests/
│   └── HeartRateBroadcast.Tests/     # xUnit 自动化测试
├── HeartRateBroadcast.sln            # x64 解决方案
├── .gitignore
└── .gitattributes
```

## 架构

```text
BluetoothAdvertisementService / HeartRateGattClient
                       ↓
              HeartRateParserPipeline
                       ↓
              HeartRateStateService
                 ↙            ↘
      MainWindowViewModel   OverlayViewModel
```

- 蓝牙与计时器线程仅维护服务内部状态，对 UI 发布不可变快照。
- ViewModel 在 WPF Dispatcher 上更新绑定集合和展示状态。
- GATT 客户端串行化连接、断开和释放，并隔离旧会话通知。
- 设置存储使用深快照、唯一临时文件和串行写入。

## 隐私与诊断

- 应用不需要云端服务，设置保存在当前用户的本地应用数据目录。
- 诊断模式默认关闭。
- 诊断开启后仍会记录截断的广播载荷；分享诊断信息前请确认其中不含敏感设备数据。

## 全屏与显示限制

支持普通窗口、无边框全屏和窗口化全屏，并在显示设置变化后恢复置顶与可见位置。普通桌面窗口无法覆盖真正绕过 DWM 的独占全屏、UAC 安全桌面或受保护系统界面。本项目不使用注入、DirectX Hook 或反作弊规避技术。

自动反色依赖 GDI 屏幕采样；HDR/WCG、受保护视频、硬件 Overlay 或远程桌面环境下结果可能与视觉亮度不同。

## 故障排查

- **未发现设备**：确认蓝牙已开启、设备允许心率共享，并尝试停止后重新扫描。
- **连接超时**：将设备靠近电脑，断开手机端占用后重试。
- **悬浮窗无法点击**：从主窗口点击“切换点击穿透”恢复鼠标交互。
- **设置恢复默认**：损坏设置会在本地配置目录备份为 `settings.json.corrupt-*.bak`。

## 贡献

提交改动前请运行：

```powershell
dotnet build HeartRateBroadcast.sln -c Release
dotnet test HeartRateBroadcast.sln -c Release
```

请保持文本文件为 UTF-8，勿提交构建产物、本地设置、诊断数据或凭据。

## 许可证

