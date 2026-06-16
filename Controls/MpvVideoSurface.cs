using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace BlueJayPlayer.Controls;

public class MpvVideoSurface : NativeControlHost
{
    // Expose an event so our MainWindow can know exactly when the HWND handle is ready
    public event Action<IntPtr>? HandleReady;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // Let Avalonia create the standard native Win32 container handle structure
        var handle = base.CreateNativeControlCore(parent);
        
        if (handle != null)
        {
            // Fire the handle address straight to our player engine initialization routine
            HandleReady?.Invoke(handle.Handle);
        }
        
        return handle;
    }
}
