# AltStore Suite

类AltStore的iOS应用侧载系统，包含iOS客户端、主机端服务和核心签名库。

> 📖 详细使用说明见 [用户使用手册](docs/用户使用手册.md)

## 项目结构

```
AltStore Suite/
├── AltSign/           # 核心签名库 (Swift Package)
│   └── Sources/AltSign/
│       ├── SigningEngine.swift          # IPA签名引擎
│       ├── AppleDeveloperPortal.swift   # Apple开发者门户API
│       ├── DeviceManager.swift          # 设备通信 (libimobiledevice)
│       ├── LocalServer.swift            # 本地服务器 (iOS端)
│       └── Models/                      # 数据模型
│           ├── Certificate.swift
│           ├── Device.swift
│           ├── AppID.swift
│           ├── ProvisioningProfile.swift
│           ├── IPA.swift
│           └── HTTP.swift
│
├── AltServer/         # 主机端服务
│   ├── Sources/AltServer/              # AltServer Core
│   │   └── Services/
│   │       ├── AltServerDaemon.swift   # 主机守护进程
│   │       ├── CertificateManager.swift # 证书管理
│   │       └── SourceManager.swift     # 源管理
│   └── Sources/AltServerCLI/           # 命令行入口
│       └── main.swift
│
├── AltStore/          # iOS客户端 App
│   ├── AltStore.xcodeproj/
│   └── Sources/AltStore/
│       ├── AltStoreApp.swift           # App入口
│       ├── Views/                      # SwiftUI视图
│       │   ├── ContentView.swift
│       │   ├── AppsView.swift
│       │   ├── SourcesView.swift
│       │   └── SettingsView.swift
│       ├── Models/                     # 数据模型
│       │   └── AppState.swift
│       ├── Services/                   # 服务层
│       │   └── ServerManager.swift     # 与AltServer通信
│       └── Utilities/
│           └── AppStateStorage.swift
│
├── AltServer.Windows/ # Windows版主机端 (C# .NET 8 WPF)
│   ├── AltServer.Windows.csproj
│   ├── App.xaml / App.xaml.cs          # WPF应用入口
│   ├── Views/
│   │   ├── MainWindow.xaml             # 主界面
│   │   ├── MainWindow.xaml.cs          # 界面逻辑 + 托盘
│   │   └── Converters.cs               # XAML绑定转换器
│   ├── Models/
│   │   └── Device.cs                   # 数据模型
│   ├── ViewModels/
│   │   └── MainViewModel.cs            # 界面状态管理
│   ├── Services/
│   │   ├── DeviceService.cs            # libimobiledevice封装
│   │   ├── SigningService.cs           # zsign签名封装
│   │   ├── HttpServer.cs               # 本地HTTP服务器(端口27000)
│   │   ├── SettingsService.cs          # JSON设置持久化
│   │   └── LogService.cs               # 日志系统
│   └── app.manifest                    # Windows应用清单
│
├── scripts/
│   └── download-windows-tools.ps1      # Windows工具下载脚本
│
└── .github/
    └── workflows/
        ├── ci.yml                      # CI测试
        └── build-release.yml           # 构建发布
```

## 系统架构

```
┌─────────────────────────────┐
│         iOS设备              │
│  ┌──────────┐  ┌──────────┐ │
│  │ AltStore │  │ 侧载App  │ │
│  │ 客户端    │  │         │ │
│  └────┬─────┘  └──────────┘ │
│       │                      │
└───────┼──────────────────────┘
        │ USB / Wi-Fi
┌───────┼──────────────────────┐
│ ┌─────▼─────┐  ┌───────────┐ │
│ │ AltServer │  │ Apple     │ │
│ │ 主机端    │◄─│开发者门户  │ │
│ └───────────┘  └───────────┘ │
└──────────────────────────────┘
```

## 功能特性

- **应用侧载**：通过开发者证书安装IPA应用
- **自动刷新**：后台定时刷新签名，避免7天过期
- **源系统**：支持第三方应用源 (JSON格式)
- **Wi-Fi刷新**：无需USB连接即可刷新
- **证书管理**：可视化查看和管理开发者证书
- **PAL支持**：兼容欧盟Alternative Marketplace

## 快速开始

### 环境要求

