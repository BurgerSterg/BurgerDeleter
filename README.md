# BurgerDeleter

A Windows storage optimizer and deep uninstaller.

## What it does
- Scans your full drive, sorted largest to smallest with exact GB/MB sizes
- Detects games from Steam, Epic Games, GOG, and EA App without launchers
- Finds large files: videos (VODs, recordings), ISOs, archives
- Deep uninstalls programs across 6 layers: uninstaller, registry, files, startup, scheduled tasks, services
- Light/dark mode toggle
- Before/after storage display when selecting items to delete

---

## Setup

### Requirements
- Windows 10 or 11
- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
- Visual Studio 2022 or Cursor (recommended)

### Build & Run in Cursor
1. Open the BurgerDeleter folder in Cursor
2. Open the terminal (Ctrl+`)
3. Run: `dotnet build`
4. Run: `dotnet run`

Or to build a standalone .exe:
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
Output goes to: `bin\Release\net8.0-windows\win-x64\publish\BurgerDeleter.exe`

---

## Project Structure

```
BurgerDeleter/
├── App.xaml / App.xaml.cs          Theme bootstrapper
├── MainWindow.xaml / .cs           Shell: sidebar nav, title bar, drive info
├── Themes/
│   ├── DarkTheme.xaml              Dark color palette
│   └── LightTheme.xaml             Light color palette
├── Views/
│   ├── HomeView                    Landing page with step-by-step instructions
│   ├── ScanView                    Full drive scanner (BUILT)
│   ├── GamesView                   Game detector (stub -- build next)
│   ├── LargeFilesView              Large file finder (stub -- build next)
│   └── UninstallerView             Deep uninstaller UI (stub -- build next)
├── Models/
│   └── DriveItem.cs                Represents a scanned file or folder
└── Services/
    ├── DiskScannerService.cs       Async recursive drive scanner
    ├── GameDetectorService.cs      Reads Steam/Epic/GOG/EA manifests
    └── UninstallerService.cs       6-layer deep uninstall
```

---

## What's Built vs TODO

| Feature | Status |
|---|---|
| Home screen with steps | DONE |
| Light/dark theme toggle | DONE |
| Custom window chrome | DONE |
| Drive usage in sidebar | DONE |
| Full drive scan (ScanView) | DONE |
| Before/after storage display | DONE |
| Game detector service | DONE (needs UI) |
| Large file scanner service | DONE (needs UI) |
| 6-layer uninstaller service | DONE (needs UI) |
| GamesView UI | TODO |
| LargeFilesView UI | TODO |
| UninstallerView UI | TODO |

---

## Notes
- The app requests elevation (runas) when needed for registry/service operations
- Windows Defender may flag registry edits -- that is expected behavior for an uninstaller
- GOG detection is simplified; full detection requires SQLite parsing of galaxy_2.0.db
