using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using BlueJayPlayer.Controls;
using Avalonia.Interactivity;

namespace BlueJayPlayer.Views;

public partial class MainWindow : Window
{
    [DllImport("libmpv-2.dll", EntryPoint = "mpv_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MpvCreate();

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_initialize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvInitialize(IntPtr handle);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_set_option_string", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvSetOptionString(IntPtr handle, byte[] name, byte[] value);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_command", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvCommand(IntPtr handle, IntPtr utf8Args);

    private IntPtr _mpvHandle;
    private bool _isEngineInitialized = false;
    private string? _pendingFileToLoad;
    private string _logPath = Path.Combine(AppContext.BaseDirectory, "bluejay_debug.log");

    public MainWindow()
    {
        InitializeComponent();
        
        var videoSurface = this.FindControl<MpvVideoSurface>("VideoSurface");
        if (videoSurface != null)
        {
            videoSurface.HandleReady += InitializeRawEngine;
        }

        var dropPanel = this.FindControl<Panel>("MainDropPanel");
        if (dropPanel != null)
        {
            dropPanel.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            dropPanel.AddHandler(DragDrop.DropEvent, OnFileDropped);
        }

        // Change Tunneling to Tunnel
        this.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    private void Log(string msg) => File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}" + Environment.NewLine);

    public void QueueStartupFile(string filePath)
    {
        if (_isEngineInitialized)
        {
            PlayMediaFile(filePath);
        }
        else
        {
            _pendingFileToLoad = filePath;
        }
    }

    private void InitializeRawEngine(IntPtr hwnd)
    {
        if (_isEngineInitialized) return;

        try
        {
            Log("Booting raw native interop engine...");
            _mpvHandle = MpvCreate();
            if (_mpvHandle == IntPtr.Zero) return;

            SetMpvOptionString(_mpvHandle, "wid", hwnd.ToInt64().ToString());
            SetMpvOptionString(_mpvHandle, "hwdec", "auto");

            int initResult = MpvInitialize(_mpvHandle);
            Log($"Engine initialization code returned: {initResult}");

            if (initResult == 0)
            {
                _isEngineInitialized = true;

                if (!string.IsNullOrEmpty(_pendingFileToLoad))
                {
                    PlayMediaFile(_pendingFileToLoad);
                }
                else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "test.mp4")))
                {
                    PlayMediaFile(Path.Combine(AppContext.BaseDirectory, "test.mp4"));
                }
            }
        }
        catch (Exception ex) { Log($"Initialization Fatal: {ex.Message}"); }
    }

    private void PlayMediaFile(string filePath)
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        var placeholder = this.FindControl<TextBlock>("PlaceholderText");
        if (placeholder != null) placeholder.IsVisible = false;

        Log($"Sending loadfile command for: {filePath}");
        SendCommand(_mpvHandle, "loadfile", filePath);
    }

    private void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!_isEngineInitialized || _mpvHandle == IntPtr.Zero) return;

        // Handle Fullscreen natively in Avalonia to bypass unmanaged Win32 window containment rules
        if (e.Key == Key.F)
        {
            e.Handled = true;
            Log($"Toggling UI WindowState from: {this.WindowState}");
            
            if (this.WindowState == WindowState.FullScreen)
            {
                this.WindowState = WindowState.Normal;
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            else
            {
                this.WindowState = WindowState.FullScreen;
            }
            return;
        }

        // Map the remaining physical playback controls directly to low-level mpv actions
        (string command, string? argument) = e.Key switch
        {
            Key.Space => ("cycle", "pause"),
            Key.Right => ("seek", "5"),
            Key.Left  => ("seek", "-5"),
            Key.Up    => ("add", "volume 2"),
            Key.Down  => ("add", "volume -2"),
            _         => (string.Empty, null)
        };

        if (!string.IsNullOrEmpty(command))
        {
            e.Handled = true; // Mark the input event consumed completely
            Log($"Executing native action directly for {e.Key}: {command} {argument}");
            
            if (argument != null)
            {
                SendCommand(_mpvHandle, command, argument);
            }
            else
            {
                SendCommand(_mpvHandle, command);
            }
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnFileDropped(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        var files = e.DataTransfer.TryGetFiles();
        if (files != null)
        {
            var firstFile = files.FirstOrDefault();
            if (firstFile != null && File.Exists(firstFile.Path.LocalPath))
            {
                PlayMediaFile(firstFile.Path.LocalPath);
            }
        }
    }

    private static void SetMpvOptionString(IntPtr mpvHandle, string name, string value)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        byte[] valueBytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        MpvSetOptionString(mpvHandle, nameBytes, valueBytes);
    }

    private static void SendCommand(IntPtr mpvHandle, params string[] args)
    {
        IntPtr[] ptrArray = new IntPtr[args.Length + 1];
        for (int i = 0; i < args.Length; i++)
        {
            ptrArray[i] = Marshal.StringToHGlobalAnsi(args[i]);
        }
        ptrArray[args.Length] = IntPtr.Zero;

        IntPtr unmanagedArray = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(IntPtr)) * ptrArray.Length);
        Marshal.Copy(ptrArray, 0, unmanagedArray, ptrArray.Length);

        MpvCommand(mpvHandle, unmanagedArray);

        for (int i = 0; i < args.Length; i++)
        {
            Marshal.FreeHGlobal(ptrArray[i]);
        }
        Marshal.FreeHGlobal(unmanagedArray);
    }
}
