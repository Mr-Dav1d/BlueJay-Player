using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using BlueJayPlayer.Controls;

namespace BlueJayPlayer.Views;

public partial class MainWindow : Window
{
    [DllImport("libmpv-2.dll", EntryPoint = "mpv_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MpvCreate();

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_initialize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvInitialize(IntPtr handle);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_command", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvCommand(IntPtr handle, IntPtr utf8Args);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_set_option_string", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvSetOptionString(IntPtr handle, byte[] name, byte[] value);

    private IntPtr _mpvHandle;
    private bool _isEngineInitialized = false;
    private string? _pendingFileToLoad;

    public MainWindow()
    {
        InitializeComponent();
        
        // Securely listen for the native Win32 window handle allocation event
        var videoSurface = this.FindControl<MpvVideoSurface>("VideoSurface");
        if (videoSurface != null)
        {
            videoSurface.HandleReady += InitializeRawEngine;
        }

        // Setup clean top-level drag and drop handlers
        var dropPanel = this.FindControl<Panel>("MainDropPanel");
        if (dropPanel != null)
        {
            dropPanel.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            dropPanel.AddHandler(DragDrop.DropEvent, OnFileDropped);
        }
    }

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
            _mpvHandle = MpvCreate();
            if (_mpvHandle == IntPtr.Zero) return;

            // Bind directly to the safely extracted HWND container handle
            SetMpvOptionString(_mpvHandle, "wid", hwnd.ToInt64().ToString());
            SetMpvOptionString(_mpvHandle, "log-file", "mpv_engine_log.txt");
            SetMpvOptionString(_mpvHandle, "hwdec", "auto");

            int initResult = MpvInitialize(_mpvHandle);

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
        catch (Exception) { }
    }

    private void PlayMediaFile(string filePath)
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        var placeholder = this.FindControl<TextBlock>("PlaceholderText");
        if (placeholder != null) placeholder.IsVisible = false;

        SendCommand(_mpvHandle, "loadfile", filePath);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnFileDropped(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        // Clean, warning-free .NET 9 storage reference extraction API
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
