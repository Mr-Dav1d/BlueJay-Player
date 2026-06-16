using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace BlueJayPlayer.Controls;

public class MpvVideoSurface : NativeControlHost
{
    public event Action<IntPtr>? HandleReady;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        
        if (handle != null)
        {
            HandleReady?.Invoke(handle.Handle);
            return handle;
        }
        
        // Fallback to prevent compiler null warnings if a handle creation anomaly occurs
        throw new InvalidOperationException("Failed to allocate native Win32 container surface handles.");
    }
}
