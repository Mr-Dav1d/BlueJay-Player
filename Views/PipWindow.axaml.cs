using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace BlueJayPlayer.Views;

public partial class PipWindow : Window
{
    public event EventHandler? RedockRequested;
    public event EventHandler? TogglePlayPauseRequested;
    public event EventHandler? SkipBackwardRequested;
    public event EventHandler? SkipForwardRequested;
    public event EventHandler<double>? SeekRequested;

    public bool IsShuttingDown { get; set; }
    public Action<IntPtr>? SurfaceHandleReady { get; set; }
    public IntPtr ChildSurfaceHandle { get; private set; }
    public double AspectRatio { get; set; } = 16.0 / 9.0;

    private static readonly Geometry PlayIconSvg = StreamGeometry.Parse("M8 5v14l11-7z");
    private static readonly Geometry PauseIconSvg = StreamGeometry.Parse("M6 19h4V5H6v14zm8-14v14h4V5h-4z");

    private bool _isUserSeekingInPip;

    // Win32 Subclassing P/Invokes
    public delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private SubclassProc? _subclassProc;
    private const uint WM_SIZING = 0x0214;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public PipWindow()
    {
        InitializeComponent();

        PipVideoSurface.HandleReady = (hwnd) =>
        {
            ChildSurfaceHandle = hwnd;
            PipLogger.Log($"PipWindow: SurfaceHandleReady callback triggered with hwnd = {hwnd}");
            SurfaceHandleReady?.Invoke(hwnd);
        };

        PipPlayPauseButton.Click += (_, _) => TogglePlayPauseRequested?.Invoke(this, EventArgs.Empty);
        PipSkipBack10Button.Click += (_, _) => SkipBackwardRequested?.Invoke(this, EventArgs.Empty);
        PipSkipForward10Button.Click += (_, _) => SkipForwardRequested?.Invoke(this, EventArgs.Empty);
        PipRedockButton.Click += (_, _) => TriggerRedock();
        PipCloseButton.Click += (_, _) => TriggerRedock();

        PipPlaybackSlider.AddHandler(
            InputElement.PointerPressedEvent,
            (object? sender, PointerPressedEventArgs e) =>
            {
                _isUserSeekingInPip = true;
                e.Pointer.Capture(PipPlaybackSlider);
                PipLogger.Log("PipWindow: PipPlaybackSlider drag started, pointer captured");

                // Immediately seek on pointer press
                var point = e.GetCurrentPoint(PipPlaybackSlider);
                if (point.Properties.IsLeftButtonPressed && PipPlaybackSlider.Bounds.Width > 0)
                {
                    double pct = point.Position.X / PipPlaybackSlider.Bounds.Width;
                    pct = Math.Max(0, Math.Min(1, pct));
                    double newValue = PipPlaybackSlider.Minimum + pct * (PipPlaybackSlider.Maximum - PipPlaybackSlider.Minimum);
                    PipPlaybackSlider.Value = newValue;
                }
            },
            RoutingStrategies.Tunnel);

        PipPlaybackSlider.AddHandler(
            InputElement.PointerReleasedEvent,
            (object? sender, PointerReleasedEventArgs e) =>
            {
                if (!_isUserSeekingInPip) return;
                e.Pointer.Capture(null);
                _isUserSeekingInPip = false;
                SeekRequested?.Invoke(this, PipPlaybackSlider.Value);
                PipLogger.Log($"PipWindow: PipPlaybackSlider drag released, dispatched seek to {PipPlaybackSlider.Value}");
            },
            RoutingStrategies.Tunnel);

        PipPlaybackSlider.AddHandler(
            InputElement.PointerCaptureLostEvent,
            (object? sender, PointerCaptureLostEventArgs e) =>
            {
                if (!_isUserSeekingInPip) return;
                _isUserSeekingInPip = false;
                SeekRequested?.Invoke(this, PipPlaybackSlider.Value);
                PipLogger.Log($"PipWindow: PipPlaybackSlider drag capture lost, fallback seek to {PipPlaybackSlider.Value}");
            },
            RoutingStrategies.Tunnel);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var handle = this.TryGetPlatformHandle();
            if (handle != null && handle.Handle != IntPtr.Zero)
            {
                PipLogger.Log($"PipWindow: OnOpened - Subclassing hWnd = {handle.Handle}");
                _subclassProc = WndProcSubclass;
                SetWindowSubclass(handle.Handle, _subclassProc, (IntPtr)1, IntPtr.Zero);
            }
        }
    }

    private IntPtr WndProcSubclass(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_SIZING)
        {
            try
            {
                RECT rect = Marshal.PtrToStructure<RECT>(lParam);
                
                double aspect = AspectRatio;
                if (double.IsNaN(aspect) || double.IsInfinity(aspect) || aspect <= 0)
                {
                    aspect = 16.0 / 9.0;
                }

                int edge = (int)wParam;
                switch (edge)
                {
                    case 1: // WMSZ_LEFT
                    case 2: // WMSZ_RIGHT
                        rect.Bottom = rect.Top + (int)Math.Round((rect.Right - rect.Left) / aspect);
                        break;

                    case 3: // WMSZ_TOP
                    case 5: // WMSZ_TOPRIGHT
                    case 6: // WMSZ_BOTTOM
                        rect.Right = rect.Left + (int)Math.Round((rect.Bottom - rect.Top) * aspect);
                        break;

                    case 4: // WMSZ_TOPLEFT
                    case 7: // WMSZ_BOTTOMLEFT
                        rect.Left = rect.Right - (int)Math.Round((rect.Bottom - rect.Top) * aspect);
                        break;

                    case 8: // WMSZ_BOTTOMRIGHT
                        rect.Bottom = rect.Top + (int)Math.Round((rect.Right - rect.Left) / aspect);
                        break;
                }

                Marshal.StructureToPtr(rect, lParam, false);
                return IntPtr.Zero; // Sizing processed
            }
            catch (Exception ex)
            {
                PipLogger.Log($"PipWindow: WndProcSubclass WM_SIZING exception: {ex.Message}");
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Bypass move-drag hijacking if clicking interactive controls (Buttons, Sliders, etc.)
        var source = e.Source as Avalonia.Visual;
        while (source != null && source != this)
        {
            if (source is Button || source is Slider)
            {
                return;
            }
            source = source.GetVisualParent();
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (IsShuttingDown)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _subclassProc != null)
            {
                var handle = this.TryGetPlatformHandle();
                if (handle != null && handle.Handle != IntPtr.Zero)
                {
                    PipLogger.Log($"PipWindow: OnClosing (Shutdown) - Unsubclassing hWnd = {handle.Handle}");
                    RemoveWindowSubclass(handle.Handle, _subclassProc, (IntPtr)1);
                    _subclassProc = null;
                }
            }
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        TriggerRedock();
    }

    private void TriggerRedock()
    {
        RedockRequested?.Invoke(this, EventArgs.Empty);
        this.Hide();
    }

    public void UpdatePlayState(bool isPlaying)
    {
        PipPlayPauseIcon.Data = isPlaying ? PauseIconSvg : PlayIconSvg;
    }

    public void UpdateTime(double currentSeconds, double totalSeconds, string title)
    {
        if (!string.IsNullOrEmpty(title))
        {
            PipTitleText.Text = title;
        }

        if (!_isUserSeekingInPip)
        {
            if (totalSeconds > 0)
            {
                PipPlaybackSlider.Maximum = totalSeconds;
                PipPlaybackSlider.Value = currentSeconds;
            }
            else
            {
                PipPlaybackSlider.Maximum = 1;
                PipPlaybackSlider.Value = 0;
            }

            string currentStr = TimeSpan.FromSeconds(currentSeconds).ToString(@"mm\:ss");
            string totalStr = TimeSpan.FromSeconds(totalSeconds).ToString(@"mm\:ss");
            PipTimeText.Text = $"{currentStr} / {totalStr}";
        }
    }

    private void OnHudPointerEntered(object? sender, PointerEventArgs e)
    {
        HoverControlHud.Opacity = 1.0;
    }

    private void OnHudPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isUserSeekingInPip)
        {
            HoverControlHud.Opacity = 0.0;
        }
    }
}

public static class PipLogger
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "pip_debug.log");
    private static readonly object LockObj = new object();

    public static void Log(string message)
    {
        try
        {
            lock (LockObj)
            {
                string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, logLine);
                System.Diagnostics.Debug.WriteLine(message);
            }
        }
        catch { }
    }
}
