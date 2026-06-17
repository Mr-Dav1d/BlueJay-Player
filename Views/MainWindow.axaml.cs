using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using BlueJayPlayer.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace BlueJayPlayer.Views;

public partial class MainWindow : Window
{
    // --- Native P/Invoke Declarations ---
    [DllImport("libmpv-2.dll", EntryPoint = "mpv_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MpvCreate();

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_initialize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvInitialize(IntPtr handle);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_terminate_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void MpvTerminateDestroy(IntPtr handle);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_set_option_string", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvSetOptionString(IntPtr handle, byte[] name, byte[] value);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_command", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvCommand(IntPtr handle, IntPtr utf8Args);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_wait_event", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MpvWaitEvent(IntPtr handle, double timeout);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_observe_property", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvObserveProperty(IntPtr handle, ulong replyId, IntPtr namePtr, int format);

    // --- Native Event Struct Layouts ---
    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyId;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEventProperty
    {
        public IntPtr Name;
        public int Format;
        public IntPtr Data;
    }

    private IntPtr _mpvHandle;
    private IntPtr _cachedHwnd = IntPtr.Zero;
    private bool _isEngineInitialized = false;
    private string? _pendingFileToLoad;
    private string _logPath = Path.Combine(AppContext.BaseDirectory, "bluejay_debug.log");
    private CancellationTokenSource? _eventLoopCts;

    private int _lastLoggedSecond = -1;

    public MainWindow()
    {
        InitializeComponent();
        
        var videoSurface = this.FindControl<MpvVideoSurface>("VideoSurface");
        if (videoSurface != null)
        {
            // 1. Cache the handle pointer safely instead of initializing immediately
            videoSurface.HandleReady += (hwnd) =>
            {
                _cachedHwnd = hwnd;
            };
        }

        var dropPanel = this.FindControl<Panel>("MainDropPanel");
        if (dropPanel != null)
        {
            dropPanel.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            dropPanel.AddHandler(DragDrop.DropEvent, OnFileDropped);
        }

        this.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    // 2. Override the Window Opening Lifecycle event hook
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_cachedHwnd != IntPtr.Zero)
        {
            // Offload the entire engine boot cycle to a background worker thread
            // This keeps the Avalonia window perfectly smooth and responsive
            Task.Run(() => InitializeRawEngine(_cachedHwnd));
        }
        else
        {
            Log("ERROR: Window opened but native control handle (HWND) was never captured.");
        }
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
            Log("Booting raw native interop engine with bidirectional event loops...");
            _mpvHandle = MpvCreate();
            if (_mpvHandle == IntPtr.Zero) return;

            SetMpvOptionString(_mpvHandle, "wid", hwnd.ToInt64().ToString());
            SetMpvOptionString(_mpvHandle, "hwdec", "auto");

            int initResult = MpvInitialize(_mpvHandle);
            Log($"Engine initialization code returned: {initResult}");

            if (initResult == 0)
            {
                _isEngineInitialized = true;

                // Safely allocate unmanaged strings to pass down to C++
                IntPtr timePropPtr = Marshal.StringToHGlobalAnsi("time-pos");
                IntPtr durationPropPtr = Marshal.StringToHGlobalAnsi("duration");
                IntPtr volumePropPtr = Marshal.StringToHGlobalAnsi("volume");

                // Format 5 = MPV_FORMAT_DOUBLE
                MpvObserveProperty(_mpvHandle, 101, timePropPtr, 5);
                MpvObserveProperty(_mpvHandle, 102, durationPropPtr, 5);
                MpvObserveProperty(_mpvHandle, 103, volumePropPtr, 5);

                Marshal.FreeHGlobal(timePropPtr);
                Marshal.FreeHGlobal(durationPropPtr);
                Marshal.FreeHGlobal(volumePropPtr);

                _eventLoopCts = new CancellationTokenSource();
                Task.Run(() => RunEventLoop(_eventLoopCts.Token));

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

    private void RunEventLoop(CancellationToken token)
    {
        Log("Background event property observation loop spawned successfully.");
        
        while (!token.IsCancellationRequested && _mpvHandle != IntPtr.Zero)
        {
            IntPtr eventPtr = MpvWaitEvent(_mpvHandle, 0.02);
            if (eventPtr == IntPtr.Zero) continue;

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPtr);
            if (mpvEvent.EventId == 0) continue; 

            // FIXED: Handle Property Changed Notifications using correct ID 22
            if (mpvEvent.EventId == 22) // MPV_EVENT_PROPERTY_CHANGE
            {
                if (mpvEvent.Data == IntPtr.Zero) continue;

                var prop = Marshal.PtrToStructure<MpvEventProperty>(mpvEvent.Data);
                string propName = Marshal.PtrToStringAnsi(prop.Name) ?? string.Empty;

                // FIXED: Check for Format 5 (MPV_FORMAT_DOUBLE)
                if (prop.Format == 5 && prop.Data != IntPtr.Zero)
                {
                    double doubleVal = Marshal.PtrToStructure<double>(prop.Data);
                    ProcessObservedProperty(propName, doubleVal);
                }
            }
        }
    }

    private void ProcessObservedProperty(string propertyName, double value)
    {
        switch (propertyName)
        {
            case "time-pos":
                int currentSecond = (int)Math.Floor(value);
                if (currentSecond != _lastLoggedSecond)
                {
                    _lastLoggedSecond = currentSecond;
                    LogEngineEvent($"Playback Engine Time Tick: {currentSecond}s");
                }
                break;

            case "duration":
                LogEngineEvent($"Playback Engine Track Total Duration: {value:F2}s");
                break;

            case "volume":
                LogEngineEvent($"Playback Engine Master Volume Altered: {value:F0}%");
                break;
        }
    }

    private void LogEngineEvent(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Log(message);
        });
    }

