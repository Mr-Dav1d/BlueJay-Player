using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace BlueJayPlayer.Views;

public partial class MainWindow : Window
{
    // Core function signatures imported straight from libmpv-2.dll
    [DllImport("libmpv-2.dll", EntryPoint = "mpv_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MpvCreate();

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_initialize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvInitialize(IntPtr handle);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_command", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvCommand(IntPtr handle, IntPtr utf8Args);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_set_option_string", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvSetOptionString(IntPtr handle, byte[] name, byte[] value);

    private IntPtr _mpvHandle;

    public MainWindow()
    {
        InitializeComponent();
        this.Opened += (s, e) => InitializeRawEngine();
    }

    private void InitializeRawEngine()
    {
        string logPath = Path.Combine(AppContext.BaseDirectory, "bluejay_debug.log");
        void Log(string msg) => File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}" + Environment.NewLine);

        try
        {
            Log("Booting raw native interop engine...");
            _mpvHandle = MpvCreate();
            
            if (_mpvHandle == IntPtr.Zero)
            {
                Log("CRITICAL: Could not allocate memory handle.");
                return;
            }

            // Standardized way to fetch the native HWND handle on modern Avalonia versions
            var platformHandle = this.TryGetPlatformHandle();
            if (platformHandle != null)
            {
                long hwnd = platformHandle.Handle.ToInt64();
                Log($"Acquired native window handle (HWND): {hwnd}");

                // Embed mpv directly into our window container surface
                SetMpvOptionString(_mpvHandle, "wid", hwnd.ToString());
            }
            else
            {
                Log("WARNING: Could not resolve Windows native platform handle container.");
            }

            // Set up diagnostic engine log writing
            SetMpvOptionString(_mpvHandle, "log-file", "mpv_engine_log.txt");
            SetMpvOptionString(_mpvHandle, "hwdec", "auto");

            // Initialize engine
            int initResult = MpvInitialize(_mpvHandle);
            Log($"Engine initialization code returned: {initResult}");

            if (initResult == 0)
            {
                Log("Native player engine connected. Sending loadfile command...");
                
                // Fire off playback command strings safely
                SendCommand(_mpvHandle, "loadfile", "test.mp4");
                Log("Playback command sent!");
            }
        }
        catch (Exception ex)
        {
            Log($"Native Error: {ex.Message}");
            Log(ex.ToString());
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
