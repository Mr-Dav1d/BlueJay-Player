using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BlueJayPlayer.ViewModels;
using BlueJayPlayer.Views;

namespace BlueJayPlayer;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // Intercept any path passed via the terminal arguments
            string? targetFile = desktop.Args?.FirstOrDefault();
            if (!string.IsNullOrEmpty(targetFile))
            {
                mainWindow.QueueStartupFile(targetFile);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
