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

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEventEndFile
    {
        public int Reason;
        public int Error;
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
    private Border? _videoTitleOverlay;
    private TextBlock? _videoTitleText;

    // Greenfield Stats Overlay Fields
    private Border? _statsOverlayCard;
    private TextBlock? _statsTextPanel;
    private System.Threading.CancellationTokenSource? _statsCts;
    private bool _isStatsSticky;
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
    // Loop mode: 0 = Off (play next in queue), 1 = Loop Queue, 2 = Loop Current Track
    private int _loopMode = 0;
    private int _currentQueueIndex = -1;
    private bool _isPlayingFromQueue = false;
    private Button? _shuffleButton;
    private bool _isShuffleEnabled = false;
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
    private Panel? _idleLogoPanel;

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

    // Phase 3 state fields
    private System.Collections.ObjectModel.ObservableCollection<QueueItemViewModel> _playbackQueueItems = new();
    private System.Collections.ObjectModel.ObservableCollection<MediaItemViewModel> _directoryItems = new();
    private System.Collections.Generic.List<FileSystemInfo> _allEntries = new();
    private string _currentDirectoryPath = "";
    private int _currentPageIndex = 1;
    private int _totalPageCount = 1;
    private string _activeTab = "Queue";
    private const int ItemsPerPage = 10;
    private System.Threading.CancellationTokenSource? _lazyLoadCts;

    // Phase 3 Control References
    private Border? _dynamicSidePanel;
    private Button? _queueTabButton;
    private Button? _directoryTabButton;
    private Border? _queueTabIndicator;
    private Border? _directoryTabIndicator;
    private Panel? _queuePanel;
    private Panel? _directoryPanel;
    private ListBox? _queueListBox;
    private StackPanel? _queuePlaceholderText;
    private TextBox? _breadcrumbText;
    private Button? _upDirectoryButton;
    private ListBox? _directoryListBox;
    private Button? _prevPageButton;
    private TextBlock? _pageNumberText;
    private Button? _nextPageButton;
    private Button[] _sidePanelButtons = Array.Empty<Button>();

    public MainWindow()
    {
        // InitializeComponent runs first to build the visual context tree from XAML
        InitializeComponent();

        // Set BlueJay window icon from embedded asset
        try
        {
            using var iconStream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://BlueJayPlayer/Assets/blujay_logo.jpg"));
            this.Icon = new WindowIcon(iconStream);
        }
        catch { /* Icon load failed gracefully */ }

        // Phase 3 Control Resolution
        _dynamicSidePanel = this.FindControl<Border>("DynamicSidePanel");
        _queueTabButton = this.FindControl<Button>("QueueTabButton");
        _directoryTabButton = this.FindControl<Button>("DirectoryTabButton");
        _queueTabIndicator = this.FindControl<Border>("QueueTabIndicator");
        _directoryTabIndicator = this.FindControl<Border>("DirectoryTabIndicator");
        _queuePanel = this.FindControl<Panel>("QueuePanel");
        _directoryPanel = this.FindControl<Panel>("DirectoryPanel");
        _queueListBox = this.FindControl<ListBox>("QueueListBox");
        _queuePlaceholderText = this.FindControl<StackPanel>("QueuePlaceholderText");
        _breadcrumbText = this.FindControl<TextBox>("BreadcrumbText");
        _upDirectoryButton = this.FindControl<Button>("UpDirectoryButton");
        _directoryListBox = this.FindControl<ListBox>("DirectoryListBox");
        _prevPageButton = this.FindControl<Button>("PrevPageButton");
        _pageNumberText = this.FindControl<TextBlock>("PageNumberText");
        _nextPageButton = this.FindControl<Button>("NextPageButton");

        SetupSidePanel();
        
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
        _idleLogoPanel     = FindInTree<Panel>(hudPanel, "IdleLogoPanel");
        _videoTitleOverlay = FindInTree<Border>(hudPanel, "VideoTitleOverlay");
        _videoTitleText    = FindInTree<TextBlock>(hudPanel, "VideoTitleText");

        _statsOverlayCard        = FindInTree<Border>(hudPanel, "StatsOverlayCard");
        _statsTextPanel          = FindInTree<TextBlock>(hudPanel, "StatsTextPanel");
        _overlayResizeGrid = FindInTree<Grid>(hudPanel, "OverlayResizeGrid");
        if (_overlayResizeGrid != null)
        {
            _overlayResizeGrid.IsVisible = this.WindowState == WindowState.Normal;
        }

        var singleSidePanelBtn = FindInTree<Button>(hudPanel, "SingleSidePanelButton");
        var deckSidePanelBtn = FindInTree<Button>(hudPanel, "DeckSidePanelButton");
        _sidePanelButtons = new Button[] { singleSidePanelBtn!, deckSidePanelBtn! }.Where(x => x != null).ToArray();

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

        foreach (var btn in _sidePanelButtons)
        {
            btn.Click += (s, e) => ToggleSidePanel();
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
                // No default auto-play — show idle logo instead
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

            if (mpvEvent.EventId == 7) // MPV_EVENT_END_FILE
            {
                if (mpvEvent.Data != IntPtr.Zero)
                {
                    var endFileEvent = Marshal.PtrToStructure<MpvEventEndFile>(mpvEvent.Data);
                    Log($"RunEventLoop: MPV_EVENT_END_FILE received. Reason: {endFileEvent.Reason}, Error: {endFileEvent.Error}");
                    
                    // Reason 0 = MPV_END_FILE_REASON_EOF (ended naturally)
                    if (endFileEvent.Reason == 0)
                    {
                        Log("RunEventLoop: Video completed naturally. Advancing queue.");
                        PlayNextInQueue();
                    }
                }
            }
            else if (mpvEvent.EventId == 22) // MPV_EVENT_PROPERTY_CHANGE
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

        // Cycle: 0 (Off) -> 1 (Loop Queue) -> 2 (Loop Track) -> 0 (Off)
        _loopMode = (_loopMode + 1) % 3;

        // Tell mpv: loop-file only when mode == 2
        if (_loopMode == 2)
        {
            SendCommand(_mpvHandle, "set", "loop-file", "inf");
        }
        else
        {
            SendCommand(_mpvHandle, "set", "loop-file", "no");
        }

        string[] tooltips = { "Repeat: Off", "Repeat: Queue", "Repeat: Track" };
        Log($"Loop mode changed to: {tooltips[_loopMode]}");

        // Update visual state for all loop buttons
        Dispatcher.UIThread.Post(() =>
        {
            var dimColor = SolidColorBrush.Parse("#5C647C");
            var activeColor = SolidColorBrush.Parse("#66AFFF");

            foreach (var btn in _loopButtons)
            {
                btn.SetValue(ToolTip.TipProperty, tooltips[_loopMode]);

                if (btn.Content is Grid grid)
                {
                    foreach (var child in grid.Children)
                    {
                        if (child is PathIcon pathIcon)
                        {
                            // Off = dim, Queue/Track = active blue
                            pathIcon.Foreground = _loopMode == 0 ? dimColor : activeColor;
                        }
                        else if (child is TextBlock badge)
                        {
                            // Badge "1" only visible in Track mode (2)
                            badge.IsVisible = _loopMode == 2;
                        }
                    }
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

    private double GetMpvPropertyDouble(string name)
    {
        string? valStr = GetMpvPropertyString(name);
        if (double.TryParse(valStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
        {
            return val;
        }
        return 0;
    }

    private long GetMpvPropertyLong(string name)
    {
        string? valStr = GetMpvPropertyString(name);
        if (long.TryParse(valStr, out long val))
        {
            return val;
        }
        return 0;
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

    private void WakeTransportHUD(int delayMs = 2500)
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

            if (_videoTitleOverlay != null && _videoTitleOverlay.Opacity < 1.0 && _idleLogoPanel != null && !_idleLogoPanel.IsVisible)
            {
                _videoTitleOverlay.Opacity = 1.0;
            }

            Task.Delay(delayMs, token).ContinueWith(t =>
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

                        if (_videoTitleOverlay != null)
                        {
                            _videoTitleOverlay.Opacity = 0.0;
                        }
                        
                        // Hide mouse cursor on inactivity timeout
                        this.Cursor = new Cursor(StandardCursorType.None);
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
            if (!_isPlayingFromQueue)
            {
                int idx = -1;
                for (int i = 0; i < _playbackQueueItems.Count; i++)
                {
                    if (string.Equals(_playbackQueueItems[i].FullPath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }

                if (idx >= 0)
                {
                    if (_currentQueueIndex >= 0 && _currentQueueIndex < _playbackQueueItems.Count)
                    {
                        _playbackQueueItems[_currentQueueIndex].IsPlaying = false;
                    }
                    _currentQueueIndex = idx;
                    _playbackQueueItems[idx].IsPlaying = true;
                }
                else
                {
                    ClearQueuePlayingState();
                }
            }

            // Hide idle logo when media starts playing
            if (_idleLogoPanel != null) _idleLogoPanel.IsVisible = false;

            if (_videoTitleText != null)
            {
                _videoTitleText.Text = Path.GetFileName(filePath);
            }

            if (_statsOverlayCard != null)
            {
                _statsOverlayCard.Opacity = 0.0;
                _isStatsSticky = false;
                _statsCts?.Cancel();
            }

            WakeTransportHUD(4000);
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

        if (e.Key == Key.I)
        {
            e.Handled = true;
            bool isShiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            ToggleStatsOverlay(isShiftPressed);
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
        _statsCts?.Cancel();
        _transportCts?.Cancel();
        _previewCts?.Cancel();
        _lazyLoadCts?.Cancel();
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

    // --- Phase 3 Methods & Helpers ---
    private void SetupSidePanel()
    {
        if (_queueListBox != null)
        {
            _queueListBox.ItemsSource = _playbackQueueItems;
            _queueListBox.AddHandler(Button.ClickEvent, OnQueueListBoxButtonClicked);
            _queueListBox.DoubleTapped += (s, e) =>
            {
                if (_queueListBox.SelectedItem is QueueItemViewModel item)
                {
                    int index = _playbackQueueItems.IndexOf(item);
                    if (index >= 0)
                    {
                        PlayQueueItem(index);
                    }
                }
            };
        }

        if (_directoryListBox != null)
        {
            _directoryListBox.ItemsSource = _directoryItems;
            _directoryListBox.AddHandler(Button.ClickEvent, OnDirectoryListBoxButtonClicked);
            _directoryListBox.DoubleTapped += (s, e) =>
            {
                if (_directoryListBox.SelectedItem is MediaItemViewModel item)
                {
                    if (item.IsDirectory)
                    {
                        LoadDirectory(item.FullPath);
                    }
                    else
                    {
                        PlayMediaFile(item.FullPath);
                    }
                }
            };
        }

        if (_queueTabButton != null) _queueTabButton.Click += (s, e) => SwitchTab("Queue");
        if (_directoryTabButton != null) _directoryTabButton.Click += (s, e) => SwitchTab("Directory");
        if (_upDirectoryButton != null) _upDirectoryButton.Click += (s, e) => NavigateUpDirectory();
        if (_prevPageButton != null) _prevPageButton.Click += (s, e) => NavigatePage(-1);
        if (_nextPageButton != null) _nextPageButton.Click += (s, e) => NavigatePage(1);

        _shuffleButton = this.FindControl<Button>("ShuffleButton");
        if (_shuffleButton != null)
        {
            _shuffleButton.Click += (s, e) => ToggleShuffle();
        }

        var clearQueueBtn = this.FindControl<Button>("ClearQueueButton");
        if (clearQueueBtn != null)
        {
            clearQueueBtn.Click += (s, e) => ClearQueue();
        }

        // Allow typing a directory path and pressing Enter
        if (_breadcrumbText != null)
        {
            _breadcrumbText.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Return)
                {
                    string? typed = _breadcrumbText.Text?.Trim();
                    if (!string.IsNullOrEmpty(typed) && Directory.Exists(typed))
                    {
                        LoadDirectory(typed);
                    }
                    else
                    {
                        // Reset to the current directory if invalid path typed
                        _breadcrumbText.Text = _currentDirectoryPath;
                    }
                    e.Handled = true;
                }
            };
        }

        if (_queuePanel != null)
        {
            DragDrop.AddDragOverHandler(_queuePanel, (s, e) =>
            {
                e.Handled = true;
                e.DragEffects = DragDropEffects.Copy;
            });
            DragDrop.AddDropHandler(_queuePanel, OnQueuePanelFileDropped);
        }

        UpdateQueuePlaceholder();
        SwitchTab("Queue");
    }

    private async void ToggleSidePanel()
    {
        if (_dynamicSidePanel == null) return;
        bool opening = _dynamicSidePanel.Width == 0;
        _dynamicSidePanel.Width = opening ? 320 : 0;

        // Perform continuous sync on the overlay window while transition plays
        for (int i = 0; i < 25; i++)
        {
            _videoSurface?.SyncOverlayGeometry();
            await Task.Delay(15);
        }
        _videoSurface?.SyncOverlayGeometry();
    }

    private void SwitchTab(string tabName)
    {
        _activeTab = tabName;

        if (tabName == "Queue")
        {
            if (_queueTabButton != null && !_queueTabButton.Classes.Contains("active"))
            {
                _queueTabButton.Classes.Add("active");
            }
            if (_directoryTabButton != null)
            {
                _directoryTabButton.Classes.Remove("active");
            }
            if (_queueTabIndicator != null) _queueTabIndicator.IsVisible = true;
            if (_directoryTabIndicator != null) _directoryTabIndicator.IsVisible = false;
            if (_queuePanel != null) _queuePanel.IsVisible = true;
            if (_directoryPanel != null) _directoryPanel.IsVisible = false;
        }
        else // Directory
        {
            if (_directoryTabButton != null && !_directoryTabButton.Classes.Contains("active"))
            {
                _directoryTabButton.Classes.Add("active");
            }
            if (_queueTabButton != null)
            {
                _queueTabButton.Classes.Remove("active");
            }
            if (_queueTabIndicator != null) _queueTabIndicator.IsVisible = false;
            if (_directoryTabIndicator != null) _directoryTabIndicator.IsVisible = true;
            if (_queuePanel != null) _queuePanel.IsVisible = false;
            if (_directoryPanel != null) _directoryPanel.IsVisible = true;

            // Load default folder if not set
            if (string.IsNullOrEmpty(_currentDirectoryPath))
            {
                // Default to the user's Downloads folder
                string defaultPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (string.IsNullOrEmpty(defaultPath) || !Directory.Exists(defaultPath))
                {
                    defaultPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
                if (string.IsNullOrEmpty(defaultPath) || !Directory.Exists(defaultPath))
                {
                    defaultPath = AppContext.BaseDirectory;
                }
                LoadDirectory(defaultPath);
            }
        }
    }

    private void LoadDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        _currentDirectoryPath = path;
        _currentPageIndex = 1;

        try
        {
            var dirInfo = new DirectoryInfo(path);
            var entries = dirInfo.GetFileSystemInfos();

            var folders = entries.Where(e => (e.Attributes & FileAttributes.Directory) != 0)
                                 .OrderBy(e => e.Name)
                                 .ToList();

            var files = entries.Where(e => (e.Attributes & FileAttributes.Directory) == 0 && IsVideoFile(e.Extension))
                               .OrderBy(e => e.Name)
                               .ToList();

            _allEntries = folders.Concat(files).ToList();
            _totalPageCount = (int)Math.Ceiling((double)_allEntries.Count / ItemsPerPage);
            if (_totalPageCount == 0) _totalPageCount = 1;

            RenderCurrentPage();
        }
        catch (Exception ex)
        {
            Log($"Error loading directory contents: {ex.Message}");
        }
    }

    private void RenderCurrentPage()
    {
        _directoryItems.Clear();

        int startIndex = (_currentPageIndex - 1) * ItemsPerPage;
        var pageEntries = _allEntries.Skip(startIndex).Take(ItemsPerPage).ToList();

        foreach (var entry in pageEntries)
        {
            bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
            var item = new MediaItemViewModel
            {
                Name = entry.Name,
                FullPath = entry.FullName,
                IsDirectory = isDir,
                SizeText = isDir ? "" : FormatFileSize(((FileInfo)entry).Length),
                DurationText = "",
                Thumbnail = null
            };
            _directoryItems.Add(item);
        }

        if (_breadcrumbText != null) _breadcrumbText.Text = _currentDirectoryPath;
        if (_pageNumberText != null) _pageNumberText.Text = $"Page {_currentPageIndex} / {_totalPageCount}";

        if (_prevPageButton != null)
        {
            _prevPageButton.IsEnabled = _currentPageIndex > 1;
            _prevPageButton.Opacity = _currentPageIndex > 1 ? 1.0 : 0.2;
        }
        if (_nextPageButton != null)
        {
            _nextPageButton.IsEnabled = _currentPageIndex < _totalPageCount;
            _nextPageButton.Opacity = _currentPageIndex < _totalPageCount ? 1.0 : 0.2;
        }

        // Trigger staggered lazy loader
        _lazyLoadCts?.Cancel();
        _lazyLoadCts = new System.Threading.CancellationTokenSource();
        var token = _lazyLoadCts.Token;
        Task.Run(() => LazyLoadPageMetadataAndThumbnailsAsync(pageEntries, token), token);
    }

    private async Task LazyLoadPageMetadataAndThumbnailsAsync(System.Collections.Generic.List<FileSystemInfo> pageEntries, CancellationToken token)
    {
        if (_previewMpvHandle == IntPtr.Zero) return;

        foreach (var entry in pageEntries)
        {
            if (token.IsCancellationRequested) return;

            // Process video files only
            if ((entry.Attributes & FileAttributes.Directory) != 0 || !IsVideoFile(entry.Extension))
                continue;

            string filePath = entry.FullName;

            // Load file in background preview engine
            SendCommand(_previewMpvHandle, "loadfile", filePath);

            // Wait for duration to load
            double duration = 0;
            int elapsed = 0;
            while (elapsed < 1500 && !token.IsCancellationRequested)
            {
                string? durStr = GetMpvPropertyStringForHandle(_previewMpvHandle, "duration");
                if (double.TryParse(durStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double durVal) && durVal > 0)
                {
                    duration = durVal;
                    break;
                }
                await Task.Delay(25, token);
                elapsed += 25;
            }

            if (token.IsCancellationRequested) return;

            if (duration <= 0) continue;

            string durationText = TimeSpan.FromSeconds(duration).ToString(@"hh\:mm\:ss");
            Dispatcher.UIThread.Post(() =>
            {
                var target = _directoryItems.FirstOrDefault(x => x.FullPath == filePath);
                if (target != null)
                {
                    target.DurationText = durationText;
                }
            });

            // Clean existing frames
            if (Directory.Exists(_previewTempDir))
            {
                foreach (var f in Directory.GetFiles(_previewTempDir, "*.jpg"))
                {
                    try { File.Delete(f); } catch { }
                }
            }

            // Seek preview engine to 5%
            double seekTime = duration * 0.05;
            SendCommand(_previewMpvHandle, "seek", seekTime.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute");

            // Wait for new frame file
            string? imgPath = null;
            int seekElapsed = 0;
            while (seekElapsed < 1500 && !token.IsCancellationRequested)
            {
                if (Directory.Exists(_previewTempDir))
                {
                    var files = Directory.GetFiles(_previewTempDir, "*.jpg");
                    if (files.Length > 0)
                    {
                        imgPath = files.OrderBy(f => f).Last();
                        break;
                    }
                }
                await Task.Delay(25, token);
                seekElapsed += 25;
            }

            if (token.IsCancellationRequested) return;

            if (imgPath != null && File.Exists(imgPath))
            {
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
                        await Task.Delay(25, token);
                        retries++;
                    }
                }

                if (bitmap != null && !token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var target = _directoryItems.FirstOrDefault(x => x.FullPath == filePath);
                        if (target != null)
                        {
                            target.Thumbnail = bitmap;
                        }
                    });
                }
            }
        }
    }

    private void NavigateUpDirectory()
    {
        if (string.IsNullOrEmpty(_currentDirectoryPath)) return;
        var parent = Directory.GetParent(_currentDirectoryPath);
        if (parent != null)
        {
            LoadDirectory(parent.FullName);
        }
    }

    private void NavigatePage(int direction)
    {
        int newPage = _currentPageIndex + direction;
        if (newPage >= 1 && newPage <= _totalPageCount)
        {
            _currentPageIndex = newPage;
            RenderCurrentPage();
        }
    }

    private void OnQueuePanelFileDropped(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;

        foreach (var file in files)
        {
            string path = file.Path.LocalPath;
            if (string.IsNullOrEmpty(path)) continue;

            if (Directory.Exists(path))
            {
                EnqueueDirectoryRecursively(path);
            }
            else if (File.Exists(path))
            {
                if (IsVideoFile(Path.GetExtension(path)))
                {
                    EnqueueFile(path);
                }
            }
        }
    }

    private void EnqueueDirectoryRecursively(string dirPath)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(dirPath).OrderBy(d => d))
            {
                EnqueueDirectoryRecursively(dir);
            }
            foreach (var file in Directory.GetFiles(dirPath).Where(f => IsVideoFile(Path.GetExtension(f))).OrderBy(f => f))
            {
                EnqueueFile(file);
            }
        }
        catch (Exception ex)
        {
            Log($"Error enqueuing directory recursively: {ex.Message}");
        }
    }

    private void EnqueueFile(string filePath)
    {
        string name = Path.GetFileName(filePath);
        string durationText = "--:--";

        var existing = _directoryItems.FirstOrDefault(x => x.FullPath == filePath);
        if (existing != null && !string.IsNullOrEmpty(existing.DurationText))
        {
            durationText = existing.DurationText;
        }

        var item = new QueueItemViewModel
        {
            Name = name,
            FullPath = filePath,
            DurationText = durationText
        };
        _playbackQueueItems.Add(item);
        UpdateQueuePlaceholder();
    }

    private void UpdateQueuePlaceholder()
    {
        if (_queuePlaceholderText != null)
        {
            _queuePlaceholderText.IsVisible = _playbackQueueItems.Count == 0;
        }
    }

    private void OnQueueListBoxButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button btn && btn.Name == "RemoveQueueButton")
        {
            if (btn.DataContext is QueueItemViewModel item)
            {
                int index = _playbackQueueItems.IndexOf(item);
                if (index >= 0)
                {
                    _playbackQueueItems.RemoveAt(index);
                    UpdateQueuePlaceholder();

                    if (_currentQueueIndex == index)
                    {
                        _currentQueueIndex = -1;
                        if (_idleLogoPanel != null)
                        {
                            _idleLogoPanel.IsVisible = true;
                        }
                        if (_mpvHandle != IntPtr.Zero && _isEngineInitialized)
                        {
                            SendCommand(_mpvHandle, "stop");
                        }
                    }
                    else if (_currentQueueIndex > index)
                    {
                        _currentQueueIndex--;
                    }
                }
                e.Handled = true;
            }
        }
    }

    private void OnDirectoryListBoxButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button btn && btn.DataContext is MediaItemViewModel item)
        {
            if (btn.Name == "PlayActionButton")
            {
                PlayMediaFile(item.FullPath);
                e.Handled = true;
            }
            else if (btn.Name == "EnqueueActionButton")
            {
                EnqueueFile(item.FullPath);
                e.Handled = true;
            }
        }
    }

    private string? GetMpvPropertyStringForHandle(IntPtr handle, string name)
    {
        if (handle == IntPtr.Zero) return null;
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        IntPtr ptr = MpvGetPropertyString(handle, nameBytes);
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

    private static readonly System.Collections.Generic.HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp"
    };

    private bool IsVideoFile(string ext) => VideoExtensions.Contains(ext);

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        double number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:F1} {suffixes[counter]}";
    }

    private void ClearQueuePlayingState()
    {
        if (_currentQueueIndex >= 0 && _currentQueueIndex < _playbackQueueItems.Count)
        {
            _playbackQueueItems[_currentQueueIndex].IsPlaying = false;
        }
        _currentQueueIndex = -1;
    }

    private void PlayQueueItem(int index)
    {
        if (index < 0 || index >= _playbackQueueItems.Count) return;

        if (_currentQueueIndex >= 0 && _currentQueueIndex < _playbackQueueItems.Count)
        {
            _playbackQueueItems[_currentQueueIndex].IsPlaying = false;
        }

        _currentQueueIndex = index;
        var item = _playbackQueueItems[index];
        item.IsPlaying = true;

        _isPlayingFromQueue = true;
        try
        {
            PlayMediaFile(item.FullPath);
        }
        finally
        {
            _isPlayingFromQueue = false;
        }
    }

    private void ToggleShuffle()
    {
        _isShuffleEnabled = !_isShuffleEnabled;
        if (_shuffleButton != null)
        {
            if (_isShuffleEnabled)
            {
                _shuffleButton.Foreground = Avalonia.Media.SolidColorBrush.Parse("#2B88FF");
                _shuffleButton.BorderBrush = Avalonia.Media.SolidColorBrush.Parse("#2B88FF");
            }
            else
            {
                _shuffleButton.Foreground = Avalonia.Media.SolidColorBrush.Parse("#5C647C");
                _shuffleButton.BorderBrush = Avalonia.Media.SolidColorBrush.Parse("#1F222F");
            }
        }
        Log($"Shuffle mode toggled: {_isShuffleEnabled}");
    }

    private void ClearQueue()
    {
        _playbackQueueItems.Clear();
        UpdateQueuePlaceholder();
        
        if (_currentQueueIndex >= 0)
        {
            _currentQueueIndex = -1;
            if (_idleLogoPanel != null)
            {
                _idleLogoPanel.IsVisible = true;
            }
            if (_mpvHandle != IntPtr.Zero && _isEngineInitialized)
            {
                SendCommand(_mpvHandle, "stop");
            }
        }
        Log("Playback queue cleared successfully.");
    }

    private void PlayNextInQueue()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Mode 2 = Loop current track — mpv handles it via loop-file=inf, do nothing
            if (_loopMode == 2) return;

            if (_playbackQueueItems.Count > 0)
            {
                int nextIndex = -1;

                if (_isShuffleEnabled && _playbackQueueItems.Count > 1)
                {
                    var random = new Random();
                    do
                    {
                        nextIndex = random.Next(_playbackQueueItems.Count);
                    } while (nextIndex == _currentQueueIndex);
                }
                else if (_currentQueueIndex >= 0)
                {
                    nextIndex = _currentQueueIndex + 1;
                    if (nextIndex >= _playbackQueueItems.Count)
                    {
                        if (_loopMode == 1) // Loop Queue
                        {
                            nextIndex = 0;
                        }
                        else
                        {
                            nextIndex = -1; // Stop playing
                        }
                    }
                }
                else
                {
                    nextIndex = 0;
                }

                if (nextIndex >= 0)
                {
                    PlayQueueItem(nextIndex);
                }
                else
                {
                    ClearQueuePlayingState();
                    if (_idleLogoPanel != null)
                    {
                        _idleLogoPanel.IsVisible = true;
                    }
                }
            }
            else
            {
                ClearQueuePlayingState();
                if (_idleLogoPanel != null)
                {
                    _idleLogoPanel.IsVisible = true;
                }
            }
        });
    }

    private void ToggleStatsOverlay(bool sticky = false)
    {
        if (_statsOverlayCard == null) return;

        bool isCurrentlyVisible = _statsOverlayCard.Opacity > 0;
        
        if (isCurrentlyVisible)
        {
            _statsCts?.Cancel();
            Dispatcher.UIThread.Post(() =>
            {
                if (_statsOverlayCard != null)
                {
                    _statsOverlayCard.Opacity = 0.0;
                    _isStatsSticky = false;
                }
            });
        }
        else
        {
            PopulateAndShowStats(sticky);
        }
    }

    private void PopulateAndShowStats(bool sticky = false)
    {
        if (_statsOverlayCard == null) return;

        _statsCts?.Cancel();
        _statsCts = new System.Threading.CancellationTokenSource();
        var token = _statsCts.Token;

        _statsOverlayCard.Opacity = 1.0;
        _isStatsSticky = sticky;

        // Perform first immediate update
        UpdateStatsText();

        // Start background 1-second update loop
        Task.Run(async () =>
        {
            try
            {
                // Wait 1 second before first loop tick
                await Task.Delay(1000, token);
                while (!token.IsCancellationRequested)
                {
                    UpdateStatsText();
                    await Task.Delay(1000, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Stats update loop encountered error: {ex.Message}");
            }
        }, token);

        if (!sticky)
        {
            Task.Delay(4000, token).ContinueWith(t =>
            {
                if (!token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_statsOverlayCard != null)
                        {
                            _statsOverlayCard.Opacity = 0.0;
                            _isStatsSticky = false;
                        }
                        _statsCts?.Cancel();
                    });
                }
            }, TaskContinuationOptions.None);
        }
    }

    private void UpdateStatsText()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        // Fetch properties
        string videoCodec = GetMpvPropertyString("video-codec") ?? GetMpvPropertyString("video-format") ?? "Unknown";
        string pixFmt = GetMpvPropertyString("video-params/pixelformat") ?? GetMpvPropertyString("video-params/pix-fmt") ?? "Unknown";
        string matrix = GetMpvPropertyString("video-params/colormatrix") ?? "Unknown";
        string primaries = GetMpvPropertyString("video-params/primaries") ?? "Unknown";
        string colorLevels = GetMpvPropertyString("video-params/colorlevels") ?? GetMpvPropertyString("video-params/color-levels") ?? "Unknown";
        string gamma = GetMpvPropertyString("video-params/gamma") ?? "Unknown";

        string videoW = GetMpvPropertyString("video-params/w") ?? "0";
        string videoH = GetMpvPropertyString("video-params/h") ?? "0";
        string outW = GetMpvPropertyString("video-out-params/w") ?? "0";
        string outH = GetMpvPropertyString("video-out-params/h") ?? "0";

        double containerFps = GetMpvPropertyDouble("container-fps");
        double estimatedFps = GetMpvPropertyDouble("estimated-vf-fps");
        if (estimatedFps <= 0) estimatedFps = GetMpvPropertyDouble("fps");

        long dropped = GetMpvPropertyLong("frame-drop-count");
        long mistimed = GetMpvPropertyLong("mistimed-frame-count");

        string audioCodec = GetMpvPropertyString("audio-codec") ?? GetMpvPropertyString("audio-format") ?? "Unknown";
        double sampleRate = GetMpvPropertyDouble("audio-params/samplerate");
        string channels = GetMpvPropertyString("audio-params/channel-count") ?? "Unknown";
        string audioFormat = GetMpvPropertyString("audio-params/format") ?? "Unknown";
        double audioBitrate = GetMpvPropertyDouble("audio-bitrate");

        double cacheDuration = GetMpvPropertyDouble("demuxer-cache-duration");
        string videoSync = GetMpvPropertyString("video-sync") ?? "Unknown";

        // Build telemetry text layout exactly as specified
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🎥 VIDEO");
        sb.AppendLine($"Video:        {videoCodec} ({videoW}x{videoH} -> {outW}x{outH})");
        sb.AppendLine($"Pixel Format: {pixFmt}");
        sb.AppendLine($"Color Space:  Matrix: {matrix} / Primaries: {primaries} / Levels: {colorLevels}");
        sb.AppendLine($"Gamma/Curve:  {gamma}");
        sb.AppendLine($"Frame Rate:   {containerFps:F3} fps (Specified) / {estimatedFps:F3} fps (Rendered)");
        sb.AppendLine($"Dropped:      {dropped} frames / Mistimed: {mistimed}");
        sb.AppendLine();
        sb.AppendLine("🔊 AUDIO");
        sb.AppendLine($"Audio:        {audioCodec} ({sampleRate:F0} Hz / {channels}ch / {audioFormat})");
        sb.AppendLine($"Bitrate:      {(audioBitrate / 1000):F0} kbps");
        sb.AppendLine();
        sb.AppendLine("⚙️ PERFORMANCE");
        sb.AppendLine($"Cache:        {cacheDuration:F2} sec ahead");
        sb.Append($"Display Sync: {videoSync}");

        string fullTelemetryText = sb.ToString();

        Dispatcher.UIThread.Post(() =>
        {
            if (_statsTextPanel != null)
            {
                _statsTextPanel.Text = fullTelemetryText;
            }
        });
    }
}

