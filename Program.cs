using System;
using System.IO;
using System.Threading;
using System.Text.Json;
using Avalonia;

namespace BlueJayPlayer;

class Program
{
    private static Mutex? _appMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Environment.SetEnvironmentVariable("PATH", baseDir + ";" + Environment.GetEnvironmentVariable("PATH"));

        // Check if settings.json disables multiple instances
        bool allowMultiple = true;
        try
        {
            string settingsPath = Path.Combine(baseDir, "settings.json");
            if (File.Exists(settingsPath))
            {
                string json = File.ReadAllText(settingsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("AllowMultipleInstances", out var prop))
                {
                    allowMultiple = prop.GetBoolean();
                }
            }
        }
        catch { /* Fallback to allowing multiple instances if settings load fails */ }

        if (!allowMultiple)
        {
            _appMutex = new Mutex(true, "BlueJayPlayerUniqueMutex", out bool createdNew);
            if (!createdNew)
            {
                // Already running! Exit program immediately.
                return;
            }
        }

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (_appMutex != null)
            {
                _appMutex.ReleaseMutex();
                _appMutex.Dispose();
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
