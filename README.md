# BA Pointer

一款 Windows 桌面指针与点击特效工具，使用从《蔚蓝档案》（Blue Archive）`UI/FX_Touch` 资源中提取的素材还原游戏内效果。当前版本采用 WinUI 3、Direct3D 11 与 DirectComposition 实现。

## 下载

从 [v1.1.1 发布页](https://github.com/Dr-hydra/BA-Pointer/releases/tag/v1.1.1) 下载：

- `BA.Pointer.WinUI-1.1.1-x64.exe`：完整自包含版，内置 .NET 与 Windows App Runtime，适用于 Windows 10 19041 或更高版本。

这是一个 Windows x64 单文件程序，首次运行时会将内置的原生运行库和资源解压到用户临时目录。

## 主要功能

- 使用本地保存的《蔚蓝档案》指针图片与 `FX_Touch` 贴图
- Direct3D 11 HDR 场景、DirectComposition 透明覆盖层与多级 Bloom
- 可调总体缩放、碎片大小、碎片密度、颜色过渡、动画时长、拖尾和 Bloom 参数
- 可选拖尾常驻，无需按住鼠标也可显示连续拖尾
- 支持不同分辨率、混合 DPI、负坐标排列和显示器热插拔的多屏桌面
- 支持“全部桌面”与“前台应用全屏时暂停”两种生效范围
- 支持托盘控制、全局 `Ctrl+Alt+P` 开关、设置持久化、静默启动和管理员启动
- 打开主界面时自动检查 GitHub 稳定版更新
- 自动检测并恢复 Direct3D/DirectComposition 显示链路，开关特效时完整重建覆盖层
- 停止效果或退出程序时恢复原系统指针

## v1.1.1

- 将分段绘制改为连续带状网格，消除拖尾接缝和重复亮斑
- 修复点击特效结束后拖尾 Bloom 被提前关闭、视觉上突然变细的问题
- 增加“拖尾常驻”开关，可在普通鼠标移动时持续显示拖尾
- 本版本仅发布完整自包含包

## v1.1.0

- 每台显示器使用独立 DirectComposition 覆盖层，支持不同分辨率、DPI、负坐标和热插拔
- 将移动碎片与拖尾参数拆分为独立设置分组
- 打开主界面时自动检查 GitHub Releases 更新
- 关于页增加 B 站主页链接
- 增加不内置 Windows App Runtime/WinUI 3 运行时的 Win11 22H2+ 精简包

## v1.0.1

- 修复长时间运行后点击与拖尾特效可能不再显示的问题
- 增加 SwapChain Present、DirectComposition、覆盖窗口及自动恢复诊断日志
- 软件内开关现在会完整重建覆盖窗口和图形管线

## 构建

环境要求：Windows 10 19041 或更高版本、.NET 10 SDK。Windows App SDK 和其他 NuGet 依赖会在还原项目时下载。

```powershell
dotnet build .\BA.Pointer.WinUI\BA.Pointer.WinUI.csproj -c Debug
dotnet publish .\BA.Pointer.WinUI\BA.Pointer.WinUI.csproj -c Release -r win-x64 -o .\dist\BA.Pointer.WinUI-Release
```

Release 配置会生成自包含单文件 EXE。素材来自 `BA.Pointer\Assets`，着色器位于 `BA.Pointer.WinUI\Shaders`。

## 数据目录

设置文件和生成的光标文件保存在 `%APPDATA%\BA.Pointer`，运行日志位于 `%APPDATA%\BA.Pointer\runtime.log`。

## 声明

BA Pointer 是非官方社区工具，与 Blue Archive、Nexon、NAT Games 及其关联方无关。《蔚蓝档案》的名称和提取素材版权归各自权利人所有。程序运行时不会注入或修改游戏进程。

本仓库目前未对提取的游戏素材授予独立的开源许可证。

---

## English

BA Pointer is a Windows desktop pointer and click-effects utility reconstructed from extracted Blue Archive `UI/FX_Touch` resources. The current app uses WinUI 3, Direct3D 11, and DirectComposition.

### Download

Download the following file from the [v1.1.1 release](https://github.com/Dr-hydra/BA-Pointer/releases/tag/v1.1.1):

- `BA.Pointer.WinUI-1.1.1-x64.exe`: fully self-contained, with .NET and Windows App Runtime included; supports Windows 10 19041 or later.

### Features

- Locally bundled Blue Archive pointer image and `FX_Touch` textures
- Direct3D 11 HDR rendering, DirectComposition overlay, and multi-level Bloom
- Adjustable scale, fragments, color transition, density, trail, duration, and Bloom parameters
- Optional persistent trail while moving the pointer without holding a mouse button
- Multi-monitor support for mixed resolutions, DPI scaling, negative coordinates, and display hot-plugging
- Desktop-wide effects or automatic pause while a foreground application is fullscreen
- Tray controls, global `Ctrl+Alt+P` toggle, silent startup, and optional administrator startup
- Automatic stable-release checks when the main window is opened
- Automatic Direct3D/DirectComposition health checks and overlay recovery

### v1.1.1

- Replaces segmented trail sprites with one continuous ribbon mesh
- Keeps trail Bloom active after the short click effect expires
- Adds a persistent-trail toggle for ordinary pointer movement
- Ships only the fully self-contained package

### v1.1.0

- Uses one DirectComposition overlay per display for mixed resolutions, DPI, negative coordinates, and hot-plugging
- Separates fragment and trail controls into distinct settings groups
- Checks GitHub Releases when the main window opens
- Adds the Bilibili profile link to the About page
- Adds a smaller Windows 11 22H2+ package without the bundled Windows App Runtime/WinUI 3 runtime

### v1.0.1

- Fixes effects becoming invisible after extended runtime
- Adds detailed SwapChain, DirectComposition, overlay, and recovery diagnostics
- Recreates the complete overlay and graphics pipeline when effects are toggled

### Build

Requires Windows 10 19041 or later and the .NET 10 SDK.

```powershell
dotnet build .\BA.Pointer.WinUI\BA.Pointer.WinUI.csproj -c Debug
dotnet publish .\BA.Pointer.WinUI\BA.Pointer.WinUI.csproj -c Release -r win-x64 -o .\dist\BA.Pointer.WinUI-Release
```

Settings, the generated cursor file, and `runtime.log` are stored under `%APPDATA%\BA.Pointer`.

### Notice

BA Pointer is an unofficial community tool. It is not affiliated with Blue Archive, Nexon, NAT Games, or their affiliates. Blue Archive names and extracted game assets remain the property of their respective rights holders. No game process injection or modification is performed at runtime.

This repository does not grant a separate open-source license for the extracted game assets.
