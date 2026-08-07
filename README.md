# BA Pointer

Windows desktop pointer and click effects reconstructed from the extracted `UI/FX_Touch` resources of Blue Archive. The current app is WinUI 3 with Direct3D 11 and DirectComposition.

## Download

Download `BA.Pointer.WinUI.exe` from the [v1.0.0 release](https://github.com/Dr-hydra/BA-Pointer/releases/tag/v1.0.0). It is a self-contained single-file Windows x64 build and does not require a separate .NET or Windows App SDK installation.

The first launch extracts bundled native/runtime files to the user's temporary directory. The distribution itself is one executable.

## Features

- Extracted Blue Archive pointer image and `FX_Touch` textures stored locally with the app
- Direct3D 11 HDR scene with DirectComposition overlay and multi-level Bloom
- Adjustable overall scale, fragment size, fragment density, color transition, duration, trail, and Bloom parameters
- Effect scope: all desktop, or pause while a foreground application is fullscreen
- Tray controls, global `Ctrl+Alt+P` toggle, settings persistence, silent startup, and optional administrator startup
- Original system cursor restoration when the effect stops or the app exits

## Build

Requirements: Windows 10 19041 or later, .NET 10 SDK, and the Windows App SDK workload restored by NuGet.

```powershell
dotnet build .\BA.Pointer.WinUI\BA.Pointer.WinUI.csproj -c Debug
dotnet publish .\BA.Pointer.WinUI\BA.Pointer.WinUI.csproj -c Release -r win-x64 -o .\dist\BA.Pointer.WinUI-Release
```

The Release configuration produces a self-contained single-file executable. Assets are copied from `BA.Pointer\Assets` and the shader is copied from `BA.Pointer.WinUI\Shaders`.

## Storage

Settings and the generated cursor file are stored under `%APPDATA%\BA.Pointer`.

## Notice

BA Pointer is an unofficial community tool. It is not affiliated with Blue Archive, Nexon, NAT Games, or their affiliates. Blue Archive names and extracted game assets remain the property of their respective rights holders. No game process injection or modification is performed at runtime.

This repository does not currently grant a separate open-source license for the extracted game assets.
