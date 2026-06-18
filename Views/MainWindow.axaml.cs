using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using BlueJayPlayer.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;

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

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_get_property_string", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MpvGetPropertyString(IntPtr handle, byte[] name);

    [DllImport("libmpv-2.dll", EntryPoint = "mpv_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void MpvFree(IntPtr data);

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
    private TextBlock[] _currentTimeTexts = Array.Empty<TextBlock>();
    private TextBlock[] _totalDurationTexts = Array.Empty<TextBlock>();
    private Slider[] _playbackSliders = Array.Empty<Slider>();
    private Button[] _playPauseButtons = Array.Empty<Button>();
    private PathIcon[] _playPauseIcons = Array.Empty<PathIcon>();
    private Slider[] _volumeSliders = Array.Empty<Slider>();
    private PathIcon[] _volumeIcons = Array.Empty<PathIcon>();
    private System.Threading.CancellationTokenSource? _transportCts;
    private bool _isPaused;
    private bool _isMuted;
    private double _volumeBeforeMute = 100;
    private bool _isVolumeUpdatingFromMpv;
    private Button? _singleModeSwitchButton;
    private Button? _deckModeSwitchButton;

    // Phase 2 loop, audio, and subtitle selectors
    private bool _isLooping = false;
    private Button[] _loopButtons = Array.Empty<Button>();
    private Button[] _audioButtons = Array.Empty<Button>();
    private Button[] _subtitleButtons = Array.Empty<Button>();
    private MenuFlyout[] _audioFlyouts = Array.Empty<MenuFlyout>();
    private MenuFlyout[] _subtitleFlyouts = Array.Empty<MenuFlyout>();

    // Layout References for Fullscreen Operations
    private Grid? _mainRootGrid;
    private Grid? _customTitleBar;
    private Border? _rootBorder;
    private MpvVideoSurface? _videoSurface;
    private Grid? _resizeGrid;
    private Grid? _overlayResizeGrid;

    private static readonly string PlaySvg = "M8 5v14l11-7z";
    private static readonly string PauseSvg = "M6 19h4V5H6v14zm8-14v14h4V5h-4z";
    private static readonly string VolumeHighSvg = "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z";
    private static readonly string VolumeMuteSvg = "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.21.05-.42.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.03c1.37-.33 2.61-.96 3.66-1.78L19.73 21 21 19.73 4.27 3zM12 4L9.91 6.09 12 8.18V4z";

    private int _lastLoggedSecond = -1;
    private double _playbackPosition;
    private double _playbackDuration;
    private bool _isUserSeeking;
    private bool _transportHandlersAttached;
    private DateTime _lastSeekTime = DateTime.MinValue;

    // Background preview engine fields
    private IntPtr _previewMpvHandle = IntPtr.Zero;
    private string? _previewTempDir;
    private System.Threading.CancellationTokenSource? _previewCts;
    private System.Threading.Timer? _debounceTimer;
    private double _hoveredSeekTime = -1;
    private Control? _hudPanel;
    private Border? _seekPreviewCard;

    public MainWindow()
    {
        // InitializeComponent runs first to build the visual context tree from XAML
        InitializeComponent();
        
        // Cache Root Layout References safely now that the components are inflated
        _mainRootGrid = this.FindControl<Grid>("MainRootGrid");
        _customTitleBar = this.FindControl<Grid>("CustomTitleBar");
        _rootBorder = this.FindControl<Border>("RootBorder");
        _resizeGrid = this.FindControl<Grid>("ResizeGrid");

        void ToggleMaximize()
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                if (_rootBorder != null) _rootBorder.CornerRadius = new Avalonia.CornerRadius(8);
                Log("Window restored to standard desktop size layout.");
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                if (_rootBorder != null) _rootBorder.CornerRadius = new Avalonia.CornerRadius(0);
                Log("Window maximized to fill available desktop workspace boundaries.");
            }
            this.Focus();
        }

        // Custom title bar windows drag hook
        if (_customTitleBar != null)
        {
            _customTitleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    if (e.ClickCount == 2)
                    {
                        ToggleMaximize();
                    }
                    else
                    {
                        this.BeginMoveDrag(e);
                    }
                }
            };
        }

        // Window control buttons mappings
        var minBtn = this.FindControl<Button>("MinimizeButton");
        if (minBtn != null) minBtn.Click += (s, e) => this.WindowState = WindowState.Minimized;

        var maxBtn = this.FindControl<Button>("MaximizeButton");
        if (maxBtn != null)
        {
            maxBtn.Click += (s, e) => ToggleMaximize();
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
        _hudPanel = hudPanel;

        _transportBar      = FindInTree<Border>(hudPanel, "TransportBar");
        if (_transportBar != null)
        {
            // Clear any accidental stale classes and force clean startup state tracking
            _transportBar.Classes.Remove("tactical-deck");
            if (!_transportBar.Classes.Contains("single-line"))
            {
                _transportBar.Classes.Add("single-line");
            }
        }
        
        var singleCurrentTimeText   = FindInTree<TextBlock>(hudPanel, "SingleCurrentTimeText");
        var deckCurrentTimeText     = FindInTree<TextBlock>(hudPanel, "DeckCurrentTimeText");
        _currentTimeTexts = new TextBlock[] { singleCurrentTimeText!, deckCurrentTimeText! }.Where(x => x != null).ToArray();

        var singleTotalDurationText = FindInTree<TextBlock>(hudPanel, "SingleTotalDurationText");
        var deckTotalDurationText   = FindInTree<TextBlock>(hudPanel, "DeckTotalDurationText");
        _totalDurationTexts = new TextBlock[] { singleTotalDurationText!, deckTotalDurationText! }.Where(x => x != null).ToArray();

        var singlePlaybackSlider    = FindInTree<Slider>(hudPanel, "SinglePlaybackSlider");
        var deckPlaybackSlider      = FindInTree<Slider>(hudPanel, "DeckPlaybackSlider");
        _playbackSliders = new Slider[] { singlePlaybackSlider!, deckPlaybackSlider! }.Where(x => x != null).ToArray();

        var singlePlayPauseButton   = FindInTree<Button>(hudPanel, "SinglePlayPauseButton");
        var deckPlayPauseButton     = FindInTree<Button>(hudPanel, "DeckPlayPauseButton");
        _playPauseButtons = new Button[] { singlePlayPauseButton!, deckPlayPauseButton! }.Where(x => x != null).ToArray();

        var singlePlayPauseIcon     = FindInTree<PathIcon>(hudPanel, "SinglePlayPauseIcon");
        var deckPlayPauseIcon       = FindInTree<PathIcon>(hudPanel, "DeckPlayPauseIcon");
        _playPauseIcons = new PathIcon[] { singlePlayPauseIcon!, deckPlayPauseIcon! }.Where(x => x != null).ToArray();

        var singleVolumeSlider      = FindInTree<Slider>(hudPanel, "SingleVolumeSlider");
        var deckVolumeSlider        = FindInTree<Slider>(hudPanel, "DeckVolumeSlider");
        _volumeSliders = new Slider[] { singleVolumeSlider!, deckVolumeSlider! }.Where(x => x != null).ToArray();

        var singleVolumeIcon        = FindInTree<PathIcon>(hudPanel, "SingleVolumeIcon");
        var deckVolumeIcon          = FindInTree<PathIcon>(hudPanel, "DeckVolumeIcon");
        _volumeIcons = new PathIcon[] { singleVolumeIcon!, deckVolumeIcon! }.Where(x => x != null).ToArray();

        var singleLoopBtn = FindInTree<Button>(hudPanel, "SingleLoopButton");
        var deckLoopBtn = FindInTree<Button>(hudPanel, "DeckLoopButton");
        _loopButtons = new Button[] { singleLoopBtn!, deckLoopBtn! }.Where(x => x != null).ToArray();

        var singleAudioBtn = FindInTree<Button>(hudPanel, "SingleAudioButton");
        var deckAudioBtn = FindInTree<Button>(hudPanel, "DeckAudioButton");
        _audioButtons = new Button[] { singleAudioBtn!, deckAudioBtn! }.Where(x => x != null).ToArray();
        _audioFlyouts = _audioButtons.Select(b => b.Flyout as MenuFlyout).Where(f => f != null).ToArray()!;

        var singleSubBtn = FindInTree<Button>(hudPanel, "SingleSubtitleButton");
        var deckSubBtn = FindInTree<Button>(hudPanel, "DeckSubtitleButton");
        _subtitleButtons = new Button[] { singleSubBtn!, deckSubBtn! }.Where(x => x != null).ToArray();
        _subtitleFlyouts = _subtitleButtons.Select(b => b.Flyout as MenuFlyout).Where(f => f != null).ToArray()!;

        _osdCard           = FindInTree<Border>(hudPanel, "OsdCard");
        _osdIcon           = FindInTree<TextBlock>(hudPanel, "OsdIcon");
        _osdText           = FindInTree<TextBlock>(hudPanel, "OsdText");
        _seekPreviewCard   = FindInTree<Border>(hudPanel, "SeekPreviewCard");
        _overlayResizeGrid = FindInTree<Grid>(hudPanel, "OverlayResizeGrid");
        if (_overlayResizeGrid != null)
        {
            _overlayResizeGrid.IsVisible = this.WindowState == WindowState.Normal;
        }

        Log($"ResolveTransportControls: sliders={_playbackSliders.Length}, " +
            $"timeTexts={_currentTimeTexts.Length}, durTexts={_totalDurationTexts.Length}, " +
            $"transport={_transportBar != null}, osd={_osdCard != null}");

        if (_playbackSliders.Length > 0 || _osdCard != null)
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

        foreach (var slider in _playbackSliders)
        {
            slider.SizeChanged += (s, e) => RenderChapterTicks();
            slider.PointerMoved += OnSliderPointerMoved;
            slider.PointerEntered += OnSliderPointerEntered;
            slider.PointerExited += OnSliderPointerExited;

            slider.AddHandler(
                InputElement.PointerPressedEvent,
                (object? sender, PointerPressedEventArgs e) =>
                {
                    _isUserSeeking = true;
                    var point = e.GetCurrentPoint(slider);
                    if (point.Properties.IsLeftButtonPressed && slider.Bounds.Width > 0)
                    {
                        double pct = point.Position.X / slider.Bounds.Width;
                        pct = Math.Max(0, Math.Min(1, pct));
                        double newValue = slider.Minimum + pct * (slider.Maximum - slider.Minimum);
                        slider.Value = newValue;
                        PerformSeekFromSlider(slider);
                    }
                },
                RoutingStrategies.Tunnel);

            slider.AddHandler(
                InputElement.PointerReleasedEvent,
                (_, _) =>
                {
                    if (!_isUserSeeking) return;
                    _isUserSeeking = false;
                    PerformSeekFromSlider(slider);
                },
                RoutingStrategies.Tunnel);

            slider.AddHandler(
                InputElement.PointerCaptureLostEvent,
                (_, _) =>
                {
                    if (!_isUserSeeking) return;
                    _isUserSeeking = false;
                    PerformSeekFromSlider(slider);
                },
                RoutingStrategies.Tunnel);
        }

        foreach (var btn in _playPauseButtons)
        {
            btn.Click += (_, _) => TogglePlayPause();
        }

        var hudRoot = _videoSurface?.Content as Avalonia.Controls.Control;

        var singleMuteBtn = hudRoot != null ? FindInTree<Button>(hudRoot, "SingleMuteButton") : null;
        var deckMuteBtn = hudRoot != null ? FindInTree<Button>(hudRoot, "DeckMuteButton") : null;
        var muteBtns = new Button[] { singleMuteBtn!, deckMuteBtn! }.Where(x => x != null);
        foreach (var muteBtn in muteBtns)
        {
            muteBtn.Click += (_, _) => ToggleMute();
        }

        var singleFullscreenBtn = hudRoot != null ? FindInTree<Button>(hudRoot, "SingleFullscreenButton") : null;
        var deckFullscreenBtn = hudRoot != null ? FindInTree<Button>(hudRoot, "DeckFullscreenButton") : null;
        var fullscreenBtns = new Button[] { singleFullscreenBtn!, deckFullscreenBtn! }.Where(x => x != null);
        foreach (var fsBtn in fullscreenBtns)
        {
            fsBtn.Click += (_, _) => ToggleFullscreen();
        }

        var deckSkipBackBtn = hudRoot != null ? FindInTree<Button>(hudRoot, "DeckSkipBackButton") : null;
        if (deckSkipBackBtn != null)
        {
            deckSkipBackBtn.Click += (_, _) =>
            {
                if (_mpvHandle != IntPtr.Zero)
                    SendCommand(_mpvHandle, "seek", "-10", "relative");
            };
        }

        var deckSkipFwdBtn = hudRoot != null ? FindInTree<Button>(hudRoot, "DeckSkipForwardButton") : null;
        if (deckSkipFwdBtn != null)
        {
            deckSkipFwdBtn.Click += (_, _) =>
            {
                if (_mpvHandle != IntPtr.Zero)
                    SendCommand(_mpvHandle, "seek", "10", "relative");
            };
        }

        _singleModeSwitchButton = hudRoot != null ? FindInTree<Button>(hudRoot, "SingleModeSwitchButton") : null;
        _deckModeSwitchButton = hudRoot != null ? FindInTree<Button>(hudRoot, "DeckModeSwitchButton") : null;
        if (_singleModeSwitchButton != null)
            _singleModeSwitchButton.Click += (_, _) => ToggleLayoutMode();
        if (_deckModeSwitchButton != null)
            _deckModeSwitchButton.Click += (_, _) => ToggleLayoutMode();

        foreach (var slider in _volumeSliders)
        {
            slider.Value = 100;
            slider.ValueChanged += (s, e) =>
            {
                if (_isVolumeUpdatingFromMpv) return;
                if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;
                
                // Sync the other slider visual value immediately
                _isVolumeUpdatingFromMpv = true;
                foreach (var otherSlider in _volumeSliders)
                {
                    if (otherSlider != s)
                        otherSlider.Value = e.NewValue;
                }
                _isVolumeUpdatingFromMpv = false;

                SetMpvPropertyDouble(_mpvHandle, "volume", e.NewValue);
                UpdateVolumeAmplifiedClass(e.NewValue);
            };

            // Implement coordinate-based click-to-set-volume
            slider.AddHandler(
                InputElement.PointerPressedEvent,
                (object? sender, PointerPressedEventArgs e) =>
                {
                    var point = e.GetCurrentPoint(slider);
                    if (point.Properties.IsLeftButtonPressed && slider.Bounds.Width > 0)
                    {
                        double pct = point.Position.X / slider.Bounds.Width;
                        pct = Math.Max(0, Math.Min(1, pct));
                        double newValue = slider.Minimum + pct * (slider.Maximum - slider.Minimum);
                        slider.Value = newValue;
                    }
                },
                RoutingStrategies.Tunnel);
        }

        foreach (var btn in _loopButtons)
        {
            btn.Click += (_, _) => ToggleLooping();
        }

        // Pre-populate dropdown menus immediately on startup
        PopulateAudioTracks();
        PopulateSubtitleTracks();
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
                InitializePreviewEngine();

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
                    LoadChapters();
                    PopulateAudioTracks();
                    PopulateSubtitleTracks();
                    UpdatePreviewContainerAspect();
                }
                break;

            case "volume":
                if (double.IsNaN(value) || double.IsInfinity(value)) return;
                int roundedVol = (int)Math.Round(value);
                LogEngineEvent($"Playback Engine Master Volume Altered: {roundedVol}%");
                TriggerOsdHUD("🔊", $"Vol {roundedVol}%");
                Dispatcher.UIThread.Post(() =>
                {
                    _isVolumeUpdatingFromMpv = true;
                    foreach (var slider in _volumeSliders)
                        slider.Value = value;
                    _isVolumeUpdatingFromMpv = false;
                    if (value > 0) _isMuted = false;
                    foreach (var icon in _volumeIcons)
                        icon.Data = StreamGeometry.Parse(_isMuted ? VolumeMuteSvg : VolumeHighSvg);
                });
                UpdateVolumeAmplifiedClass(value);
                break;
        }
    }

    private void UpdateVolumeAmplifiedClass(double value)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var slider in _volumeSliders)
            {
                if (value > 100)
                {
                    if (!slider.Classes.Contains("amplified"))
                        slider.Classes.Add("amplified");
                }
                else
                {
                    slider.Classes.Remove("amplified");
                }
            }
        });
    }

    private void ToggleLooping()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        _isLooping = !_isLooping;
        SendCommand(_mpvHandle, "cycle", "loop-file");
        Log($"Looping state toggled: {_isLooping}");

        // Update both loop buttons visual state
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var btn in _loopButtons)
            {
                if (btn.Content is PathIcon pathIcon)
                {
                    pathIcon.Foreground = _isLooping ? SolidColorBrush.Parse("#66AFFF") : SolidColorBrush.Parse("#5C647C");
                }
            }
        });
    }

    private string? GetMpvPropertyString(string name)
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return null;
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        IntPtr ptr = MpvGetPropertyString(_mpvHandle, nameBytes);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            MpvFree(ptr);
        }
    }

    private void PopulateAudioTracks()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        string? countStr = GetMpvPropertyString("track-list/count");
        if (!int.TryParse(countStr, out int count)) count = 0;

        var audioTracks = new System.Collections.Generic.List<(int id, string title, string lang, bool selected)>();

        for (int i = 0; i < count; i++)
        {
            string? type = GetMpvPropertyString($"track-list/{i}/type");
            if (type == "audio")
            {
                string? idStr = GetMpvPropertyString($"track-list/{i}/id");
                if (int.TryParse(idStr, out int id))
                {
                    string title = GetMpvPropertyString($"track-list/{i}/title") ?? "";
                    string lang = GetMpvPropertyString($"track-list/{i}/lang") ?? "";
                    string selectedStr = GetMpvPropertyString($"track-list/{i}/selected") ?? "no";
                    bool selected = selectedStr == "yes";
                    audioTracks.Add((id, title, lang, selected));
                }
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var flyout in _audioFlyouts)
            {
                flyout.Items.Clear();

                if (audioTracks.Count == 0)
                {
                    flyout.Items.Add(new MenuItem { Header = "No Audio Tracks", IsEnabled = false });
                    continue;
                }

                foreach (var track in audioTracks)
                {
                    string label = $"Track {track.id}";
                    if (!string.IsNullOrEmpty(track.title) || !string.IsNullOrEmpty(track.lang))
                    {
                        label += $" ({(string.IsNullOrEmpty(track.title) ? track.lang : track.title)})";
                    }
                    if (track.selected)
                    {
                        label = "✓ " + label;
                    }

                    var item = new MenuItem { Header = label };
                    int trackId = track.id;
                    item.Click += (s, e) => SelectAudioTrack(trackId);
                    flyout.Items.Add(item);
                }
            }
        });
    }

    private void SelectAudioTrack(int trackId)
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;
        Log($"Selecting audio track: {trackId}");
        SetMpvPropertyString(_mpvHandle, "aid", trackId.ToString());
    }

    private void PopulateSubtitleTracks()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        string? countStr = GetMpvPropertyString("track-list/count");
        if (!int.TryParse(countStr, out int count)) count = 0;

        var subTracks = new System.Collections.Generic.List<(int id, string title, string lang, bool selected)>();

        string? currentSid = GetMpvPropertyString("sid");
        bool subsDisabled = string.IsNullOrEmpty(currentSid) || currentSid == "no";

        for (int i = 0; i < count; i++)
        {
            string? type = GetMpvPropertyString($"track-list/{i}/type");
            if (type == "sub")
            {
                string? idStr = GetMpvPropertyString($"track-list/{i}/id");
                if (int.TryParse(idStr, out int id))
                {
                    string title = GetMpvPropertyString($"track-list/{i}/title") ?? "";
                    string lang = GetMpvPropertyString($"track-list/{i}/lang") ?? "";
                    string selectedStr = GetMpvPropertyString($"track-list/{i}/selected") ?? "no";
                    bool selected = selectedStr == "yes" && !subsDisabled;
                    subTracks.Add((id, title, lang, selected));
                }
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var flyout in _subtitleFlyouts)
            {
                flyout.Items.Clear();

                // A) Direct File Injector
                var addExternalItem = new MenuItem { Header = "+ Add External Subtitle File..." };
                addExternalItem.Click += async (s, e) =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel != null)
                    {
                        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                        {
                            Title = "Select Subtitle File",
                            AllowMultiple = false,
                            FileTypeFilter = new[]
                            {
                                new FilePickerFileType("Subtitle Files")
                                {
                                    Patterns = new[] { "*.srt", "*.vtt", "*.ass", "*.sub", "*.ssa" }
                                }
                            }
                        });

                        if (files != null && files.Count > 0)
                        {
                            var localPath = files[0].Path.LocalPath;
                            if (!string.IsNullOrEmpty(localPath))
                            {
                                SendCommand(_mpvHandle, "sub-add", localPath);
                                TriggerOsdHUD("📝", "External Sub Added");
                                Log($"Injected external subtitle file: {localPath}");
                                PopulateSubtitleTracks();
                            }
                        }
                    }
                };
                flyout.Items.Add(addExternalItem);

                // B) Real-time Style Overrides
                var settingsMenu = new MenuItem { Header = "A_ Subtitle Settings" };

                var sizeMenu = new MenuItem { Header = "Text Size" };
                sizeMenu.Items.Add(CreateSizeItem("Small (0.8x)", 0.8));
                sizeMenu.Items.Add(CreateSizeItem("Normal (1.0x)", 1.0));
                sizeMenu.Items.Add(CreateSizeItem("Large (1.3x)", 1.3));
                sizeMenu.Items.Add(CreateSizeItem("Huge (1.6x)", 1.6));
                settingsMenu.Items.Add(sizeMenu);

                var colorMenu = new MenuItem { Header = "Text Color" };
                colorMenu.Items.Add(CreateColorItem("White", "#FFFFFF"));
                colorMenu.Items.Add(CreateColorItem("Yellow", "#FFFF00"));
                colorMenu.Items.Add(CreateColorItem("Blue", "#66AFFF"));
                settingsMenu.Items.Add(colorMenu);

                var posMenu = new MenuItem { Header = "Positioning" };
                posMenu.Items.Add(CreatePosItem("High", 80));
                posMenu.Items.Add(CreatePosItem("Normal", 36));
                posMenu.Items.Add(CreatePosItem("Low", 15));
                settingsMenu.Items.Add(posMenu);

                flyout.Items.Add(settingsMenu);

                // C) Audio Synchronization Shifter
                var syncMenu = new MenuItem { Header = "⏱ Subtitle Sync" };

                var delayItem = new MenuItem { Header = "Delay 100ms (-0.1s)" };
                delayItem.Click += (s, e) => AdjustSubDelay(-0.1);
                syncMenu.Items.Add(delayItem);

                var advanceItem = new MenuItem { Header = "Advance 100ms (+0.1s)" };
                advanceItem.Click += (s, e) => AdjustSubDelay(0.1);
                syncMenu.Items.Add(advanceItem);

                var resetItem = new MenuItem { Header = "Reset Sync" };
                resetItem.Click += (s, e) => AdjustSubDelay(0, true);
                syncMenu.Items.Add(resetItem);

                flyout.Items.Add(syncMenu);

                // Separator before the standard track list
                flyout.Items.Add(new Separator());

                string disableLabel = "Disable Subtitles";
                if (subsDisabled)
                {
                    disableLabel = "✓ " + disableLabel;
                }
                var disableItem = new MenuItem { Header = disableLabel };
                disableItem.Click += (s, e) => SelectSubtitleTrack(-1);
                flyout.Items.Add(disableItem);

                if (subTracks.Count > 0)
                {
                    flyout.Items.Add(new Separator());
                }

                foreach (var track in subTracks)
                {
                    string label = $"Subtitle {track.id}";
                    if (!string.IsNullOrEmpty(track.title) || !string.IsNullOrEmpty(track.lang))
                    {
                        label += $" ({(string.IsNullOrEmpty(track.title) ? track.lang : track.title)})";
                    }
                    if (track.selected)
                    {
                        label = "✓ " + label;
                    }

                    var item = new MenuItem { Header = label };
                    int trackId = track.id;
                    item.Click += (s, e) => SelectSubtitleTrack(trackId);
                    flyout.Items.Add(item);
                }
            }
        });
    }

    private void SelectSubtitleTrack(int trackId)
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;
        
        if (trackId == -1)
        {
            Log("Disabling subtitles");
            SetMpvPropertyString(_mpvHandle, "sid", "no");
        }
        else
        {
            Log($"Selecting subtitle track: {trackId}");
            SetMpvPropertyString(_mpvHandle, "sid", trackId.ToString());
        }
    }

    private void UpdateTransportUI(double current, double total)
    {
        if (double.IsNaN(current) || double.IsInfinity(current) || current < 0) current = 0;
        if (double.IsNaN(total) || double.IsInfinity(total) || total < 0) total = 0;

        Dispatcher.UIThread.Post(() =>
        {
            if (_playbackSliders.Length == 0 && _currentTimeTexts.Length == 0 && _totalDurationTexts.Length == 0)
            {
                Log("UpdateTransportUI: one or more controls are null, skipping");
                return;
            }

            if (total > 0)
            {
                foreach (var slider in _playbackSliders)
                    slider.Maximum = total;
            }

            // Avoid updating slider position if user is seeking or if a seek was performed recently (within 500ms) to prevent rubber-banding
            if (!_isUserSeeking && (DateTime.UtcNow - _lastSeekTime).TotalMilliseconds > 500)
            {
                foreach (var slider in _playbackSliders)
                    slider.Value = current;
            }

            string currentStr = TimeSpan.FromSeconds(current).ToString(@"hh\:mm\:ss");
            foreach (var txt in _currentTimeTexts)
                txt.Text = currentStr;

            string totalStr = TimeSpan.FromSeconds(total).ToString(@"hh\:mm\:ss");
            foreach (var txt in _totalDurationTexts)
                txt.Text = totalStr;
        });
    }

    private void PerformSeekFromSlider(Slider slider)
    {
        if (!_isEngineInitialized || _mpvHandle == IntPtr.Zero) return;

        double seekPos = slider.Value;
        Log($"Transport seek command from {slider.Name}: seek {seekPos:F3} absolute");
        SendCommand(_mpvHandle, "seek", seekPos.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute");
        _lastSeekTime = DateTime.UtcNow;
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
            foreach (var icon in _playPauseIcons)
                icon.Data = StreamGeometry.Parse(_isPaused ? PlaySvg : PauseSvg);
        });
    }

    private void ToggleMute()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        _isMuted = !_isMuted;
        if (_isMuted)
        {
            _volumeBeforeMute = _volumeSliders.FirstOrDefault()?.Value ?? 100;
            SetMpvPropertyDouble(_mpvHandle, "volume", 0);
        }
        else
        {
            SetMpvPropertyDouble(_mpvHandle, "volume", _volumeBeforeMute);
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var icon in _volumeIcons)
                icon.Data = StreamGeometry.Parse(_isMuted ? VolumeMuteSvg : VolumeHighSvg);
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
            if (_rootBorder != null)
            {
                _rootBorder.CornerRadius = new Avalonia.CornerRadius(8);
                _rootBorder.BorderThickness = new Thickness(1);
            }
            Log("Exited Fullscreen Mode: UI Frame Chrome Layout restored.");
            _videoSurface?.SyncOverlayGeometry();
        }
        else
        {
            this.WindowState = WindowState.FullScreen;
            if (_customTitleBar != null) _customTitleBar.IsVisible = false;
            if (_mainRootGrid != null)
                _mainRootGrid.RowDefinitions = new RowDefinitions("0,*");
            if (_rootBorder != null)
            {
                _rootBorder.CornerRadius = new Avalonia.CornerRadius(0);
                _rootBorder.BorderThickness = new Thickness(0);
            }
            Log("Entered Fullscreen Mode: UI Frame Chrome Layout completely hidden.");
            _videoSurface?.SyncOverlayGeometry();
        }
    }

    private void ToggleLayoutMode()
    {
        if (_transportBar == null) return;
        
        if (_transportBar.Classes.Contains("single-line"))
        {
            _transportBar.Classes.Remove("single-line");
            _transportBar.Classes.Add("tactical-deck");
            Log("Layout switched to Tactical Deck mode.");
        }
        else
        {
            _transportBar.Classes.Remove("tactical-deck");
            _transportBar.Classes.Add("single-line");
            Log("Layout switched to Single Line mode.");
        }
        
        // Force parent container layout measurement invalidation pass
        _transportBar.InvalidateMeasure();
        _transportBar.InvalidateVisual();
        _videoSurface?.SyncOverlayGeometry();
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

            // Restore default cursor on mouse activity
            this.Cursor = null;

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
                            
                            // Hide mouse cursor on inactivity timeout
                            this.Cursor = new Cursor(StandardCursorType.None);
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

        if (_previewMpvHandle != IntPtr.Zero)
        {
            SendCommand(_previewMpvHandle, "loadfile", filePath);
        }
    }

    private void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!_isEngineInitialized || _mpvHandle == IntPtr.Zero) return;

        // 1. Fullscreen Engine & UI Toggle Layout Alignment
        if (e.Key == Key.F || (e.Key == Key.Escape && this.WindowState == WindowState.FullScreen))
        {
            e.Handled = true;
            ToggleFullscreen();
            return;
        }

        if (e.Key == Key.Z)
        {
            e.Handled = true;
            AdjustSubDelay(-0.1);
            return;
        }
        if (e.Key == Key.X)
        {
            e.Handled = true;
            AdjustSubDelay(0.1);
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
        _previewCts?.Cancel();
        _debounceTimer?.Dispose();

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

        if (_previewMpvHandle != IntPtr.Zero)
        {
            IntPtr localHandle = _previewMpvHandle;
            _previewMpvHandle = IntPtr.Zero;
            MpvTerminateDestroy(localHandle);
            Log("Background preview engine successfully terminated and destroyed.");
        }

        try
        {
            if (Directory.Exists(_previewTempDir))
            {
                Directory.Delete(_previewTempDir, true);
            }
        }
        catch { }

        base.OnClosing(e);
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized || this.WindowState == WindowState.FullScreen)
            return;

        if (sender is Control control && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (Enum.TryParse<WindowEdge>(control.Tag?.ToString(), out var edge))
            {
                this.BeginResizeDrag(edge, e);
            }
        }
    }

    private System.Collections.Generic.List<double> _chapterPercentages = new();

    private void LoadChapters()
    {
        _chapterPercentages.Clear();
        if (_playbackDuration <= 0) return;

        try
        {
            string? jsonStr = GetMpvPropertyString("chapter-list");
            if (!string.IsNullOrEmpty(jsonStr))
            {
                using var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.TryGetProperty("time", out var timeProp))
                        {
                            double timeSeconds = timeProp.GetDouble();
                            if (timeSeconds > 0 && timeSeconds < _playbackDuration)
                            {
                                double pct = timeSeconds / _playbackDuration;
                                _chapterPercentages.Add(pct);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error parsing chapters: {ex.Message}");
        }

        Log($"Parsed {_chapterPercentages.Count} chapters for current video.");
        RenderChapterTicks();
    }

    private void RenderChapterTicks()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var slider in _playbackSliders)
            {
                var canvas = FindVisualChild<Canvas>(slider, "ChaptersCanvas");
                if (canvas == null) continue;

                canvas.Children.Clear();
                double width = canvas.Bounds.Width;
                if (width <= 0) continue;

                foreach (var pct in _chapterPercentages)
                {
                    var tick = new Border
                    {
                        Width = 1,
                        Background = SolidColorBrush.Parse("#0C0C12"), // Segment color matches window background
                    };
                    tick.Bind(Border.HeightProperty, canvas.GetObservable(Canvas.HeightProperty));
                    Canvas.SetLeft(tick, pct * width);
                    canvas.Children.Add(tick);
                }
            }
        });
    }

    private static T? FindVisualChild<T>(Avalonia.Visual visual, string name) where T : Avalonia.Visual
    {
        if (visual.Name == name && visual is T match) return match;

        foreach (var child in visual.GetVisualChildren())
        {
            var result = FindVisualChild<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private void AdjustSubDelay(double delta, bool reset = false)
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        double currentDelay = 0;
        string? delayStr = GetMpvPropertyString("sub-delay");
        if (double.TryParse(delayStr, out double val))
        {
            currentDelay = val;
        }

        double newDelay = reset ? 0 : currentDelay + delta;
        SetMpvPropertyDouble(_mpvHandle, "sub-delay", newDelay);

        int ms = (int)Math.Round(newDelay * 1000);
        string direction = ms >= 0 ? "+" : "";
        TriggerOsdHUD("⏱", $"Sub Delay: {direction}{ms}ms");
        Log($"Subtitle delay adjusted: {newDelay:F2}s");
    }

    private MenuItem CreateSizeItem(string header, double scale)
    {
        var item = new MenuItem { Header = header };
        item.Click += (s, e) =>
        {
            if (_mpvHandle != IntPtr.Zero)
            {
                Log($"Setting sub-scale to {scale}");
                SetMpvPropertyDouble(_mpvHandle, "sub-scale", scale);
                TriggerOsdHUD("🔎", $"Sub Size: {scale}x");
            }
        };
        return item;
    }

    private MenuItem CreateColorItem(string header, string hexColor)
    {
        var item = new MenuItem { Header = header };
        item.Click += (s, e) =>
        {
            if (_mpvHandle != IntPtr.Zero)
            {
                Log($"Setting sub-color to {hexColor}");
                SetMpvPropertyString(_mpvHandle, "sub-color", hexColor);
                TriggerOsdHUD("🎨", $"Sub Color: {header}");
            }
        };
        return item;
    }

    private MenuItem CreatePosItem(string header, double marginY)
    {
        var item = new MenuItem { Header = header };
        item.Click += (s, e) =>
        {
            if (_mpvHandle != IntPtr.Zero)
            {
                Log($"Setting sub-margin-y to {marginY}");
                SetMpvPropertyDouble(_mpvHandle, "sub-margin-y", marginY);
                TriggerOsdHUD("↕", $"Sub Position: {header}");
            }
        };
        return item;
    }

    private void InitializePreviewEngine()
    {
        try
        {
            Log("Initializing background preview engine...");
            // Use AppContext.BaseDirectory for reliable output directory alignment
            _previewTempDir = Path.Combine(AppContext.BaseDirectory, "preview_temp");
            if (!Directory.Exists(_previewTempDir))
            {
                Directory.CreateDirectory(_previewTempDir);
            }

            _previewMpvHandle = MpvCreate();
            if (_previewMpvHandle == IntPtr.Zero)
            {
                Log("Failed to create background preview engine handle.");
                return;
            }

            // Headless VO configuration (ensures NO visible window is ever opened)
            SetMpvOptionString(_previewMpvHandle, "ao", "null");
            SetMpvOptionString(_previewMpvHandle, "vo", "image");
            SetMpvOptionString(_previewMpvHandle, "vo-image-format", "jpg");
            SetMpvOptionString(_previewMpvHandle, "vo-image-outdir", _previewTempDir);
            SetMpvOptionString(_previewMpvHandle, "osc", "no");
            SetMpvOptionString(_previewMpvHandle, "osd-level", "0");
            SetMpvOptionString(_previewMpvHandle, "embeddedfonts", "no");
            SetMpvOptionString(_previewMpvHandle, "vf", "scale=240:-1");
            SetMpvOptionString(_previewMpvHandle, "pause", "yes");

            int res = MpvInitialize(_previewMpvHandle);
            Log($"Background preview engine initialization returned: {res}");
        }
        catch (Exception ex)
        {
            Log($"Failed to initialize preview engine: {ex.Message}");
        }
    }

    private void UpdatePreviewContainerAspect()
    {
        string? wStr = GetMpvPropertyString("width");
        string? hStr = GetMpvPropertyString("height");
        if (double.TryParse(wStr, out double w) && double.TryParse(hStr, out double h) && w > 0 && h > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var container = _seekPreviewCard != null ? FindInTree<Border>(_seekPreviewCard, "PreviewImageContainer") : null;
                if (container != null)
                {
                    double aspect = w / h;
                    container.Width = 160;
                    container.Height = 160 / aspect;
                }
            });
        }
    }

    private void OnSliderPointerEntered(object? sender, PointerEventArgs e)
    {
        var card = _seekPreviewCard;
        if (card != null) card.Opacity = 1.0;
    }

    private void OnSliderPointerExited(object? sender, PointerEventArgs e)
    {
        var card = _seekPreviewCard;
        if (card != null)
        {
            card.Opacity = 0.0;
        }

        _debounceTimer?.Dispose();
        _debounceTimer = null;

        var previewImage = _seekPreviewCard != null ? FindInTree<Image>(_seekPreviewCard, "PreviewImage") : null;
        if (previewImage != null) previewImage.Source = null;
    }

    private void OnSliderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Slider slider || _playbackDuration <= 0) return;

        var point = e.GetCurrentPoint(slider);
        double mouseX = point.Position.X;
        double sliderWidth = slider.Bounds.Width;
        if (sliderWidth <= 0) return;

        double ratio = mouseX / sliderWidth;
        ratio = Math.Max(0, Math.Min(1, ratio));
        _hoveredSeekTime = ratio * _playbackDuration;

        var timeText = _seekPreviewCard != null ? FindInTree<TextBlock>(_seekPreviewCard, "PreviewTimeText") : null;
        if (timeText != null)
        {
            timeText.Text = TimeSpan.FromSeconds(_hoveredSeekTime).ToString(@"hh\:mm\:ss");
        }

        double bottomMargin = 76;
        if (_transportBar != null && _transportBar.Classes.Contains("tactical-deck"))
        {
            bottomMargin = 112;
        }

        if (_hudPanel != null && _seekPreviewCard != null)
        {
            var relativePoint = slider.TranslatePoint(new Point(mouseX, 0), _hudPanel);
            Log($"OnSliderPointerMoved: mouseX={mouseX:F2}, sliderWidth={sliderWidth:F2}, ratio={ratio:F3}, relativePoint={relativePoint}, hudPanelBounds={_hudPanel.Bounds.Width:F2}x{_hudPanel.Bounds.Height:F2}");
            
            double left = 0;
            if (relativePoint.HasValue)
            {
                double hoverX = relativePoint.Value.X;
                double cardWidth = 172; // width of SeekPreviewCard + padding
                left = hoverX - (cardWidth / 2);
                left = Math.Max(6, Math.Min(left, _hudPanel.Bounds.Width - cardWidth - 6));
            }
            else
            {
                // Fallback: manually walk up visual parent chain
                try
                {
                    double absoluteX = mouseX;
                    Visual? current = slider;
                    while (current != null && current != _hudPanel)
                    {
                        absoluteX += current.Bounds.X;
                        current = current.GetVisualParent();
                    }
                    
                    double cardWidth = 172;
                    left = absoluteX - (cardWidth / 2);
                    double hudWidth = _hudPanel.Bounds.Width > 0 ? _hudPanel.Bounds.Width : 800;
                    left = Math.Max(6, Math.Min(left, hudWidth - cardWidth - 6));
                }
                catch (Exception ex)
                {
                    Log($"OnSliderPointerMoved fallback error: {ex.Message}");
                }
            }

            _seekPreviewCard.Margin = new Thickness(0, 0, 0, bottomMargin);
            _seekPreviewCard.RenderTransform = new TranslateTransform(left, 0);
        }

        _debounceTimer?.Dispose();
        double targetTime = _hoveredSeekTime;
        _debounceTimer = new System.Threading.Timer(_ =>
        {
            Task.Run(() => ExtractPreviewFrameAsync(targetTime));
        }, null, 75, System.Threading.Timeout.Infinite);
    }

    private async Task ExtractPreviewFrameAsync(double timeSeconds)
    {
        if (_previewMpvHandle == IntPtr.Zero) return;

        _previewCts?.Cancel();
        _previewCts = new System.Threading.CancellationTokenSource();
        var token = _previewCts.Token;

        try
        {
            // 1. Clear any existing preview frames in preview_temp
            if (Directory.Exists(_previewTempDir))
            {
                foreach (var file in Directory.GetFiles(_previewTempDir, "*.jpg"))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            // 2. Perform seek to generate the frame
            Log($"ExtractPreviewFrameAsync: Seeking preview engine to {timeSeconds:F3}s...");
            SendCommand(_previewMpvHandle, "seek", timeSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute");

            // 3. Wait for the new jpg file to appear
            string? imgPath = null;
            int elapsed = 0;
            while (elapsed < 350) // wait up to 350ms
            {
                if (token.IsCancellationRequested) return;

                if (Directory.Exists(_previewTempDir))
                {
                    var files = Directory.GetFiles(_previewTempDir, "*.jpg");
                    if (files.Length > 0)
                    {
                        imgPath = files.OrderBy(f => f).Last(); // Get the latest file
                        break;
                    }
                }
                await Task.Delay(15, token);
                elapsed += 15;
            }

            if (imgPath != null && File.Exists(imgPath) && !token.IsCancellationRequested)
            {
                Log($"ExtractPreviewFrameAsync: Found frame image at '{imgPath}' after {elapsed}ms");
                
                // Read with retry loop to handle file write locks cleanly
                Avalonia.Media.Imaging.Bitmap? bitmap = null;
                int retries = 0;
                while (retries < 5 && bitmap == null && !token.IsCancellationRequested)
                {
                    try
                    {
                        using (var stream = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                        }
                    }
                    catch (IOException)
                    {
                        await Task.Delay(15, token);
                        retries++;
                    }
                }

                if (bitmap != null && !token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var previewImage = _seekPreviewCard != null ? FindInTree<Image>(_seekPreviewCard, "PreviewImage") : null;
                        if (previewImage != null)
                        {
                            previewImage.Source = bitmap;
                        }
                    });
                }
            }
            else
            {
                Log($"ExtractPreviewFrameAsync: Frame image not found or timed out after {elapsed}ms");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"Preview extraction error: {ex.Message}");
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            bool isNormal = this.WindowState == WindowState.Normal;
            if (_resizeGrid != null)
            {
                _resizeGrid.IsVisible = isNormal;
            }
            if (_overlayResizeGrid != null)
            {
                _overlayResizeGrid.IsVisible = isNormal;
            }
        }
    }
}
