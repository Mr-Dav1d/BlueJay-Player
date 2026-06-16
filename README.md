### Pre-requisites & Setup Instructions

1. **Clone the Repository**
   ```bash
   git clone <repo-url>
   cd BlueJayPlayer
   ```

2. **Download the Native Playback Engine**
   - Go to the official mpv installation directory or shinchiro's Windows builds archive.
   - Download the latest 64-bit libmpv V2 architecture zip archive (Ensure it is `libmpv-2.dll`, not the standalone player application `mpv.exe`).

3. **Place the Binary in the Workspace**
   - Create a folder named `Libs` in the root of the project directory.
   - Paste the `libmpv-2.dll` file directly inside that folder: `BlueJayPlayer/Libs/libmpv-2.dll`.
   - Drop a sample video file named `test.mp4` directly into your main project folder right next to your `.csproj` file.

4. **Verify Project Deployment Rules**
   Ensure the bottom of your `BlueJayPlayer.csproj` file includes the deployment directives to copy the native library, test media, and player configurations to the executable root directory on compile:

   ```xml
   <ItemGroup>
     <None Include="Libs\libmpv-2.dll">
       <Link>libmpv-2.dll</Link>
       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
     </None>
     
     <None Update="input.conf">
       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
     </None>
     
     <None Include="test.mp4">
       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
     </None>
   </ItemGroup>
   ```

5. **Build and Run**
   ```powershell
   dotnet build
   dotnet run
   ```