    private void PlayMediaFile(string filePath)
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        // Use the dispatcher to safely update UI components from our background process
        Dispatcher.UIThread.Post(() =>
        {
            var placeholder = this.FindControl<TextBlock>("PlaceholderText");
            if (placeholder != null) placeholder.IsVisible = false;
        });

        Log($"Sending loadfile command for: {filePath}");
        SendCommand(_mpvHandle, "loadfile", filePath);
    }

    private void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!_isEngineInitialized || _mpvHandle == IntPtr.Zero) return;

        if (e.Key == Key.F)
        {
            e.Handled = true;
            this.WindowState = this.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            Log($"Toggled Fullscreen Mode. State is now: {this.WindowState}");
            return;
        }

        bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        (string command, string? argument) = e.Key switch
        {
            Key.Space => ("cycle", "pause"),
            Key.Right => ("seek", isShiftPressed ? "1" : "5"),
            Key.Left  => ("seek", isShiftPressed ? "-1" : "-5"),
            Key.Up    => ("add", "volume,2"),
            Key.Down  => ("add", "volume,-2"),
            Key.M     => ("cycle", "mute"),
            Key.A     => ("cycle", "audio"),
            Key.V     => ("cycle", "sub-visibility"),
            Key.Z     => ("add", "sub-delay -0.1"),
            Key.X     => ("add", "sub-delay 0.1"),
            _         => (string.Empty, null)
        };

        if (!string.IsNullOrEmpty(command))
        {
            e.Handled = true;
            Log($"Executing native action directly for {e.Key} (Shift: {isShiftPressed}): {command} {argument}");
            
            if (argument != null)
            {
                // If we passed a comma-separated multi-argument, split it into discrete array parts
                if (argument.Contains(','))
                {
                    var parts = argument.Split(',');
                    // Prepend the base command string to the front of our argument pieces
                    var fullArgs = new[] { command }.Concat(parts).ToArray();
                    SendCommand(_mpvHandle, fullArgs);
                }
                else
                {
                    SendCommand(_mpvHandle, command, argument);
                }
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

    private static void SetMpvOptionString(IntPtr mpvHandle, string name, string value)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        byte[] valueBytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        MpvSetOptionString(mpvHandle, nameBytes, valueBytes);
    }

    private void SetEngineProperty(string propertyName, string value)
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;
        
        Log($"Setting native property choice -> {propertyName} : {value}");
        SetMpvOptionString(_mpvHandle, propertyName, value);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Log("MainWindow closing detected. Initiating native engine teardown sequence...");
        
        // 1. Signal the background C# thread loop to stop parsing events immediately
        _eventLoopCts?.Cancel();

        // 2. Safely tell libmpv to destroy its unmanaged handles and clear memory hooks
        if (_mpvHandle != IntPtr.Zero)
        {
            IntPtr localHandle = _mpvHandle;
            _mpvHandle = IntPtr.Zero; // Prevent any late racing callbacks from using it
            _isEngineInitialized = false;

            // Force libmpv to cleanly release its Win32 window attachments and resources
            MpvTerminateDestroy(localHandle);
            Log("Native mpv core handle successfully terminated and destroyed.");
        }

        base.OnClosing(e);
    }
}
