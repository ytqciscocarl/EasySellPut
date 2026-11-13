# EasySellPut

An ultra-lightweight market data terminal for Windows and macOS, with support for U.S. stocks and options strategies.

一款可在 Windows 和 macOS 上运行的超轻量级行情终端，支持美股及期权策略。

Current version: **0.5.3**

## Features

- Watchlists, quote overview, and K-line charts
- Options chain, implied volatility, and strategy P/L
- Price / IV alerts, news and fundamentals, and data-source status
- Chinese and English UI, light and dark themes
- Windows installer / portable builds, and a macOS portable app

## Build from source

Requirements: Windows 10+ (for the Windows app) or macOS 26+ (for the Mac app), and the repo-local .NET 10 SDK installed by the setup script (no admin rights required).

```powershell
# Windows: install the local toolchain, then build and test
powershell -ExecutionPolicy Bypass -File tools\setup-toolchain.ps1
powershell -ExecutionPolicy Bypass -File build\build.ps1 -Test
```

```powershell
# Run the Windows app with data kept inside the repo
powershell -ExecutionPolicy Bypass -File build\run-dev.ps1
```

Release packs (installers and portable zips) are produced by `build/pack.ps1` and `build/pack-mac.sh`. Those artifacts are not stored in this repository; publish them with GitHub Releases if needed.

## Data location

Installed / normal mode stores settings under `%LOCALAPPDATA%\EasySellPut` on Windows, or `~/Library/Application Support/EasySellPut` on macOS. Portable builds keep data in a `data` folder next to the app. Existing folders from earlier names (`美丽稻谷终端` or `TyuStockApp`) are migrated automatically when possible.

## License

[MIT](LICENSE) © 2026 Carl Yu

This software is not investment advice. Market data comes from third-party public endpoints and may be delayed, interrupted, or incorrect. Follow each data source's terms; do not redistribute that data commercially.
