# Changelog

## [1.0.0] - 2026-09-15

### Added
- AltSign 核心签名库
  - IPA解析与签名引擎
  - Apple Developer Portal API客户端
  - 证书管理 (Keychain)
  - 设备通信 (libimobiledevice集成)
  - 本地HTTP服务器 (iOS端)
- AltServer 主机端
  - CLI入口 (install/refresh/devices/certs/server)
  - 守护进程服务
  - 应用源管理
- AltStore iOS客户端
  - 应用列表与安装管理
  - 源系统 (第三方JSON源)
  - 设置与证书状态
  - AltServer连接状态管理
- CI/CD
  - GitHub Actions CI (lint + test + build)
  - GitHub Actions 发布流程 (DMG + IPA + PAL)

## [0.1.0] - 2026-08-01

### Added
- 初始项目结构
- 基础文档