// --- Phase 3 ViewModels ---
public class MediaItemViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    private string _fullPath = "";
    public string FullPath
    {
        get => _fullPath;
        set { _fullPath = value; OnPropertyChanged(nameof(FullPath)); }
    }

    private bool _isDirectory;
    public bool IsDirectory
    {
        get => _isDirectory;
        set { _isDirectory = value; OnPropertyChanged(nameof(IsDirectory)); }
    }

    private string _sizeText = "";
    public string SizeText
    {
        get => _sizeText;
        set { _sizeText = value; OnPropertyChanged(nameof(SizeText)); }
    }

    private string _durationText = "";
    public string DurationText
    {
        get => _durationText;
        set { _durationText = value; OnPropertyChanged(nameof(DurationText)); }
    }

    private Avalonia.Media.Imaging.Bitmap? _thumbnail;
    public Avalonia.Media.Imaging.Bitmap? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(nameof(Thumbnail)); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public class QueueItemViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    private string _fullPath = "";
    public string FullPath
    {
        get => _fullPath;
        set { _fullPath = value; OnPropertyChanged(nameof(FullPath)); }
    }

    private string _durationText = "";
    public string DurationText
    {
        get => _durationText;
        set { _durationText = value; OnPropertyChanged(nameof(DurationText)); }
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set 
        { 
            _isPlaying = value; 
            OnPropertyChanged(nameof(IsPlaying)); 
            OnPropertyChanged(nameof(ForegroundBrush));
            OnPropertyChanged(nameof(PathForegroundBrush));
        }
    }

    public Avalonia.Media.IBrush ForegroundBrush => IsPlaying ? Avalonia.Media.SolidColorBrush.Parse("#2B88FF") : Avalonia.Media.SolidColorBrush.Parse("#C5CEE3");
    public Avalonia.Media.IBrush PathForegroundBrush => IsPlaying ? Avalonia.Media.SolidColorBrush.Parse("#66AFFF") : Avalonia.Media.SolidColorBrush.Parse("#5C647C");

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
