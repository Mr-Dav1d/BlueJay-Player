# BlueJay Player 🎬

BlueJay Player is a premium, enthusiast-grade media player built with **Avalonia UI (.NET 9)** and powered by the native **libmpv** playback engine. It features a custom Nocturne Deep Blue design, sliding playlist/directory drawers, real-time telemetry diagnostic decks, chapter markings, seekbar thumbnail previews, and advanced subtitle synchronizers.

---

## 💻 Prerequisites & Setup Instructions

To set up the development environment on a new PC, follow these steps:

### 1. Install System Development Tools
*   **Install .NET 9.0 SDK**: Download and install the latest [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) from Microsoft.
*   **Install Git**: Install [Git](https://git-scm.com/) to clone the repository.
*   **Choose an IDE**:
    *   *Visual Studio 2022* (with the **.NET Desktop Development** workload checked).
    *   *JetBrains Rider*.
    *   *VS Code* (with the **C# Dev Kit** and **Avalonia UI** extensions installed).

---

### 2. Clone the Repository
Open a terminal and run:
```bash
git clone <repo-url>
cd BlueJayPlayer
```

---

### 3. Setup the Native Playback Engine (libmpv)
BlueJay Player uses the raw C API of `libmpv` for high-performance decoding and rendering.
1.  **Download the DLL**:
    *   Go to [mpv-winbuilds (shinchiro)](https://mpv.smarterplay.vip/) or shinchiro's [SourceForge page](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/).
    *   Download the **64-bit `libmpv` shared library** (usually named `mpv-dev-x86_64-YYYYMMDD-git-XXXXXXX.7z` or similar). Do *not* download the standalone `mpv.exe` player application.
2.  **Place the DLL in the Project**:
    *   In the root of the cloned `BlueJayPlayer` directory, create a folder named `Libs` (next to `BlueJayPlayer.csproj`).
    *   Extract `mpv-2.dll` (or `libmpv.dll`) from the downloaded archive.
    *   **Rename the file to `libmpv-2.dll`** (if it isn't already named exactly that).
    *   Place it directly inside your `Libs` folder: `BlueJayPlayer/Libs/libmpv-2.dll`.
3.  **Note on ffmpeg**:
    *   You do **not** need to install ffmpeg separately! All necessary ffmpeg libraries are statically compiled directly inside the `libmpv-2.dll` binary itself.

---

### 4. Setup Optional Test Media
*   The project is configured to look for a sample video named `test.mp4` in the project root for verification tests. You can copy any video file, rename it to `test.mp4`, and place it in the root folder next to `BlueJayPlayer.csproj`.

---

### 5. Build and Run
You can build and run the player using your IDE, or from the command line in the project root folder:
```powershell
dotnet build
dotnet run
```

---

## 🎨 Theme & Architecture Details
*   **Core UI**: Avalonia UI 11 with custom Nocturne Deep Blue vector styling templates.
*   **Playback Core**: Direct raw P/Invoke bindings to the unmanaged `libmpv-2.dll` C-interface.
*   **Media Cache/OSD**: Interactive sub-elements (timeline, sliders, floating title, telemetry diagnostics) overlayed directly onto the video surface canvas.
