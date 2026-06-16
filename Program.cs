using System;
using Avalonia;

namespace BlueJayPlayer;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Environment.SetEnvironmentVariable("PATH", baseDir + ";" + Environment.GetEnvironmentVariable("PATH"));

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