- **macOS 13.0+** (macOS版 AltServer)
- **Windows 10/11** (Windows版 AltServer)
- **iPhone/iPad iOS 16.0+**
- **Xcode 15.0+** (macOS构建)
- **Swift 5.9+** (macOS构建)
- **.NET 8.0+** (Windows构建)

### 构建

```bash
# ---- macOS ----
# 构建AltServer (macOS)
swift build -c release --product AltServerCLI

# 运行测试
swift test

# 构建iOS客户端 (需要Xcode)
xcodebuild -project AltStore/AltStore.xcodeproj \
  -scheme AltStore \
  -configuration Debug \
  -destination 'generic/platform=iOS Simulator' \
  build

# ---- Windows ----
# 下载Windows所需工具 (libimobiledevice + zsign)
pwsh .\scripts\download-windows-tools.ps1

# 构建AltServer (Windows，自包含单文件)
dotnet publish AltServer.Windows/AltServer.Windows.csproj \
  -c Release \
  --self-contained true \
  -r win-x64 \
  -p:PublishSingleFile=true \
  -o build/AltServer-Windows
```

### 使用

```bash
# ---- macOS ----
# 列出连接的设备
AltServer devices

# 安装应用
AltServer install /path/to/app.ipa --device <UDID>

# 刷新签名
AltServer refresh

# 启动守护进程
AltServer server

# ---- Windows ----
# 运行AltServer.exe（带图形界面+系统托盘）
# 双击运行 AltServer.exe 或在命令行执行：
AltServer.exe

# Windows版AltServer会自动启动本地HTTP服务器（端口27000）
# iOS端AltStore客户端可自动发现并连接
```

### Windows 工具依赖

Windows版AltServer需要以下外部工具，放在 `tools/` 子目录下：

| 工具 | 用途 | 下载地址 |
|------|------|----------|
| `idevice_id.exe` | 设备发现 | [libimobiledevice-win32](https://github.com/libimobiledevice-win32/imobiledevice-net/releases) |
| `ideviceinfo.exe` | 设备信息 | 同上 |
| `ideviceinstaller.exe` | 应用安装/卸载 | 同上 |
| `idevicepair.exe` | 设备配对 | 同上 |
| `zsign.exe` | IPA代码签名 | [zsign](https://github.com/zhlynn/zsign/releases) |

自动下载：`pwsh .\scripts\download-windows-tools.ps1`

## GitHub Actions

### CI (`ci.yml`)
- 每次push到 `main`/`develop` 或创建PR时触发
- SwiftLint代码检查
- 单元测试
- Debug构建

### 构建发布 (`build-release.yml`)
- 推送 `v*` tag 时触发
- 构建四种产物：
  1. `AltServer.dmg` - macOS主机端
  2. `AltServer-Windows.zip` - Windows主机端（自包含）
  3. `AltStore.ipa` - iOS客户端
  4. `AltStore-PAL.ipa` - 欧盟版

## 签名配置

构建发布版IPA需要配置Apple开发者证书：

```yaml
# 在GitHub仓库设置中添加Secrets
env:
  APPLE_ID: ${{ secrets.APPLE_ID }}
  APPLE_ID_PASSWORD: ${{ secrets.APPLE_SPECIFIC_PASSWORD }}
  DEVELOPMENT_TEAM: ${{ secrets.DEVELOPMENT_TEAM }}
```

## 应用源格式

```json
{
  "apps": [
    {
      "name": "示例应用",
      "bundleIdentifier": "com.example.app",
      "version": "1.0.0",
      "versionDate": "2026-01-01",
      "downloadURL": "https://example.com/app.ipa",
      "localizedDescription": "应用描述",
      "iconURL": "https://example.com/icon.png",
      "tintColor": "#FF0000",
      "screenshots": [],
      "appPermissions": {
        "entitlements": [],
        "privacy": []
      }
    }
  ],
  "news": []
}
```

## 参考项目

- [AltStore](https://github.com/altstoreio/AltStore) - 官方开源
- [AltServer-Linux](https://github.com/NicklasVraa/AltServer-Linux) - Linux版
- [Sideloadly](https://sideloadly.io/) - 侧载工具
- [TrollStore](https://github.com/opa334/TrollStore) - 永久签名

## 许可证

本项目仅供学习研究使用。请遵守Apple Developer Program协议和相关法律法规。