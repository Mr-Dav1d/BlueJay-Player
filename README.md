# 🐦 BlueJay Player v2.0

> **A high-performance, dark retro-futuristic desktop media client built with Avalonia 11 and native libmpv engine integration.**

BlueJay Player combines modern hardware-accelerated video rendering, precise sub-millisecond audio synchronization, and a sleek cyberpunk/tactical desktop experience.

---

## ⚡ Key Features (v2.0)

- **⚡ Native HWND & Floating Airspace Engine:** Native C interop binding to `libmpv` with a transparent floating Avalonia overlay for seamless UI airspace over hardware-accelerated video surfaces.
- **🌐 Network Stream Link & URL Playback (`Ctrl + V`):** Direct playback support for `http://`, `https://`, `stremio://`, `rtmp://`, and `rtsp://` video streams via instant clipboard paste (`Ctrl + V`) or drag-and-drop.
- **📁 Directory Navigation Drawer:** Integrated slide-out sidebar for real-time folder tree browsing, recursive directory file searching, and rapid playlist queued loading.
- **💬 Subtitle Syncing & Stepper Controls:** Dedicated subtitle track dropdown, external subtitle file import (`.srt`, `.ass`, `.vtt`), instant delay adjustment hotkeys (`Z`/`X` for ±0.1s, `Shift+Z`/`Shift+X` for ±0.5s), and dynamic center OSD feedback cards.
- **⏩ Modernized Playback Speed Flyouts:** Click-to-jump playback speed slider, quick presets (`0.5x`, `1.0x`, `1.5x`, `2.0x`), scroll-wheel adjustment on transport speed label, and hotkeys (`[` / `]` / `\`).
- **🖼️ Picture-in-Picture (PiP) Window:** Aspect-ratio-preserved floating PiP mode with native Win32 window subclassing, instant position sync, and double-click maximize/restore.
- **🖥️ Lock-Solid Borderless Fullscreen Mode:** Toggle borderless fullscreen with the `F` hotkey or double-click video surface, complete with active window drag guards.

---

## ⌨️ Keyboard Shortcuts Reference

| Shortcut | Description |
| :--- | :--- |
| **`Space`** / **`K`** | Play / Pause video |
| **`F`** / **`Double-Click`** | Toggle Fullscreen Mode |
| **`Z`** / **`X`** | Adjust Subtitle Delay by `-0.1s` / `+0.1s` |
| **`Shift + Z`** / **`Shift + X`** | Adjust Subtitle Delay by `-0.5s` / `+0.5s` |
| **`Ctrl + V`** | Paste & Play Network / Stremio Stream URL from Clipboard |
| **`[`** / **`]`** | Decrease / Increase Playback Speed by `0.1x` |
| **`\`** | Reset Playback Speed to `1.0x` |
| **`Left Arrow`** / **`Right Arrow`** | Seek Backward / Forward `5 seconds` |
| **`Up Arrow`** / **`Down Arrow`** | Increase / Decrease Volume `5%` |
| **`M`** | Toggle Mute |
| **`P`** | Toggle Picture-in-Picture (PiP) Mode |

---

## 🛠️ Build & Run Instructions

### Prerequisites
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows 10/11 x64

### Local Development
```powershell
# Clone repository
git clone https://github.com/Mr-Dav1d/BlueJay-Player.git
cd BlueJay-Player/BlueJayPlayer

# Build project
dotnet build

# Run application
dotnet run
```

### Build Single-File Release Executable (win-x64)
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```
The compiled self-contained release executable will be located in:
`bin/Release/net9.0/win-x64/publish/BlueJayPlayer.exe`

---

## 📄 License
Distributed under the MIT License. See `LICENSE` for details.
