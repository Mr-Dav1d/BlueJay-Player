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

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_set_property_string", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvSetPropertyString(IntPtr handle, byte[] name, byte[] value);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_set_property", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MpvSetProperty(IntPtr handle, byte[] name, int format, ref double data);

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
    private Border? _osdCard;
    private TextBlock? _osdIcon;
    private TextBlock? _osdText;
    private System.Threading.CancellationTokenSource? _osdCts;

    // Greenfield Transport Control Fields
    private Border? _transportBar;
    private TextBlock? _currentTimeText;
    private TextBlock? _totalDurationText;
    private Slider? _playbackSlider;
    private System.Threading.CancellationTokenSource? _transportCts;
    private Button? _playPauseButton;
    private TextBlock? _playPauseIcon;
    private Slider? _volumeSlider;
    private TextBlock? _volumeIcon;
    private bool _isPaused;
    private bool _isMuted;
    private double _volumeBeforeMute = 100;
    private bool _isVolumeUpdatingFromMpv;

    // Layout References for Fullscreen Operations
    private Grid? _mainRootGrid;
    private Grid? _customTitleBar;
    private Border? _rootBorder;
    private MpvVideoSurface? _videoSurface;

    private int _lastLoggedSecond = -1;
    private double _playbackPosition;
    private double _playbackDuration;
    private bool _isUserSeeking;
    private bool _transportHandlersAttached;

    public MainWindow()
    {
        // InitializeComponent runs first to build the visual context tree from XAML
        InitializeComponent();
        
        // Cache Root Layout References safely now that the components are inflated
        _mainRootGrid = this.FindControl<Grid>("MainRootGrid");
        _customTitleBar = this.FindControl<Grid>("CustomTitleBar");
        _rootBorder = this.FindControl<Border>("RootBorder");

        // Custom title bar windows drag hook
        if (_customTitleBar != null)
        {
            _customTitleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    this.BeginMoveDrag(e);
                }
            };
        }

        // Window control buttons mappings
        var minBtn = this.FindControl<Button>("MinimizeButton");
        if (minBtn != null) minBtn.Click += (s, e) => this.WindowState = WindowState.Minimized;

        var maxBtn = this.FindControl<Button>("MaximizeButton");
        if (maxBtn != null)
        {
            maxBtn.Click += (s, e) =>
            {
                if (this.WindowState == WindowState.Maximized)
                {
                    this.WindowState = WindowState.Normal;
                    // Fix: Use the explicit Avalonia namespace path
                    if (_rootBorder != null) _rootBorder.CornerRadius = new Avalonia.CornerRadius(8);
                    Log("Window restored to standard desktop size layout.");
                }
                else
                {
                    this.WindowState = WindowState.Maximized;
                    // Fix: Use the explicit Avalonia namespace path
                    if (_rootBorder != null) _rootBorder.CornerRadius = new Avalonia.CornerRadius(0);
                    Log("Window maximized to fill available desktop workspace boundaries.");
                }
                // Explicitly force focus back to the window context container
                this.Focus();
            };
        }

        var closeBtn = this.FindControl<Button>("CloseButton");
        if (closeBtn != null) closeBtn.Click += (s, e) => this.Close();

        // Universal Global Drag and Drop Hooks
        var mainDropPanel = this.FindControl<Panel>("MainDropPanel");
        if (mainDropPanel != null)
        {
            DragDrop.AddDragOverHandler(mainDropPanel, (s, e) =>
            {
                e.Handled = true;
                e.DragEffects = DragDropEffects.Copy;
            });
            DragDrop.AddDropHandler(mainDropPanel, OnWindowFileDropped);
        }
        
        if (_rootBorder != null)
        {
            DragDrop.AddDragOverHandler(_rootBorder, (s, e) =>
            {
                e.Handled = true;
                e.DragEffects = DragDropEffects.Copy;
            });
            DragDrop.AddDropHandler(_rootBorder, OnWindowFileDropped);
        }

        // Fix: Attach pointer tracking to the outer window using a tunneling routing strategy
        this.AddHandler(InputElement.PointerMovedEvent, OnWindowPointerMovedTunnel, RoutingStrategies.Tunnel);

        _videoSurface = this.FindControl<MpvVideoSurface>("VideoSurface");
        if (_videoSurface != null)
        {
            _videoSurface.HandleReady = (hwnd) =>
            {
                _cachedHwnd = hwnd;
                Dispatcher.UIThread.Post(() =>
                {
                    ResolveTransportControls();
                });
            };
            _videoSurface.DropHandler = OnWindowFileDropped;
        }

        this.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    private void ResolveTransportControls()
    {
        if (_videoSurface == null)
        {
            Log("ResolveTransportControls: _videoSurface is null");
            return;
        }

        var hudPanel = _videoSurface.Content as Avalonia.Controls.Control;
        if (hudPanel == null)
        {
            Log("ResolveTransportControls: Content is null or not a Control");
            return;
        }

        _transportBar      = FindInTree<Border>(hudPanel, "TransportBar");
        _currentTimeText   = FindInTree<TextBlock>(hudPanel, "CurrentTimeText");
        _totalDurationText = FindInTree<TextBlock>(hudPanel, "TotalDurationText");
        _playbackSlider    = FindInTree<Slider>(hudPanel, "PlaybackSlider");
        _playPauseButton   = FindInTree<Button>(hudPanel, "PlayPauseButton");
        _playPauseIcon     = FindInTree<TextBlock>(hudPanel, "PlayPauseIcon");
        _volumeSlider      = FindInTree<Slider>(hudPanel, "VolumeSlider");
        _volumeIcon        = FindInTree<TextBlock>(hudPanel, "VolumeIcon");
        _osdCard           = FindInTree<Border>(hudPanel, "OsdCard");
        _osdIcon           = FindInTree<TextBlock>(hudPanel, "OsdIcon");
        _osdText           = FindInTree<TextBlock>(hudPanel, "OsdText");

        Log($"ResolveTransportControls: slider={_playbackSlider != null}, " +
            $"timeText={_currentTimeText != null}, durText={_totalDurationText != null}, " +
            $"transport={_transportBar != null}, osd={_osdCard != null}");

        if (_playbackSlider != null || _osdCard != null)
            AttachTransportHandlers();
        else
            Log("ResolveTransportControls: critical controls still null — overlay may not be ready yet");
    }

    private static T? FindInTree<T>(Avalonia.Controls.Control root, string name) where T : Avalonia.Controls.Control
    {
        if (root.Name == name && root is T match) return match;

        foreach (var child in Avalonia.LogicalTree.LogicalExtensions.GetLogicalChildren(root))
        {
            if (child is Avalonia.Controls.Control childControl)
            {
                var result = FindInTree<T>(childControl, name);
                if (result != null) return result;
            }
        }
        return null;
    }

    private void AttachTransportHandlers()
    {
        if (_transportHandlersAttached) return;
        _transportHandlersAttached = true;

        if (_playbackSlider != null)
        {
            _playbackSlider.AddHandler(
                InputElement.PointerPressedEvent,
                (_, _) => { _isUserSeeking = true; },
                RoutingStrategies.Tunnel);

            _playbackSlider.AddHandler(
                InputElement.PointerReleasedEvent,
                (_, _) =>
                {
                    if (!_isUserSeeking) return;
                    _isUserSeeking = false;
                    PerformSeekFromSlider();
                },
                RoutingStrategies.Tunnel);

            _playbackSlider.AddHandler(
                InputElement.PointerCaptureLostEvent,
                (_, _) =>
                {
                    if (!_isUserSeeking) return;
                    _isUserSeeking = false;
                    PerformSeekFromSlider();
                },
                RoutingStrategies.Tunnel);
        }

        if (_playPauseButton != null)
            _playPauseButton.Click += (_, _) => TogglePlayPause();

        var hudRoot = _videoSurface?.Content as Avalonia.Controls.Control;
        var skipBackBtn  = hudRoot != null ? FindInTree<Button>(hudRoot, "SkipBackButton")  : null;
        var skipFwdBtn   = hudRoot != null ? FindInTree<Button>(hudRoot, "SkipForwardButton") : null;
        var fullscreenBtn = hudRoot != null ? FindInTree<Button>(hudRoot, "FullscreenButton") : null;
        var muteBtn      = hudRoot != null ? FindInTree<Button>(hudRoot, "MuteButton")      : null;

        if (skipBackBtn != null)
            skipBackBtn.Click += (_, _) =>
            {
                if (_mpvHandle != IntPtr.Zero)
                    SendCommand(_mpvHandle, "seek", "-10", "relative");
            };

        if (skipFwdBtn != null)
            skipFwdBtn.Click += (_, _) =>
            {
                if (_mpvHandle != IntPtr.Zero)
                    SendCommand(_mpvHandle, "seek", "10", "relative");
            };

        if (fullscreenBtn != null)
            fullscreenBtn.Click += (_, _) => ToggleFullscreen();

        if (muteBtn != null)
            muteBtn.Click += (_, _) => ToggleMute();

        if (_volumeSlider != null)
        {
            _volumeSlider.Value = 100;
            _volumeSlider.ValueChanged += (_, e) =>
            {
                if (_isVolumeUpdatingFromMpv) return;
                if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;
                SetMpvPropertyDouble(_mpvHandle, "volume", e.NewValue);
            };
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        Task.Delay(500).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() => ResolveTransportControls());
        });

        if (_cachedHwnd != IntPtr.Zero)
        {
            Task.Run(() => InitializeRawEngine(_cachedHwnd));
        }
        else
        {
            Log("ERROR: Window opened but HWND was never captured.");
        }
    }

    private void Log(string msg)
    {
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}" + Environment.NewLine);
        }
        catch { /* swallow — never let logging crash the engine */ }
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
                IntPtr pausePropPtr = Marshal.StringToHGlobalAnsi("pause");

                // Format 5 = MPV_FORMAT_DOUBLE
                MpvObserveProperty(_mpvHandle, 101, timePropPtr, 5);
                MpvObserveProperty(_mpvHandle, 102, durationPropPtr, 5);
                MpvObserveProperty(_mpvHandle, 103, volumePropPtr, 5);
                // Format 6 = MPV_FORMAT_FLAG
                MpvObserveProperty(_mpvHandle, 104, pausePropPtr, 6);

                Marshal.FreeHGlobal(timePropPtr);
                Marshal.FreeHGlobal(durationPropPtr);
                Marshal.FreeHGlobal(volumePropPtr);
                Marshal.FreeHGlobal(pausePropPtr);

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
            IntPtr eventPtr = MpvWaitEvent(_mpvHandle, 1.0);
            if (eventPtr == IntPtr.Zero) continue;

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPtr);

            if (mpvEvent.EventId == 0) continue;

            if (mpvEvent.EventId == 22) // MPV_EVENT_PROPERTY_CHANGE
            {
                if (mpvEvent.Data == IntPtr.Zero) continue;

                var prop = Marshal.PtrToStructure<MpvEventProperty>(mpvEvent.Data);
                string propName = Marshal.PtrToStringAnsi(prop.Name) ?? string.Empty;

                if (prop.Format == 5 && prop.Data != IntPtr.Zero)
                {
                    double doubleVal = Marshal.PtrToStructure<double>(prop.Data);
                    ProcessObservedProperty(propName, doubleVal);
                }
                else if (prop.Format == 6 && prop.Data != IntPtr.Zero)
                {
                    int flagVal = Marshal.ReadInt32(prop.Data);
                    ProcessObservedPropertyFlag(propName, flagVal);
                }
            }
        }
        Log("Event loop exited.");
    }

    private void ProcessObservedPropertyFlag(string propertyName, int flagValue)
    {
        switch (propertyName)
        {
            case "pause":
                _isPaused = flagValue != 0;
                UpdatePlayPauseIcon();
                break;
        }
    }

    private void ProcessObservedProperty(string propertyName, double value)
    {
        switch (propertyName)
        {
            case "time-pos":
                if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= 0)
                {
                    int currentSecond = (int)Math.Floor(value);
                    if (currentSecond != _lastLoggedSecond)
                    {
                        _lastLoggedSecond = currentSecond;
                        LogEngineEvent($"Playback Engine Time Tick: {currentSecond}s");
                    }
                    _playbackPosition = value;
                    UpdateTransportUI(_playbackPosition, _playbackDuration);
                }
                break;

            case "duration":
                if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 0)
                {
                    LogEngineEvent($"Playback Engine Track Total Duration: {value:F2}s");
                    _playbackDuration = value;
                    UpdateTransportUI(_playbackPosition, _playbackDuration);
                }
                break;

            case "volume":
                if (double.IsNaN(value) || double.IsInfinity(value)) return;
                int roundedVol = (int)Math.Round(value);
                LogEngineEvent($"Playback Engine Master Volume Altered: {roundedVol}%");
                TriggerOsdHUD("🔊", $"Vol {roundedVol}%");
                Dispatcher.UIThread.Post(() =>
                {
                    if (_volumeSlider == null) return;
                    _isVolumeUpdatingFromMpv = true;
                    _volumeSlider.Value = value;
                    _isVolumeUpdatingFromMpv = false;
                    if (value > 0) _isMuted = false;
                    if (_volumeIcon != null)
                        _volumeIcon.Text = _isMuted ? "🔇" : "🔊";
                });
                break;
        }
    }

    private void UpdateTransportUI(double current, double total)
    {
        if (double.IsNaN(current) || double.IsInfinity(current) || current < 0) current = 0;
        if (double.IsNaN(total) || double.IsInfinity(total) || total < 0) total = 0;

        Dispatcher.UIThread.Post(() =>
        {
            if (_playbackSlider == null || _currentTimeText == null || _totalDurationText == null)
            {
                Log("UpdateTransportUI: one or more controls are null, skipping");
                return;
            }

            if (total > 0)
                _playbackSlider.Maximum = total;

            if (!_isUserSeeking)
                _playbackSlider.Value = current;

            _currentTimeText.Text = TimeSpan.FromSeconds(current).ToString(@"hh\:mm\:ss");
            _totalDurationText.Text = TimeSpan.FromSeconds(total).ToString(@"hh\:mm\:ss");
        });
    }

    private void PerformSeekFromSlider()
    {
        if (_playbackSlider == null || !_isEngineInitialized || _mpvHandle == IntPtr.Zero) return;

        double seekPos = _playbackSlider.Value;
        Log($"Transport seek command: seek {seekPos:F3} absolute");
        SendCommand(_mpvHandle, "seek", seekPos.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    private void TogglePlayPause()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        _isPaused = !_isPaused;
        SetMpvPropertyString(_mpvHandle, "pause", _isPaused ? "yes" : "no");
        UpdatePlayPauseIcon();
        TriggerOsdHUD(_isPaused ? "⏸" : "▶", _isPaused ? "Paused" : "Playing");
    }

    private void UpdatePlayPauseIcon()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_playPauseIcon != null)
                _playPauseIcon.Text = _isPaused ? "▶" : "⏸";
        });
    }

    private void ToggleMute()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        _isMuted = !_isMuted;
        if (_isMuted)
        {
            _volumeBeforeMute = _volumeSlider?.Value ?? 100;
            SetMpvPropertyDouble(_mpvHandle, "volume", 0);
        }
        else
        {
            SetMpvPropertyDouble(_mpvHandle, "volume", _volumeBeforeMute);
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_volumeIcon != null)
                _volumeIcon.Text = _isMuted ? "🔇" : "🔊";
        });
    }

    private void ToggleFullscreen()
    {
        if (this.WindowState == WindowState.FullScreen)
        {
            this.WindowState = WindowState.Normal;
            if (_customTitleBar != null) _customTitleBar.IsVisible = true;
            if (_mainRootGrid != null)
                _mainRootGrid.RowDefinitions = new RowDefinitions("32,*");
            if (_rootBorder != null) _rootBorder.CornerRadius = new Avalonia.CornerRadius(8);
            Log("Exited Fullscreen Mode: UI Frame Chrome Layout restored.");
            _videoSurface?.SyncOverlayGeometry();
        }
        else
        {
            this.WindowState = WindowState.FullScreen;
            if (_customTitleBar != null) _customTitleBar.IsVisible = false;
            if (_mainRootGrid != null)
                _mainRootGrid.RowDefinitions = new RowDefinitions("0,*");
            if (_rootBorder != null) _rootBorder.CornerRadius = new Avalonia.CornerRadius(0);
            Log("Entered Fullscreen Mode: UI Frame Chrome Layout completely hidden.");
            _videoSurface?.SyncOverlayGeometry();
        }
    }

    private void LogEngineEvent(string message)
    {
        Log(message);
    }

    private void OnWindowPointerMovedTunnel(object? sender, PointerEventArgs e)
    {
        WakeTransportHUD();
    }

    private void WakeTransportHUD()
    {
        if (_transportBar == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_transportBar == null) return;

            _transportCts?.Cancel();
            _transportCts = new System.Threading.CancellationTokenSource();
            var token = _transportCts.Token;

            if (_transportBar.Opacity < 1.0)
            {
                _transportBar.Opacity = 1.0;
                Log("HUD Layer Diagnostics: Intercepted pointer motion, bottom transport bar brought to full opacity.");
            }

            Task.Delay(2500, token).ContinueWith(t =>
            {
                if (!token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_transportBar != null)
                        {
                            _transportBar.Opacity = 0.0;
                            Log("HUD Layer Diagnostics: Idle timeout hit, bottom transport bar faded out.");
                        }
                    });
                }
            }, TaskContinuationOptions.None);
        });
    }

    private void TriggerOsdHUD(string glyph, string description)
    {
        // Ensure we marshal back to Avalonia's primary UI thread safely
        Dispatcher.UIThread.Post(() =>
        {
            if (_osdCard == null || _osdIcon == null || _osdText == null) return;

            // Cancel any existing active countdown timer currently running
            _osdCts?.Cancel();
            _osdCts = new System.Threading.CancellationTokenSource();
            var token = _osdCts.Token;

            // Update UI string parameters dynamically
            _osdIcon.Text = glyph;
            _osdText.Text = description;

            // Make the card cleanly visible using our XAML DoubleTransition property hook
            _osdCard.Opacity = 1.0;

            // Fire a safe background thread countdown to trigger the automatic fade away
        Task.Delay(1500, token).ContinueWith(t =>
            {
            // Fix: Read the token status directly instead of checking 't'
            if (!token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (_osdCard != null) _osdCard.Opacity = 0.0;
                    });
                }
            }, TaskContinuationOptions.None);
        });
    }

    private void PlayMediaFile(string filePath)
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized)
        {
            Log($"PlayMediaFile skipped — engine not ready (handle={_mpvHandle}, initialized={_isEngineInitialized}). File: {filePath}");
            return;
        }

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

        // 1. Fullscreen Engine & UI Toggle Layout Alignment
        if (e.Key == Key.F)
        {
            e.Handled = true;
            ToggleFullscreen();
            return;
        }

        // Capture control commands for key bindings
        string? command = null;
        string? argument = null;

        if (e.Key == Key.Space) { command = "cycle"; argument = "pause"; }
        else if (e.Key == Key.Left) { command = "seek"; argument = "-5"; }
        else if (e.Key == Key.Right) { command = "seek"; argument = "5"; }
        else if (e.Key == Key.Up) { command = "add"; argument = "volume,5"; }
        else if (e.Key == Key.Down) { command = "add"; argument = "volume,-5"; }

        if (!string.IsNullOrEmpty(command))
        {
            e.Handled = true;
            Log($"Executing native action directly for {e.Key}: {command} {argument}");
            
            // Fix: Space maps to command='cycle' and argument='pause'
            if (command == "cycle" && argument == "pause")
            {
                TriggerOsdHUD("▶", "Play / Pause");
            }

            if (argument != null)
            {
                if (argument.Contains(','))
                {
                    var parts = argument.Split(',');
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

    private void OnWindowFileDropped(object? sender, DragEventArgs e)
    {
        Log("Triggering drag-and-drop file payload evaluation process...");
        e.Handled = true;

        var files = e.DataTransfer.TryGetFiles();
        if (files == null)
        {
            Log("Dropped data packet structure diagnostic: TryGetFiles() returned NULL mapping.");
            return;
        }

        var firstFile = files.FirstOrDefault();
        if (firstFile == null)
        {
            Log("Dropped data packet structure diagnostic: File collection array is completely empty.");
            return;
        }

        string localPath = firstFile.Path.LocalPath;
        Log($"Successfully extracted drop file path target: '{localPath}'");

        if (string.IsNullOrEmpty(localPath))
        {
            Log("Dropped data error: Path parsed out to an empty string reference.");
            return;
        }

        // Log a warning instead of failing out silently if file system check flags it
        if (!File.Exists(localPath))
        {
            Log($"System IO Warning: File.Exists returned false for path: '{localPath}'. Proceeding to hand off to native engine anyway.");
        }

        PlayMediaFile(localPath);
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

    private static void SetMpvPropertyString(IntPtr mpvHandle, string name, string value)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        byte[] valueBytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        MpvSetPropertyString(mpvHandle, nameBytes, valueBytes);
    }

    private static void SetMpvPropertyDouble(IntPtr mpvHandle, string name, double value)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        MpvSetProperty(mpvHandle, nameBytes, 5, ref value);
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
        _osdCts?.Cancel();
        _transportCts?.Cancel();

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
