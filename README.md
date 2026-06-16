## Pre-requisites & Setup Instructions

1. **Clone the Repository**
   * Run `git clone <repo-url>`

2. **Download the Native Playback Engine (Mandatory)**
   * The `libmpv` binary engine is excluded from source control via `.gitignore`.
   * Go to the official mpv installation directory (mpv.io/installation) or shinchiro's Windows builds archive.
   * Download the latest 64-bit **libmpv** architecture zip archive (do not download the standalone mpv.exe application).

3. **Place the Binary in the Workspace**
   * Extract the zip archive and locate the `libmpv-2.dll` (or `mpv-2.dll`) file.
   * Create a folder named `Libs` in the root of the project directory if it doesn't exist.
   * Paste the `.dll` file directly inside the `Libs/` folder so it matches the path: `BlueJayPlayer/Libs/libmpv-2.dll`.

4. **Restore and Build**
   * Open your terminal in the project root and run:
     ```bash
     dotnet restore
     dotnet run
     ```
