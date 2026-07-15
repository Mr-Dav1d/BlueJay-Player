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
    private TextBox? _singleCurrentTimeInput;
    private TextBox? _deckCurrentTimeInput;
    private bool _isEditingTime = false;
    private TextBlock[] _totalDurationTexts = Array.Empty<TextBlock>();
    private Slider[] _playbackSliders = Array.Empty<Slider>();
    private Button[] _playPauseButtons = Array.Empty<Button>();
    private PathIcon[] _playPauseIcons = Array.Empty<PathIcon>();
    private Slider[] _volumeSliders = Array.Empty<Slider>();
    private PathIcon[] _volumeIcons = Array.Empty<PathIcon>();
    private System.Threading.CancellationTokenSource? _transportCts;
    private bool _isPaused;
    private int _durationDisplayMode = 0; // 0: Total Duration, 1: Remaining Time, 2: Real-World Finish Time
    private string _currentLoadedFilePath = "";
    private bool _isMuted;
    private double _volumeBeforeMute = 100;
    private bool _isVolumeUpdatingFromMpv;
    private bool _isTacticalDeckMode = false;

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
    private System.Threading.CancellationTokenSource? _hidePreviewCts;
    private int _previewRequestId = 0;
    private DateTime _lastDragSeekTime = DateTime.MinValue;
    private System.Threading.Timer? _debounceTimer;
    private double _hoveredSeekTime = -1;
    private double _latestRequestedHoverTime = -1;
    private Control? _hudPanel;
    private Border? _seekPreviewCard;
    private Panel? _audioModeOverlay;
    private TextBlock? _audioModeTrackTitle;
    private PipWindow? _pipWindow;
    private bool _isPipActive;
    private Control? _pipActivePlaceholder;
    private Button? _returnFromPipButton;
    private Button? _singlePipButton;
    private Button? _deckPipButton;
    
    // Playback Speed Control
    private double _currentSpeed = 1.0;
    private bool _isSyncingSpeed = false;
    private Button? _singleSpeedButton;
    private Button? _deckSpeedButton;
    private TextBlock? _singleSpeedValueText;
    private TextBlock? _deckSpeedValueText;
    private Slider? _singleSpeedSlider;
    private Slider? _deckSpeedSlider;
    private Button? _singleResetSpeedButton;
    private Button? _deckResetSpeedButton;
    private StackPanel? _singlePresetPanel;
    private StackPanel? _deckPresetPanel;

    // Phase 3 state fields
    private System.Collections.ObjectModel.ObservableCollection<QueueItemViewModel> _playbackQueueItems = new();
    private System.Collections.ObjectModel.ObservableCollection<MediaItemViewModel> _directoryItems = new();
    private System.Collections.Generic.List<FileSystemInfo> _allEntries = new();
    private string _currentDirectoryPath = "";
    private string _activeTab = "Queue";
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
    private StackPanel? _breadcrumbContainer;
    private Button? _upDirectoryButton;
    private ListBox? _directoryListBox;
    private ComboBox? _directorySortComboBox;
    private int _directorySortMode = 0;
    private Button? _viewModeToggleButton;
    private TextBlock? _viewModeToggleIcon;
    private bool _isGridView = false;
    private Button[] _sidePanelButtons = Array.Empty<Button>();

    // Video Engine Matrix UI References
    private Button? _engineTabButton;
    private Border? _engineTabIndicator;
    private Panel? _enginePanel;
    
    // HDR Group
    private Border? _hdrSdrIndicator;
    private Border? _hdrActiveIndicator;
    private Button? _hdrSdrButton;
    private Button? _hdrActiveButton;
    
    // Debanding Group
    private Border? _debandOffIndicator;
    private Border? _debandLowIndicator;
    private Border? _debandRefIndicator;
    private Button? _debandOffButton;
    private Button? _debandLowButton;
    private Button? _debandRefButton;
    
    // Scaling Group
    private Border? _scalePerfIndicator;
    private Border? _scaleCinematicIndicator;
    private Button? _scalePerfButton;
    private Button? _scaleCinematicButton;
    
    // Tone-Mapping Group
    private Button? _toneMappingButton;
    private TextBlock? _toneMappingSelectedText;
    private MenuItem? _toneMapBt2446aItem;
    private MenuItem? _toneMapMobiusItem;
    private MenuItem? _toneMapReinhardItem;
    private MenuItem? _toneMapHableItem;
    private MenuItem? _toneMapSplineItem;
    private MenuItem? _toneMapLinearItem;

    // HUD Fullscreen/HDR Integration
    private Button? _singleFullscreenButton;
    private Button? _deckFullscreenButton;
    private PathIcon? _singleFullscreenIcon;
    private PathIcon? _deckFullscreenIcon;
    
    // State variables for engine options
    private bool _isHdrActive = false;
    private string _currentDebandMode = "Off"; // Off, Low, Reference
    private bool _isCinematicScaling = false;
    private string _currentToneMapping = "bt.2446a";

    // System Clock backing variables
    private bool _showSystemClock = false;
    private bool _use24HourClock = true;
    private DispatcherTimer? _systemClockTimer;

    // Phase 2 UI References
    // Refactored Engine Panel UI References
    private TextBlock? _renderingProfileSubText;
    private TextBlock? _gradientSmoothingSubText;
    private TextBlock? _imageSharpnessSubText;

    // Hardware Environment Profile UI References
    private Button? _envEcoButton;
    private Button? _envDesktopButton;
    private Button? _envEnthusiastButton;
    private Border? _envEcoIndicator;
    private Border? _envDesktopIndicator;
    private Border? _envEnthusiastIndicator;
    private TextBlock? _envProfileSubText;

    // Mitigation Telemetry Badges
    private Border? _autoDebandBadge;
    private Border? _deinterlaceBadge;

    // Advanced ComboBoxes
    private ComboBox? _scaleComboBox;
    private ComboBox? _cScaleComboBox;
    private ComboBox? _interpolationComboBox;

    // Synchronization and state tracking
    private bool _isUpdatingProfileProgrammatically = false;

    // Backing snapshots for user manual configurations
    private bool _manualHdrActive = false;
    private string _manualDebandMode = "Off";
    private bool _manualCinematicScaling = false;
    private string _manualToneMapping = "bt.2446a";


    // Settings Page Backing Fields
    private string _defaultBootDirectoryPath = "";
    private bool _rememberLastDirectoryPath = true;
    private string _lastDirectoryPath = "";
    private bool _allowMultipleInstances = false;
    private bool _disableHdrPeak = false;
    private bool _forceSoftwareDecoding = false;
    private string _defaultAudioLanguage = "eng";
    private bool _passthroughAc3 = false;
    private bool _passthroughDts = false;
    private bool _passthroughDtsHd = false;
    private bool _passthroughTrueHd = false;
    private bool _wasapiExclusive = false;
    private string _subtitleFontFamily = "";
    private double _subtitleBorderSize = 3.0;
    private double _subtitleShadowOffset = 1.0;
    private string _defaultWorkspaceTab = "Queue";
    private bool _disableOsdNotifications = false;
    private bool _disableSeekingPreviews = false;

    // Settings Overlay Controls
    private Grid? _masterSettingsOverlay;
    private Button? _settingsCloseButton;
    private Carousel? _settingsCarousel;
    private Button? _navGeneralButton;
    private Button? _navEngineButton;
    private Button? _navAudioButton;
    private Button? _navSubtitlesButton;
    private Button? _navUiButton;
    private TextBox? _defaultBootDirTextBox;
    private Button? _browseBootDirButton;
    private ToggleSwitch? _rememberLastDirToggle;
    private ToggleSwitch? _allowMultipleInstancesToggle;
    private ToggleSwitch? _disableHdrPeakToggle;
    private ToggleSwitch? _hardwareAccelerationToggle;
    private TextBox? _defaultAudioLangTextBox;
    private CheckBox? _passthroughAc3CheckBox;
    private CheckBox? _passthroughDtsCheckBox;
    private CheckBox? _passthroughDtsHdCheckBox;
    private CheckBox? _passthroughTrueHdCheckBox;
    private ToggleSwitch? _wasapiExclusiveToggle;
    private ComboBox? _subtitleFontComboBox;
    private Slider? _subBorderSizeSlider;
    private TextBlock? _subBorderSizeValueText;
    private Slider? _subShadowOffsetSlider;
    private TextBlock? _subShadowOffsetValueText;
    private ComboBox? _defaultWorkspaceTabComboBox;
    private ToggleSwitch? _showOsdNotificationsToggle;
    private ToggleSwitch? _disableSeekingPreviewsToggle;
    private ToggleSwitch? _tacticalDeckToggle;
    private ToggleSwitch? _showSystemClockToggle;
    private ComboBox? _clockFormatComboBox;
    private Border? _clockFormatCard;
    private TextBlock? _singleSystemClockText;
    private TextBlock? _deckSystemClockText;
    private TextBlock? _singleSystemClockSeparator;
    private TextBlock? _deckSystemClockSeparator;

    public MainWindow()
    {
        // InitializeComponent runs first to build the visual context tree from XAML
        InitializeComponent();

        // Load settings from settings.json
        LoadSettings();

        _systemClockTimer = new DispatcherTimer();
        _systemClockTimer.Interval = TimeSpan.FromSeconds(2);
        _systemClockTimer.Tick += OnSystemClockTimerTick;

        // Set BlueJay window icon from embedded asset
        try
        {
            using var iconStream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://BlueJayPlayer/Assets/square_one_app_logo.ico"));
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
        _breadcrumbContainer = this.FindControl<StackPanel>("BreadcrumbContainer");
        _upDirectoryButton = this.FindControl<Button>("UpDirectoryButton");
        _directoryListBox = this.FindControl<ListBox>("DirectoryListBox");
        _directorySortComboBox = this.FindControl<ComboBox>("DirectorySortComboBox");
        _viewModeToggleButton = this.FindControl<Button>("ViewModeToggleButton");
        _viewModeToggleIcon = this.FindControl<TextBlock>("ViewModeToggleIcon");
        
        _engineTabButton = this.FindControl<Button>("EngineTabButton");
        _engineTabIndicator = this.FindControl<Border>("EngineTabIndicator");
        _enginePanel = this.FindControl<Panel>("EnginePanel");

        SetupSidePanel();
        SetupSettingsPanel();
        
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
            _videoSurface.DoubleTapHandler = (s, e) => Dispatcher.UIThread.Post(() => ToggleFullscreen());
            _videoSurface.DoubleTapped += (s, e) => ToggleFullscreen();
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
        _hudPanel.DoubleTapped += (s, e) =>
        {
            var source = e.Source as Avalonia.Visual;
            while (source != null && source != _hudPanel)
            {
                if (source is Button || source is Slider || source is ComboBox || source is TextBox)
                    return;
                source = source.GetVisualParent();
            }
            ToggleFullscreen();
        };

        _transportBar      = FindInTree<Border>(hudPanel, "TransportBar");
        if (_transportBar != null)
        {
            _transportBar.Classes.Remove("tactical-deck");
            _transportBar.Classes.Remove("single-line");
            if (_isTacticalDeckMode)
            {
                _transportBar.Classes.Add("tactical-deck");
            }
            else
            {
                _transportBar.Classes.Add("single-line");
            }
        }
        
        var singleCurrentTimeText   = FindInTree<TextBlock>(hudPanel, "SingleCurrentTimeText");
        var deckCurrentTimeText     = FindInTree<TextBlock>(hudPanel, "DeckCurrentTimeText");
        _currentTimeTexts = new TextBlock[] { singleCurrentTimeText!, deckCurrentTimeText! }.Where(x => x != null).ToArray();
        _singleCurrentTimeInput = FindInTree<TextBox>(hudPanel, "SingleCurrentTimeInput");
        _deckCurrentTimeInput   = FindInTree<TextBox>(hudPanel, "DeckCurrentTimeInput");

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
        _audioModeOverlay  = FindInTree<Panel>(hudPanel, "AudioModeOverlay");
        _audioModeTrackTitle = FindInTree<TextBlock>(hudPanel, "AudioModeTrackTitle");
        _videoTitleOverlay = FindInTree<Border>(hudPanel, "VideoTitleOverlay");
        _videoTitleText    = FindInTree<TextBlock>(hudPanel, "VideoTitleText");

        _pipActivePlaceholder = this.FindControl<Control>("PipActivePlaceholder");
        _returnFromPipButton  = this.FindControl<Button>("ReturnFromPipButton");
        _singlePipButton      = FindInTree<Button>(hudPanel, "SinglePipButton");
        _deckPipButton        = FindInTree<Button>(hudPanel, "DeckPipButton");

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

        _singleFullscreenButton = FindInTree<Button>(hudPanel, "SingleFullscreenButton");
        _deckFullscreenButton = FindInTree<Button>(hudPanel, "DeckFullscreenButton");
        _singleFullscreenIcon = FindInTree<PathIcon>(hudPanel, "SingleFullscreenIcon");
        _deckFullscreenIcon = FindInTree<PathIcon>(hudPanel, "DeckFullscreenIcon");

        _singleSystemClockText = FindInTree<TextBlock>(hudPanel, "SingleSystemClockText");
        _deckSystemClockText = FindInTree<TextBlock>(hudPanel, "DeckSystemClockText");
        _singleSystemClockSeparator = FindInTree<TextBlock>(hudPanel, "SingleSystemClockSeparator");
        _deckSystemClockSeparator = FindInTree<TextBlock>(hudPanel, "DeckSystemClockSeparator");

        _singleSpeedButton = FindInTree<Button>(hudPanel, "SingleSpeedButton");
        _deckSpeedButton = FindInTree<Button>(hudPanel, "DeckSpeedButton");

        if (_showSystemClock)
        {
            UpdateSystemClockText();
            _systemClockTimer?.Start();
        }
        else
        {
            SetSystemClockVisibility(false);
        }

        UpdateHdrUI();
        UpdateDebandUI();
        UpdateScalingUI();

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
                    e.Pointer.Capture(slider);
                    slider.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                    this.Cursor = null;
                    WakeTransportHUD(5000);
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
                (object? sender, PointerReleasedEventArgs e) =>
                {
                    if (!_isUserSeeking) return;
                    _isUserSeeking = false;
                    slider.Cursor = null;
                    e.Pointer.Capture(null);
                    PerformSeekFromSlider(slider);
                    WakeTransportHUD(3000);
                },
                RoutingStrategies.Tunnel);

            slider.AddHandler(
                InputElement.PointerCaptureLostEvent,
                (object? sender, PointerCaptureLostEventArgs e) =>
                {
                    if (!_isUserSeeking) return;
                    _isUserSeeking = false;
                    slider.Cursor = null;
                    PerformSeekFromSlider(slider);
                    WakeTransportHUD(3000);
                },
                RoutingStrategies.Tunnel);
        }

        foreach (var btn in _playPauseButtons)
        {
            btn.Click += (_, _) => TogglePlayPause();
        }

        // Toggle remaining time display when clicking the total duration text
        foreach (var durText in _totalDurationTexts)
        {
            durText.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            durText.PointerPressed += (_, _) =>
            {
                _durationDisplayMode = (_durationDisplayMode + 1) % 3;
                UpdateTransportUI(_playbackPosition, _playbackDuration);
            };
        }

        var hudRoot = _videoSurface?.Content as Avalonia.Controls.Control;

        var singleMuteBtn = hudRoot != null ? FindInTree<Button>(hudRoot, "SingleMuteButton") : null;
        var deckMuteBtn = hudRoot != null ? FindInTree<Button>(hudRoot, "DeckMuteButton") : null;
        var muteBtns = new Button[] { singleMuteBtn!, deckMuteBtn! }.Where(x => x != null);
        foreach (var muteBtn in muteBtns)
        {
            muteBtn.Click += (_, _) => ToggleMute();
        }

        var fullscreenBtns = new Button?[] { _singleFullscreenButton, _deckFullscreenButton }.Where(x => x != null);
        foreach (var fsBtn in fullscreenBtns)
        {
            fsBtn!.Click += (_, _) => ToggleFullscreen();
        }

        var pipBtns = new Button?[] { _singlePipButton, _deckPipButton, _returnFromPipButton }.Where(x => x != null);
        foreach (var pipBtn in pipBtns)
        {
            pipBtn!.Click += (_, _) => TogglePip();
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
            SetMpvOptionString(_mpvHandle, "hwdec", _forceSoftwareDecoding ? "no" : "auto");
            SetMpvOptionString(_mpvHandle, "target-peak", "auto");
            SetMpvOptionString(_mpvHandle, "sub-auto", "all");
            SetMpvOptionString(_mpvHandle, "audio-display", "attachment");

            int initResult = MpvInitialize(_mpvHandle);
            Log($"Engine initialization code returned: {initResult}");

            if (initResult == 0)
            {
                _isEngineInitialized = true;
                InitializePreviewEngine();

                // Safely allocate unmanaged UTF-8 strings to pass down to C++
                IntPtr timePropPtr = Marshal.StringToCoTaskMemUTF8("playback-time");
                IntPtr durationPropPtr = Marshal.StringToCoTaskMemUTF8("duration");
                IntPtr volumePropPtr = Marshal.StringToCoTaskMemUTF8("volume");
                IntPtr pausePropPtr = Marshal.StringToCoTaskMemUTF8("pause");

                // Format 5 = MPV_FORMAT_DOUBLE
                MpvObserveProperty(_mpvHandle, 101, timePropPtr, 5);
                MpvObserveProperty(_mpvHandle, 102, durationPropPtr, 5);
                MpvObserveProperty(_mpvHandle, 103, volumePropPtr, 5);
                // Format 6 = MPV_FORMAT_FLAG
                MpvObserveProperty(_mpvHandle, 104, pausePropPtr, 6);

                Marshal.FreeCoTaskMem(timePropPtr);
                Marshal.FreeCoTaskMem(durationPropPtr);
                Marshal.FreeCoTaskMem(volumePropPtr);
                Marshal.FreeCoTaskMem(pausePropPtr);

                _eventLoopCts = new CancellationTokenSource();
                Task.Run(() => RunEventLoop(_eventLoopCts.Token));

                ApplyHdrConfig(_isHdrActive);
                ApplyDebandConfig(_currentDebandMode);
                ApplyScalingConfig(_isCinematicScaling);
                ApplyToneMappingConfig(_currentToneMapping);
                ApplyAudioSettings();
                ApplyTypographySettings();
                ApplyDecodingSettings();

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
            try
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
                else if (mpvEvent.EventId == 8) // MPV_EVENT_FILE_LOADED
                {
                    Log("RunEventLoop: MPV_EVENT_FILE_LOADED received.");

                    // Unconditionally re-apply current playback speed setting to new track
                    if (_mpvHandle != IntPtr.Zero)
                    {
                        SetMpvPropertyDouble(_mpvHandle, "speed", _currentSpeed);
                        Log($"RunEventLoop (FILE_LOADED): Re-applied playback speed rate: {_currentSpeed:F2}x");
                    }

                    if (_isPipActive && _pipWindow != null)
                    {
                        double aspect = GetMpvPropertyDouble("video-params/aspect");
                        if (!double.IsNaN(aspect) && !double.IsInfinity(aspect) && aspect > 0)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (_pipWindow != null)
                                {
                                    _pipWindow.AspectRatio = aspect;
                                }
                            });
                        }

                        if (_pipWindow.ChildSurfaceHandle != IntPtr.Zero)
                        {
                            IntPtr localMpv = _mpvHandle;
                            IntPtr localSurface = _pipWindow.ChildSurfaceHandle;
                            Task.Run(() =>
                            {
                                if (localMpv != IntPtr.Zero && localSurface != IntPtr.Zero)
                                {
                                    SetMpvPropertyString(localMpv, "wid", localSurface.ToString());
                                    Log($"RunEventLoop (FILE_LOADED): Re-asserted PiP wid: {localSurface}");
                                    PipLogger.Log($"RunEventLoop (FILE_LOADED): Re-asserted PiP wid: {localSurface}");
                                }
                            });
                        }
                    }

                    // Reset badges on UI thread
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_autoDebandBadge != null) _autoDebandBadge.IsVisible = false;
                        if (_deinterlaceBadge != null) _deinterlaceBadge.IsVisible = false;
                    });

                    // Check if loaded media is an audio file
                    bool isAudioTrack = !string.IsNullOrEmpty(_currentLoadedFilePath) && IsAudioFile(Path.GetExtension(_currentLoadedFilePath));

                    // Read video dimensions from libmpv (embedded album art or video stream)
                    string? videoWStr = GetMpvPropertyString("video-params/w");
                    bool hasVideoTrack = double.TryParse(videoWStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double videoW) && videoW > 0;

                    if (isAudioTrack)
                    {
                        if (hasVideoTrack)
                        {
                            Log($"[Media Safeguard] Audio track with embedded album art detected ({videoW}px width). Rendering cover art across viewport.");
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (_idleLogoPanel != null) _idleLogoPanel.IsVisible = false;
                                if (_audioModeOverlay != null) _audioModeOverlay.IsVisible = false;
                            });
                        }
                        else
                        {
                            Log("[Media Safeguard] Audio-only track without cover art. Displaying minimal audio overlay.");
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (_idleLogoPanel != null) _idleLogoPanel.IsVisible = false;
                                if (_audioModeOverlay != null) _audioModeOverlay.IsVisible = true;
                                if (_audioModeTrackTitle != null && !string.IsNullOrEmpty(_currentLoadedFilePath))
                                {
                                    _audioModeTrackTitle.Text = Path.GetFileNameWithoutExtension(_currentLoadedFilePath);
                                }
                            });
                        }
                        // Clear video filter for album art
                        SetMpvPropertyString(_mpvHandle, "vf", "");
                        // Bypass HDR/peak scaling and low-bitrate debanding filters for cover art
                        continue;
                    }

                    // Clear video filter first
                    SetMpvPropertyString(_mpvHandle, "vf", "");

                    // Re-apply baseline manual debanding configuration
                    ApplyDebandConfig(_manualDebandMode);

                    // Extract active track properties
                    double videoBitrate = GetMpvPropertyDouble("video-bitrate");
                    if (double.IsNaN(videoBitrate) || videoBitrate <= 0)
                    {
                        videoBitrate = GetMpvPropertyDouble("track-list/0/demux-bitrate");
                    }

                    // Check if bitrate < 2500 kbps (2,500,000 bps)
                    if (videoBitrate > 0 && videoBitrate < 2500000)
                    {
                        Log($"[Auto-Mitigation] Low bitrate detected ({videoBitrate / 1000.0:F1} kbps). Applying strong auto-deband.");
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_autoDebandBadge != null) _autoDebandBadge.IsVisible = true;
                        });

                        // Apply strong multi-iteration deband
                        SetMpvPropertyString(_mpvHandle, "deband", "yes");
                        SetMpvPropertyString(_mpvHandle, "deband-iterations", "4");
                        SetMpvPropertyString(_mpvHandle, "deband-threshold", "64");
                        SetMpvPropertyString(_mpvHandle, "deband-range", "20");
                        SetMpvPropertyString(_mpvHandle, "deband-grain", "48");
                    }

                    // Check if media is interlaced
                    string? interlacedStr = GetMpvPropertyString("video-params/interlaced");
                    if (interlacedStr == "yes")
                    {
                        Log("[Auto-Mitigation] Interlaced media detected. Injecting bwdif filter.");
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_deinterlaceBadge != null) _deinterlaceBadge.IsVisible = true;
                        });

                        // Inject deinterlacing filter
                        SetMpvPropertyString(_mpvHandle, "vf", "bwdif");
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
            catch (Exception ex)
            {
                Log($"RunEventLoop Tick Error: {ex.Message}");
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
        try
        {
            switch (propertyName)
            {
                case "playback-time":
                case "time-pos":
                    if (_isUserSeeking) break;
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
        catch (Exception ex)
        {
            Log($"Error in ProcessObservedProperty for {propertyName}: {ex.Message}");
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

        try
        {
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
        catch (Exception ex)
        {
            Log($"Error populating audio tracks: {ex.Message}");
        }
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

        try
        {
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
                            var options = new FilePickerOpenOptions
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
                            };

                            string? targetFolder = null;
                            if (!string.IsNullOrEmpty(_currentLoadedFilePath))
                            {
                                targetFolder = System.IO.Path.GetDirectoryName(_currentLoadedFilePath);
                            }
                            if ((string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder)) && !string.IsNullOrEmpty(_currentDirectoryPath))
                            {
                                targetFolder = _currentDirectoryPath;
                            }

                            if (!string.IsNullOrEmpty(targetFolder) && Directory.Exists(targetFolder))
                            {
                                options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(targetFolder);
                            }

                            var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);

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
        catch (Exception ex)
        {
            Log($"Error populating subtitle tracks: {ex.Message}");
        }
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
            if (_isEditingTime) return;

            if (_playbackSliders.Length == 0 && _currentTimeTexts.Length == 0 && _totalDurationTexts.Length == 0)
            {
                Log("UpdateTransportUI: one or more controls are null, skipping");
                return;
            }

            if (total >= 0)
            {
                foreach (var slider in _playbackSliders)
                    slider.Maximum = total;
            }

            // Avoid updating slider position if user is seeking or if a seek was performed recently (within 600ms) to prevent rubber-banding
            if (!_isUserSeeking && (DateTime.UtcNow - _lastSeekTime).TotalMilliseconds > 600)
            {
                foreach (var slider in _playbackSliders)
                    slider.Value = current;

                string currentStr = TimeSpan.FromSeconds(current).ToString(@"hh\:mm\:ss");
                foreach (var txt in _currentTimeTexts)
                    txt.Text = currentStr;
            }

            double remaining = Math.Max(0, total - current);
            string durationDisplay;

            if (_durationDisplayMode == 1)
            {
                durationDisplay = "-" + TimeSpan.FromSeconds(remaining).ToString(@"hh\:mm\:ss");
            }
            else if (_durationDisplayMode == 2)
            {
                DateTime finishTime = DateTime.Now.AddSeconds(remaining);
                string timeFmt = _use24HourClock ? "HH:mm" : "h:mm tt";
                durationDisplay = $"Ends at {finishTime.ToString(timeFmt)}";
            }
            else
            {
                durationDisplay = TimeSpan.FromSeconds(total).ToString(@"hh\:mm\:ss");
            }

            foreach (var txt in _totalDurationTexts)
                txt.Text = durationDisplay;

            if (_isPipActive && _pipWindow != null)
            {
                string mediaTitle = Path.GetFileName(_currentLoadedFilePath ?? "BlueJay Media Stream");
                _pipWindow.UpdateTime(current, total, mediaTitle);
            }
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
        this.Focus();
    }

    private void UpdatePlayPauseIcon()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var icon in _playPauseIcons)
                icon.Data = StreamGeometry.Parse(_isPaused ? PlaySvg : PauseSvg);
            _pipWindow?.UpdatePlayState(!_isPaused);
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

    private void ApplyLayoutMode()
    {
        if (_transportBar == null) return;
        
        _transportBar.Classes.Remove("tactical-deck");
        _transportBar.Classes.Remove("single-line");
        if (_isTacticalDeckMode)
        {
            _transportBar.Classes.Add("tactical-deck");
            Log("Layout switched to Tactical Deck mode.");
        }
        else
        {
            _transportBar.Classes.Add("single-line");
            Log("Layout switched to Single Line mode.");
        }
        
        // Force parent container layout measurement invalidation pass
        _transportBar.InvalidateMeasure();
        _transportBar.InvalidateVisual();
        _videoSurface?.SyncOverlayGeometry();
    }

    private void OnSystemClockTimerTick(object? sender, EventArgs e)
    {
        UpdateSystemClockText();
    }

    private void UpdateSystemClockText()
    {
        if (!_showSystemClock)
        {
            SetSystemClockVisibility(false);
            _systemClockTimer?.Stop();
            return;
        }

        SetSystemClockVisibility(true);
        string format = _use24HourClock ? "HH:mm" : "h:mm tt";
        string clockStr = DateTime.Now.ToString(format);

        if (_singleSystemClockText != null) _singleSystemClockText.Text = clockStr;
        if (_deckSystemClockText != null) _deckSystemClockText.Text = clockStr;
    }

    private void SetSystemClockVisibility(bool visible)
    {
        if (_singleSystemClockText != null) _singleSystemClockText.IsVisible = visible;
        if (_deckSystemClockText != null) _deckSystemClockText.IsVisible = visible;
        if (_singleSystemClockSeparator != null) _singleSystemClockSeparator.IsVisible = visible;
        if (_deckSystemClockSeparator != null) _deckSystemClockSeparator.IsVisible = visible;
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

            if (_isEditingTime)
            {
                _transportBar.Opacity = 1.0;
                if (_videoTitleOverlay != null)
                {
                    _videoTitleOverlay.Opacity = 1.0;
                }
                _transportCts = null;
                return;
            }

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
                        if (_isUserSeeking) return;

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
        if (_disableOsdNotifications) return;
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
        _currentLoadedFilePath = filePath;

        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized)
        {
            Log($"PlayMediaFile skipped — engine not ready (handle={_mpvHandle}, initialized={_isEngineInitialized}). File: {filePath}");
            return;
        }

        string ext = Path.GetExtension(filePath);
        if (!IsSupportedMediaFile(ext))
        {
            Log($"PlayMediaFile blocked — unsupported media file format: '{ext}' for file '{filePath}'");
            return;
        }

        bool isAudio = IsAudioFile(ext);

        // Reset seek bar tracking variables immediately when loading a new file
        _playbackPosition = 0;
        _playbackDuration = 0;
        _lastLoggedSecond = -1;
        UpdateTransportUI(0, 0);

        // Ensure vid=auto so libmpv can load embedded album art attachments or video streams
        SetMpvPropertyString(_mpvHandle, "vid", "auto");

        if (isAudio)
        {
            // Force safe stereo device fallback for audio tracks
            SetMpvPropertyString(_mpvHandle, "audio-spdif", "");
            SetMpvPropertyString(_mpvHandle, "audio-channels", "stereo");
        }
        else
        {
            // Restore cinema video audio multi-channel profiles
            ApplyAudioSettings();
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

            // Initially hide overlays; MPV_EVENT_FILE_LOADED will toggle album art vs minimal audio overlay
            if (_idleLogoPanel != null) _idleLogoPanel.IsVisible = false;
            if (!isAudio && _audioModeOverlay != null) _audioModeOverlay.IsVisible = false;

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
        CleanPreviewTempDir();
        SendCommand(_mpvHandle, "loadfile", filePath);

        if (_previewMpvHandle != IntPtr.Zero)
        {
            SendCommand(_previewMpvHandle, "loadfile", filePath);
        }

        this.Focus();
    }

    private void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                this.Activate();
            }
            e.Handled = false;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = false;
            return;
        }

        if (!_isEngineInitialized || _mpvHandle == IntPtr.Zero) return;

        // Hardware Media Keys Integration
        if (e.Key == Key.MediaPlayPause)
        {
            e.Handled = true;
            TogglePlayPause();
            return;
        }
        if (e.Key == Key.MediaNextTrack)
        {
            e.Handled = true;
            PlayNextInQueue();
            return;
        }
        if (e.Key == Key.MediaPreviousTrack)
        {
            e.Handled = true;
            PlayPreviousInQueue();
            return;
        }
        if (e.Key == Key.MediaStop)
        {
            e.Handled = true;
            StopPlayback();
            return;
        }

        // Ctrl+, Settings toggle shortcut
        if (e.Key == Key.OemComma && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            if (_masterSettingsOverlay != null)
            {
                if (_masterSettingsOverlay.IsVisible)
                    CloseSettings();
                else
                    OpenSettings();
            }
            return;
        }

        // 1. Fullscreen Engine & UI Toggle Layout Alignment
        if (e.Key == Key.F || (e.Key == Key.Escape && this.WindowState == WindowState.FullScreen))
        {
            e.Handled = true;
            ToggleFullscreen();
            return;
        }

        if (e.Key == Key.P)
        {
            e.Handled = true;
            TogglePip();
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

        if (e.Key == Key.OemOpenBrackets)
        {
            e.Handled = true;
            SetSpeed(_currentSpeed - 0.1);
            TriggerOsdHUD("⚡", $"Playback Speed: {_currentSpeed:F2}x");
            return;
        }
        if (e.Key == Key.OemCloseBrackets)
        {
            e.Handled = true;
            SetSpeed(_currentSpeed + 0.1);
            TriggerOsdHUD("⚡", $"Playback Speed: {_currentSpeed:F2}x");
            return;
        }
        if (e.Key == Key.OemBackslash)
        {
            e.Handled = true;
            SetSpeed(1.0);
            TriggerOsdHUD("⚡", "Playback Speed: 1.00x");
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
        if (mpvHandle == IntPtr.Zero || args == null || args.Length == 0) return;

        IntPtr[] ptrArray = new IntPtr[args.Length + 1];
        for (int i = 0; i < args.Length; i++)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(args[i] + "\0");
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            ptrArray[i] = ptr;
        }
        ptrArray[args.Length] = IntPtr.Zero;

        IntPtr unmanagedArray = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(IntPtr)) * ptrArray.Length);
        Marshal.Copy(ptrArray, 0, unmanagedArray, ptrArray.Length);

        MpvCommand(mpvHandle, unmanagedArray);

        for (int i = 0; i < args.Length; i++)
        {
            if (ptrArray[i] != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptrArray[i]);
            }
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

        if (_pipWindow != null)
        {
            _pipWindow.IsShuttingDown = true;
            _pipWindow.Close();
            _pipWindow = null;
        }

        // Save settings on exit
        SaveSettings();
        
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

    private void SetSpeed(double speed, bool updateControls = true)
    {
        if (_isSyncingSpeed) return;
        _isSyncingSpeed = true;
        try
        {
            // 1. Clamp speed [0.25, 2.0] and update '_currentSpeed'
            _currentSpeed = Math.Clamp(speed, 0.25, 2.0);

            // 2. Commit the speed asynchronously to libmpv
            IntPtr localMpv = _mpvHandle;
            Task.Run(() =>
            {
                if (localMpv != IntPtr.Zero)
                {
                    SetMpvPropertyDouble(localMpv, "speed", _currentSpeed);
                }
            });

            // 3. Update active transport button labels
            if (_singleSpeedButton != null) _singleSpeedButton.Content = $"{_currentSpeed:F2}x";
            if (_deckSpeedButton != null) _deckSpeedButton.Content = $"{_currentSpeed:F2}x";

            // 4. Update the Flyout UI Controls if cached
            if (updateControls)
            {
                if (_singleSpeedSlider != null) _singleSpeedSlider.Value = _currentSpeed;
                if (_deckSpeedSlider != null) _deckSpeedSlider.Value = _currentSpeed;
            }

            // 5. Update Value Textblocks live
            if (_singleSpeedValueText != null) _singleSpeedValueText.Text = $"{_currentSpeed:F2}x";
            if (_deckSpeedValueText != null) _deckSpeedValueText.Text = $"{_currentSpeed:F2}x";

            // 6. Toggle dynamic Reset Link visibility
            bool showReset = Math.Abs(_currentSpeed - 1.0) > 0.01;
            if (_singleResetSpeedButton != null) _singleResetSpeedButton.IsVisible = showReset;
            if (_deckResetSpeedButton != null) _deckResetSpeedButton.IsVisible = showReset;

            // 7. Update preset chip active classes
            UpdateActiveChipClass(_singlePresetPanel, _currentSpeed);
            UpdateActiveChipClass(_deckPresetPanel, _currentSpeed);
        }
        finally
        {
            _isSyncingSpeed = false;
        }
    }

    private void UpdateActiveChipClass(StackPanel? panel, double speed)
    {
        if (panel == null) return;
        foreach (var child in panel.Children)
        {
            if (child is Button btn)
            {
                if (btn.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double presetVal))
                {
                    bool isActive = Math.Abs(presetVal - speed) < 0.01;
                    if (isActive)
                    {
                        if (!btn.Classes.Contains("active")) btn.Classes.Add("active");
                    }
                    else
                    {
                        btn.Classes.Remove("active");
                    }
                }
            }
        }
    }

    private void SpeedFlyout_Opened(object? sender, EventArgs e)
    {
        if (sender is Flyout flyout && flyout.Content is Control content)
        {
            // Resolve controls based on Single or Deck layout
            var singleSpeedSlider = FindInTree<Slider>(content, "SingleSpeedSlider");
            if (singleSpeedSlider != null)
            {
                _singleSpeedSlider = singleSpeedSlider;
                _singleSpeedValueText = FindInTree<TextBlock>(content, "SingleSpeedValueText");
                _singleResetSpeedButton = FindInTree<Button>(content, "SingleResetSpeedButton");
                _singlePresetPanel = FindInTree<StackPanel>(content, "SinglePresetPanel");
            }

            var deckSpeedSlider = FindInTree<Slider>(content, "DeckSpeedSlider");
            if (deckSpeedSlider != null)
            {
                _deckSpeedSlider = deckSpeedSlider;
                _deckSpeedValueText = FindInTree<TextBlock>(content, "DeckSpeedValueText");
                _deckResetSpeedButton = FindInTree<Button>(content, "DeckResetSpeedButton");
                _deckPresetPanel = FindInTree<StackPanel>(content, "DeckPresetPanel");
            }

            // Immediately synchronize visual states without updating slider to prevent recursive triggering
            SetSpeed(_currentSpeed, updateControls: true);
        }
    }

    private void SpeedSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_isSyncingSpeed)
        {
            var speed = Math.Round(e.NewValue, 2);
            SetSpeed(speed, updateControls: false);
        }
    }

    private void SpeedPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && double.TryParse(tag, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            SetSpeed(speed);
            TriggerOsdHUD("⚡", $"Playback Speed: {speed:F2}x");
        }
    }

    private void ResetSpeed_Click(object? sender, RoutedEventArgs e)
    {
        SetSpeed(1.0);
        TriggerOsdHUD("⚡", "Playback Speed: 1.00x");
    }

    private void TransportSpeedLabel_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y > 0 ? 0.05 : -0.05;
        double newSpeed = Math.Clamp(_currentSpeed + delta, 0.25, 2.0);
        SetSpeed(newSpeed);
        TriggerOsdHUD("⚡", $"Playback Speed: {newSpeed:F2}x");
        e.Handled = true;
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
            SetMpvOptionString(_previewMpvHandle, "hr-seek", "no");
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
        _hidePreviewCts?.Cancel();
        _hidePreviewCts = null;

        if (_disableSeekingPreviews) return;
        if (!string.IsNullOrEmpty(_currentLoadedFilePath) && IsAudioFile(Path.GetExtension(_currentLoadedFilePath)))
        {
            if (_seekPreviewCard != null) _seekPreviewCard.Opacity = 0.0;
            return;
        }
        var card = _seekPreviewCard;
        if (card != null) card.Opacity = 1.0;
    }

    private void OnSliderPointerExited(object? sender, PointerEventArgs e)
    {
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        _hidePreviewCts?.Cancel();
        _hidePreviewCts = new System.Threading.CancellationTokenSource();
        var token = _hidePreviewCts.Token;

        Task.Delay(150, token).ContinueWith(t =>
        {
            if (!t.IsCanceled && !token.IsCancellationRequested)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_seekPreviewCard != null && !_isUserSeeking)
                    {
                        _seekPreviewCard.Opacity = 0.0;
                    }
                });
            }
        }, TaskContinuationOptions.None);
    }

    private void OnSliderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_disableSeekingPreviews) return;
        if (!string.IsNullOrEmpty(_currentLoadedFilePath) && IsAudioFile(Path.GetExtension(_currentLoadedFilePath)))
        {
            if (_seekPreviewCard != null) _seekPreviewCard.Opacity = 0.0;
            return;
        }
        if (sender is not Slider slider || _playbackDuration <= 0) return;

        var point = e.GetCurrentPoint(slider);
        double mouseX = point.Position.X;
        double sliderWidth = slider.Bounds.Width;
        if (sliderWidth <= 0) return;

        double ratio = mouseX / sliderWidth;
        ratio = Math.Max(0, Math.Min(1, ratio));
        _hoveredSeekTime = ratio * _playbackDuration;

        // Perform continuous live seeking while user is actively dragging the slider
        if (_isUserSeeking)
        {
            this.Cursor = null;
            slider.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            WakeTransportHUD(5000);

            if (_seekPreviewCard != null)
            {
                _seekPreviewCard.Opacity = 1.0;
            }

            double newValue = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
            foreach (var s in _playbackSliders)
            {
                s.Value = newValue;
            }

            string dragTimeStr = TimeSpan.FromSeconds(newValue).ToString(@"hh\:mm\:ss");
            foreach (var txt in _currentTimeTexts)
            {
                txt.Text = dragTimeStr;
            }

            // Throttle physical seek P/Invoke commands to libmpv every 40ms to prevent native decoder queue congestion
            if ((DateTime.UtcNow - _lastDragSeekTime).TotalMilliseconds >= 40)
            {
                _lastDragSeekTime = DateTime.UtcNow;
                PerformSeekFromSlider(slider);
            }
        }

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

        _latestRequestedHoverTime = _hoveredSeekTime;
        int currentReqId = ++_previewRequestId;

        // Synchronously cancel previous extraction task on UI thread before scheduling
        _previewCts?.Cancel();

        // Suppress background thumbnail extraction while actively dragging live
        if (!_isUserSeeking)
        {
            _previewCts = new System.Threading.CancellationTokenSource();
            var token = _previewCts.Token;

            _debounceTimer?.Dispose();
            double targetTime = _hoveredSeekTime;
            _debounceTimer = new System.Threading.Timer(_ =>
            {
                Task.Run(() => ExtractPreviewFrameAsync(targetTime, currentReqId, token));
            }, null, 75, System.Threading.Timeout.Infinite);
        }
    }

    private void CleanPreviewTempDir()
    {
        try
        {
            if (!string.IsNullOrEmpty(_previewTempDir) && Directory.Exists(_previewTempDir))
            {
                foreach (var file in Directory.GetFiles(_previewTempDir, "*.jpg"))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"CleanPreviewTempDir error: {ex.Message}");
        }
    }

    private async Task ExtractPreviewFrameAsync(double timeSeconds, int requestId, CancellationToken token)
    {
        if (_previewMpvHandle == IntPtr.Zero || token.IsCancellationRequested || requestId != _previewRequestId) return;
        if (Math.Abs(timeSeconds - _latestRequestedHoverTime) > 0.01) return;

        string currentFile = Path.GetFileName(_currentLoadedFilePath ?? "Unknown");
        Log($"[Preview Telemetry] Frame extraction initiated #{requestId} | Target: {timeSeconds:F3}s | File: '{currentFile}'");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrEmpty(_previewTempDir)) return;
            string targetPath = Path.Combine(_previewTempDir, $"preview_frame_{requestId}.jpg");

            if (token.IsCancellationRequested || requestId != _previewRequestId || Math.Abs(timeSeconds - _latestRequestedHoverTime) > 0.01) return;

            // 1. Perform seek and direct screenshot to explicit unique filename
            SendCommand(_previewMpvHandle, "seek", timeSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute");
            SendCommand(_previewMpvHandle, "screenshot-to-file", targetPath, "video");

            // 2. Poll explicitly for targetPath to appear without directory scanning
            string? imgPath = null;
            while (stopwatch.ElapsedMilliseconds < 650)
            {
                if (token.IsCancellationRequested || requestId != _previewRequestId || Math.Abs(timeSeconds - _latestRequestedHoverTime) > 0.01)
                {
                    if (File.Exists(targetPath)) try { File.Delete(targetPath); } catch { }
                    return;
                }

                if (File.Exists(targetPath))
                {
                    try
                    {
                        var fi = new FileInfo(targetPath);
                        if (fi.Length > 0)
                        {
                            imgPath = targetPath;
                            break;
                        }
                    }
                    catch { }
                }
                await Task.Delay(15, token);
            }

            if (imgPath == null || !File.Exists(imgPath))
            {
                if (!token.IsCancellationRequested && requestId == _previewRequestId && Math.Abs(timeSeconds - _latestRequestedHoverTime) <= 0.01)
                {
                    Log($"[Preview Timeout] libmpv failed to output frame #{requestId} within 650ms at timestamp {timeSeconds:F3}s. (Elapsed: {stopwatch.ElapsedMilliseconds}ms)");
                }
                return;
            }

            if (token.IsCancellationRequested || requestId != _previewRequestId || Math.Abs(timeSeconds - _latestRequestedHoverTime) > 0.01)
            {
                if (File.Exists(targetPath)) try { File.Delete(targetPath); } catch { }
                return;
            }

            // 3. Read with retry loop to handle file write locks cleanly
            Avalonia.Media.Imaging.Bitmap? bitmap = null;
            int retries = 0;
            while (retries < 5 && bitmap == null && !token.IsCancellationRequested && requestId == _previewRequestId && Math.Abs(timeSeconds - _latestRequestedHoverTime) <= 0.01)
            {
                try
                {
                    using (var stream = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                    }
                }
                catch (IOException ioEx)
                {
                    Log($"[Preview Lock Retry {retries + 1}/5] File locked reading '{Path.GetFileName(imgPath)}': {ioEx.Message}");
                    await Task.Delay(15, token);
                    retries++;
                }
            }

            stopwatch.Stop();

            if (bitmap != null && !token.IsCancellationRequested && requestId == _previewRequestId && Math.Abs(timeSeconds - _latestRequestedHoverTime) <= 0.01)
            {
                Log($"[Preview Telemetry] Frame #{requestId} generated successfully in {stopwatch.ElapsedMilliseconds}ms | Target: {timeSeconds:F3}s");
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested || requestId != _previewRequestId) return;
                    var previewImage = _seekPreviewCard != null ? FindInTree<Image>(_seekPreviewCard, "PreviewImage") : null;
                    if (previewImage != null)
                    {
                        previewImage.Source = bitmap;
                        UpdatePreviewContainerAspect();
                    }
                });
            }
            else
            {
                if (File.Exists(targetPath)) try { File.Delete(targetPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log($"[Preview Exception] Error extracting frame at {timeSeconds:F3}s: {ex.Message}");
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
        if (_engineTabButton != null) _engineTabButton.Click += (s, e) => SwitchTab("Engine");
        if (_upDirectoryButton != null) _upDirectoryButton.Click += (s, e) => NavigateUpDirectory();
        if (_viewModeToggleButton != null) _viewModeToggleButton.Click += (s, e) => ToggleViewMode();

        if (_directorySortComboBox != null)
        {
            _directorySortComboBox.SelectedIndex = _directorySortMode;
            _directorySortComboBox.SelectionChanged += (s, e) =>
            {
                _directorySortMode = _directorySortComboBox.SelectedIndex;
                SaveSettings();
                if (!string.IsNullOrEmpty(_currentDirectoryPath))
                {
                    LoadDirectory(_currentDirectoryPath);
                }
            };
        }

        // Resolve Video Engine Matrix controls
        _renderingProfileSubText = this.FindControl<TextBlock>("RenderingProfileSubText");
        _gradientSmoothingSubText = this.FindControl<TextBlock>("GradientSmoothingSubText");
        _imageSharpnessSubText = this.FindControl<TextBlock>("ImageSharpnessSubText");


        _hdrSdrIndicator = this.FindControl<Border>("HdrSdrIndicator");
        _hdrActiveIndicator = this.FindControl<Border>("HdrActiveIndicator");
        _hdrSdrButton = this.FindControl<Button>("HdrSdrButton");
        _hdrActiveButton = this.FindControl<Button>("HdrActiveButton");
        
        if (_hdrSdrButton != null) _hdrSdrButton.Click += (s, e) => ApplyHdrConfig(false);
        if (_hdrActiveButton != null) _hdrActiveButton.Click += (s, e) => ApplyHdrConfig(true);
        
        _debandOffIndicator = this.FindControl<Border>("DebandOffIndicator");
        _debandLowIndicator = this.FindControl<Border>("DebandLowIndicator");
        _debandRefIndicator = this.FindControl<Border>("DebandRefIndicator");
        _debandOffButton = this.FindControl<Button>("DebandOffButton");
        _debandLowButton = this.FindControl<Button>("DebandLowButton");
        _debandRefButton = this.FindControl<Button>("DebandRefButton");
        
        if (_debandOffButton != null) _debandOffButton.Click += (s, e) => ApplyDebandConfig("Off");
        if (_debandLowButton != null) _debandLowButton.Click += (s, e) => ApplyDebandConfig("Low");
        if (_debandRefButton != null) _debandRefButton.Click += (s, e) => ApplyDebandConfig("Reference");
        
        _scalePerfIndicator = this.FindControl<Border>("ScalePerfIndicator");
        _scaleCinematicIndicator = this.FindControl<Border>("ScaleCinematicIndicator");
        _scalePerfButton = this.FindControl<Button>("ScalePerfButton");
        _scaleCinematicButton = this.FindControl<Button>("ScaleCinematicButton");
        
        if (_scalePerfButton != null) _scalePerfButton.Click += (s, e) => ApplyScalingConfig(false);
        if (_scaleCinematicButton != null) _scaleCinematicButton.Click += (s, e) => ApplyScalingConfig(true);
        
        _toneMappingButton = this.FindControl<Button>("ToneMappingButton");
        _toneMappingSelectedText = this.FindControl<TextBlock>("ToneMappingSelectedText");
        
        _toneMapBt2446aItem = this.FindControl<MenuItem>("ToneMapBt2446aItem");
        _toneMapMobiusItem = this.FindControl<MenuItem>("ToneMapMobiusItem");
        _toneMapReinhardItem = this.FindControl<MenuItem>("ToneMapReinhardItem");
        _toneMapHableItem = this.FindControl<MenuItem>("ToneMapHableItem");
        _toneMapSplineItem = this.FindControl<MenuItem>("ToneMapSplineItem");
        _toneMapLinearItem = this.FindControl<MenuItem>("ToneMapLinearItem");
        
        if (_toneMapBt2446aItem != null) _toneMapBt2446aItem.Click += (s, e) => ApplyToneMappingConfig("bt.2446a");
        if (_toneMapMobiusItem != null) _toneMapMobiusItem.Click += (s, e) => ApplyToneMappingConfig("mobius");
        if (_toneMapReinhardItem != null) _toneMapReinhardItem.Click += (s, e) => ApplyToneMappingConfig("reinhard");
        if (_toneMapHableItem != null) _toneMapHableItem.Click += (s, e) => ApplyToneMappingConfig("hable");
        if (_toneMapSplineItem != null) _toneMapSplineItem.Click += (s, e) => ApplyToneMappingConfig("spline");
        if (_toneMapLinearItem != null) _toneMapLinearItem.Click += (s, e) => ApplyToneMappingConfig("linear");

        // Resolve and bind Hardware Environment Profile & Advanced tuning controls
        _envEcoButton = this.FindControl<Button>("EnvEcoButton");
        _envDesktopButton = this.FindControl<Button>("EnvDesktopButton");
        _envEnthusiastButton = this.FindControl<Button>("EnvEnthusiastButton");
        _envEcoIndicator = this.FindControl<Border>("EnvEcoIndicator");
        _envDesktopIndicator = this.FindControl<Border>("EnvDesktopIndicator");
        _envEnthusiastIndicator = this.FindControl<Border>("EnvEnthusiastIndicator");
        _envProfileSubText = this.FindControl<TextBlock>("EnvProfileSubText");

        _autoDebandBadge = this.FindControl<Border>("AutoDebandBadge");
        _deinterlaceBadge = this.FindControl<Border>("DeinterlaceBadge");

        _scaleComboBox = this.FindControl<ComboBox>("ScaleComboBox");
        _cScaleComboBox = this.FindControl<ComboBox>("CScaleComboBox");
        _interpolationComboBox = this.FindControl<ComboBox>("InterpolationComboBox");

        if (_envEcoButton != null) _envEcoButton.Click += (s, e) => ApplyEnvironmentProfile("Eco");
        if (_envDesktopButton != null) _envDesktopButton.Click += (s, e) => ApplyEnvironmentProfile("Desktop");
        if (_envEnthusiastButton != null) _envEnthusiastButton.Click += (s, e) => ApplyEnvironmentProfile("Enthusiast");

        if (_scaleComboBox != null) _scaleComboBox.SelectionChanged += (s, e) => ApplyAdvancedTuningFromUI();
        if (_cScaleComboBox != null) _cScaleComboBox.SelectionChanged += (s, e) => ApplyAdvancedTuningFromUI();
        if (_interpolationComboBox != null) _interpolationComboBox.SelectionChanged += (s, e) => ApplyAdvancedTuningFromUI();

        // Initial default environment setup
        ApplyEnvironmentProfile("Desktop");

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



        if (_queuePanel != null)
        {
            DragDrop.AddDragOverHandler(_queuePanel, (s, e) =>
            {
                e.Handled = true;
                e.DragEffects = DragDropEffects.Copy;
            });
            DragDrop.AddDropHandler(_queuePanel, OnQueuePanelFileDropped);
        }

        if (_queuePanel != null)
        {
            _queuePanel.AddHandler(InputElement.PointerPressedEvent, (object? sender, PointerPressedEventArgs e) => this.Focus(), RoutingStrategies.Tunnel);
        }
        if (_directoryPanel != null)
        {
            _directoryPanel.AddHandler(InputElement.PointerPressedEvent, (object? sender, PointerPressedEventArgs e) => this.Focus(), RoutingStrategies.Tunnel);
        }
        if (_enginePanel != null)
        {
            _enginePanel.AddHandler(InputElement.PointerPressedEvent, (object? sender, PointerPressedEventArgs e) => this.Focus(), RoutingStrategies.Tunnel);
        }

        UpdateQueuePlaceholder();
        SwitchTab(_defaultWorkspaceTab);
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

        if (_queueTabButton != null) ToggleClass(_queueTabButton, "active", tabName == "Queue");
        if (_directoryTabButton != null) ToggleClass(_directoryTabButton, "active", tabName == "Directory");
        if (_engineTabButton != null) ToggleClass(_engineTabButton, "active", tabName == "Engine");

        if (_queueTabIndicator != null) _queueTabIndicator.IsVisible = (tabName == "Queue");
        if (_directoryTabIndicator != null) _directoryTabIndicator.IsVisible = (tabName == "Directory");
        if (_engineTabIndicator != null) _engineTabIndicator.IsVisible = (tabName == "Engine");

        if (_queuePanel != null) _queuePanel.IsVisible = (tabName == "Queue");
        if (_directoryPanel != null) _directoryPanel.IsVisible = (tabName == "Directory");
        if (_enginePanel != null) _enginePanel.IsVisible = (tabName == "Engine");

        if (tabName == "Directory")
        {
            // Load default folder if not set
            if (string.IsNullOrEmpty(_currentDirectoryPath))
            {
                string targetDir = "";
                if (_rememberLastDirectoryPath && !string.IsNullOrEmpty(_lastDirectoryPath) && Directory.Exists(_lastDirectoryPath))
                {
                    targetDir = _lastDirectoryPath;
                }
                else if (!string.IsNullOrEmpty(_defaultBootDirectoryPath) && Directory.Exists(_defaultBootDirectoryPath))
                {
                    targetDir = _defaultBootDirectoryPath;
                }

                if (string.IsNullOrEmpty(targetDir))
                {
                    targetDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
                    {
                        targetDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    }
                    if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
                    {
                        targetDir = AppContext.BaseDirectory;
                    }
                }
                LoadDirectory(targetDir);
            }
        }
    }

    private void ToggleViewMode()
    {
        _isGridView = !_isGridView;
        if (_viewModeToggleIcon != null)
        {
            _viewModeToggleIcon.Text = _isGridView ? "☰" : "⊞";
        }
        if (_directoryListBox != null)
        {
            if (_isGridView)
            {
                _directoryListBox.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Avalonia.Controls.Panel?>(() => new Avalonia.Controls.WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal });
            }
            else
            {
                _directoryListBox.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Avalonia.Controls.Panel?>(() => new Avalonia.Controls.VirtualizingStackPanel());
            }
        }
        foreach (var item in _directoryItems)
        {
            item.IsGridView = _isGridView;
        }
    }

    private void RenderBreadcrumbs(string path)
    {
        if (_breadcrumbContainer == null) return;
        _breadcrumbContainer.Children.Clear();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var dir = new DirectoryInfo(path);
            var segments = new System.Collections.Generic.List<(string Name, string FullPath)>();
            var current = dir;
            while (current != null)
            {
                segments.Insert(0, (string.IsNullOrEmpty(current.Name) ? current.FullName : current.Name, current.FullName));
                current = current.Parent;
            }

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                string segPath = seg.FullPath;

                var btn = new Button
                {
                    Content = seg.Name,
                    Classes = { "page-btn" },
                    FontSize = 10,
                    Padding = new Avalonia.Thickness(6, 3),
                    CornerRadius = new Avalonia.CornerRadius(4)
                };
                btn.Click += (s, e) => LoadDirectory(segPath);
                _breadcrumbContainer.Children.Add(btn);

                if (i < segments.Count - 1)
                {
                    var sep = new TextBlock
                    {
                        Text = "/",
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#5C647C")),
                        FontSize = 10,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Margin = new Avalonia.Thickness(2, 0)
                    };
                    _breadcrumbContainer.Children.Add(sep);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error rendering breadcrumbs: {ex.Message}");
        }
    }

    private void LoadDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        _currentDirectoryPath = path;

        try
        {
            var dirInfo = new DirectoryInfo(path);
            var entries = dirInfo.GetFileSystemInfos().Where(e => e.Exists).ToList();

            var folders = entries.Where(e => (e.Attributes & FileAttributes.Directory) != 0).ToList();
            var files = entries.Where(e => (e.Attributes & FileAttributes.Directory) == 0 && IsSupportedMediaFile(e.Extension)).ToList();

            // Sort folders
            System.Collections.Generic.IEnumerable<System.IO.FileSystemInfo> sortedFolders;
            switch (_directorySortMode)
            {
                case 1: // Name (Descending)
                    sortedFolders = folders.OrderByDescending(e => e.Name);
                    break;
                case 2: // Date Modified (Newest First)
                    sortedFolders = folders.OrderByDescending(e => e.LastWriteTime);
                    break;
                case 3: // Date Modified (Oldest First)
                    sortedFolders = folders.OrderBy(e => e.LastWriteTime);
                    break;
                default: // Name (Ascending)
                    sortedFolders = folders.OrderBy(e => e.Name);
                    break;
            }

            // Sort files
            System.Collections.Generic.IEnumerable<System.IO.FileSystemInfo> sortedFiles;
            switch (_directorySortMode)
            {
                case 1: // Name (Descending)
                    sortedFiles = files.OrderByDescending(e => e.Name);
                    break;
                case 2: // Date Modified (Newest First)
                    sortedFiles = files.OrderByDescending(e => e.LastWriteTime);
                    break;
                case 3: // Date Modified (Oldest First)
                    sortedFiles = files.OrderBy(e => e.LastWriteTime);
                    break;
                case 4: // Size (Largest First)
                    sortedFiles = files.OrderByDescending(e => e is System.IO.FileInfo fi ? fi.Length : 0);
                    break;
                case 5: // Size (Smallest First)
                    sortedFiles = files.OrderBy(e => e is System.IO.FileInfo fi ? fi.Length : 0);
                    break;
                default: // Name (Ascending)
                    sortedFiles = files.OrderBy(e => e.Name);
                    break;
            }

            _allEntries = sortedFolders.Concat(sortedFiles).ToList();

            _directoryItems.Clear();
            foreach (var entry in _allEntries)
            {
                bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                bool isAudio = !isDir && IsAudioFile(entry.Extension);
                var item = new MediaItemViewModel
                {
                    Name = entry.Name,
                    FullPath = entry.FullName,
                    IsDirectory = isDir,
                    IsAudio = isAudio,
                    IsGridView = _isGridView,
                    SizeText = isDir ? "" : FormatFileSize(((FileInfo)entry).Length),
                    DurationText = isAudio ? "AUDIO" : "",
                    Thumbnail = null
                };
                _directoryItems.Add(item);
            }

            RenderBreadcrumbs(_currentDirectoryPath);

            // Trigger background lazy loader
            _lazyLoadCts?.Cancel();
            _lazyLoadCts = new System.Threading.CancellationTokenSource();
            var token = _lazyLoadCts.Token;
            Task.Run(() => LazyLoadPageMetadataAndThumbnailsAsync(_allEntries, token), token);
        }
        catch (Exception ex)
        {
            Log($"Error loading directory contents: {ex.Message}");
        }
    }

    private async Task LazyLoadPageMetadataAndThumbnailsAsync(System.Collections.Generic.List<FileSystemInfo> pageEntries, CancellationToken token)
    {
        if (_previewMpvHandle == IntPtr.Zero) return;

        foreach (var entry in pageEntries)
        {
            if (token.IsCancellationRequested) return;

            // Process media files only
            if ((entry.Attributes & FileAttributes.Directory) != 0 || !IsSupportedMediaFile(entry.Extension))
                continue;

            string filePath = entry.FullName;
            bool isAudioFile = IsAudioFile(entry.Extension);

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

            if (duration > 0)
            {
                string durationText = TimeSpan.FromSeconds(duration).ToString(@"hh\:mm\:ss");
                Dispatcher.UIThread.Post(() =>
                {
                    var target = _directoryItems.FirstOrDefault(x => x.FullPath == filePath);
                    if (target != null)
                    {
                        target.DurationText = durationText;
                    }
                });
            }

            // Skip thumbnail frame extraction for audio files
            if (isAudioFile) continue;

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
                if (IsSupportedMediaFile(Path.GetExtension(path)))
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
            foreach (var file in Directory.GetFiles(dirPath).Where(f => IsSupportedMediaFile(Path.GetExtension(f))).OrderBy(f => f))
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
        if (!File.Exists(filePath)) return;

        string name = Path.GetFileName(filePath);
        string durationText = "--:--";
        string sizeText = "";
        Avalonia.Media.Imaging.Bitmap? thumbnail = null;

        var existing = _directoryItems.FirstOrDefault(x => x.FullPath == filePath);
        if (existing != null)
        {
            if (!string.IsNullOrEmpty(existing.DurationText))
                durationText = existing.DurationText;
            if (!string.IsNullOrEmpty(existing.SizeText))
                sizeText = existing.SizeText;
            thumbnail = existing.Thumbnail;
        }

        // Compute file size if not available from directory items
        if (string.IsNullOrEmpty(sizeText))
        {
            try { sizeText = FormatFileSize(new FileInfo(filePath).Length); } catch { }
        }

        var item = new QueueItemViewModel
        {
            Name = name,
            FullPath = filePath,
            DurationText = durationText,
            SizeText = sizeText,
            Thumbnail = thumbnail
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
                            this.Focus();
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



    private void OnCurrentTimeTextPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is TextBlock textBlock)
        {
            _isEditingTime = true;
            WakeTransportHUD();

            if (textBlock.Name == "SingleCurrentTimeText" && _singleCurrentTimeInput != null)
            {
                textBlock.IsVisible = false;
                _singleCurrentTimeInput.IsVisible = true;
                _singleCurrentTimeInput.Text = textBlock.Text;
                _singleCurrentTimeInput.Focus();
                _singleCurrentTimeInput.SelectAll();
            }
            else if (textBlock.Name == "DeckCurrentTimeText" && _deckCurrentTimeInput != null)
            {
                textBlock.IsVisible = false;
                _deckCurrentTimeInput.IsVisible = true;
                _deckCurrentTimeInput.Text = textBlock.Text;
                _deckCurrentTimeInput.Focus();
                _deckCurrentTimeInput.SelectAll();
            }
        }
    }

    private void OnCurrentTimeInputKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                string input = textBox.Text ?? "";
                if (TryParseTime(input, out double targetSeconds) && targetSeconds >= 0 && targetSeconds <= _playbackDuration)
                {
                    textBox.ClearValue(TextBox.BorderBrushProperty);
                    Log($"Manual time seek command: seek {targetSeconds:F3} absolute");
                    SendCommand(_mpvHandle, "seek", targetSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute");
                    _lastSeekTime = DateTime.UtcNow;
                    HideTimeInput(textBox);
                }
                else
                {
                    textBox.BorderBrush = Avalonia.Media.Brush.Parse("#FF5555");
                    textBox.Focus();
                    textBox.SelectAll();
                }
                e.Handled = true;
            }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                HideTimeInput(textBox);
                e.Handled = true;
            }
        }
    }

    private void OnCurrentTimeInputLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            HideTimeInput(textBox);
        }
    }

    private void HideTimeInput(TextBox textBox)
    {
        _isEditingTime = false;
        textBox.ClearValue(TextBox.BorderBrushProperty);
        textBox.IsVisible = false;
        if (textBox.Name == "SingleCurrentTimeInput" && _currentTimeTexts.Length > 0)
        {
            var txt = _currentTimeTexts.FirstOrDefault(t => t.Name == "SingleCurrentTimeText");
            if (txt != null) txt.IsVisible = true;
        }
        else if (textBox.Name == "DeckCurrentTimeInput" && _currentTimeTexts.Length > 0)
        {
            var txt = _currentTimeTexts.FirstOrDefault(t => t.Name == "DeckCurrentTimeText");
            if (txt != null) txt.IsVisible = true;
        }
        WakeTransportHUD();
    }

    private bool TryParseTime(string input, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string[] parts = input.Trim().Split(':');
        if (parts.Length == 1)
        {
            if (double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
            {
                seconds = s;
                return true;
            }
            else if (double.TryParse(parts[0], out s))
            {
                seconds = s;
                return true;
            }
        }
        else if (parts.Length == 2)
        {
            if (double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double m) &&
                double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
            {
                seconds = m * 60 + s;
                return true;
            }
        }
        else if (parts.Length == 3)
        {
            if (double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h) &&
                double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double m) &&
                double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
            {
                seconds = h * 3600 + m * 60 + s;
                return true;
            }
        }

        return false;
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
        ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".ogv", ".ts", ".mts"
    };

    private static readonly System.Collections.Generic.HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".alac"
    };

    private bool IsVideoFile(string ext) => VideoExtensions.Contains(ext);
    private bool IsAudioFile(string ext) => AudioExtensions.Contains(ext);
    private bool IsSupportedMediaFile(string ext) => IsVideoFile(ext) || IsAudioFile(ext);

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
                this.Focus();
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

    private void TogglePip()
    {
        if (_isPipActive)
        {
            RedockFromPip();
        }
        else
        {
            HandoffToPip();
        }
    }

    private void HandoffToPip()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;
        if (!string.IsNullOrEmpty(_currentLoadedFilePath) && IsAudioFile(Path.GetExtension(_currentLoadedFilePath)))
        {
            Log("HandoffToPip: Suppressed for audio-only track.");
            return;
        }

        if (_pipWindow == null)
        {
            _pipWindow = new PipWindow();
            _pipWindow.RedockRequested += (s, e) => Dispatcher.UIThread.Post(() => RedockFromPip());
            _pipWindow.TogglePlayPauseRequested += (s, e) => Dispatcher.UIThread.Post(() => TogglePlayPause());
            _pipWindow.SkipBackwardRequested += (s, e) => Dispatcher.UIThread.Post(() =>
            {
                if (_mpvHandle != IntPtr.Zero) SendCommand(_mpvHandle, "seek", "-10", "relative");
            });
            _pipWindow.SkipForwardRequested += (s, e) => Dispatcher.UIThread.Post(() =>
            {
                if (_mpvHandle != IntPtr.Zero) SendCommand(_mpvHandle, "seek", "10", "relative");
            });
            _pipWindow.SeekRequested += (s, pos) =>
            {
                IntPtr localMpv = _mpvHandle;
                Task.Run(() =>
                {
                    if (localMpv != IntPtr.Zero)
                    {
                        SendCommand(localMpv, "seek", pos.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
                        Log($"HandoffToPip SeekRequested: Seeked to {pos} seconds");
                        PipLogger.Log($"HandoffToPip SeekRequested: Seeked to {pos} seconds");
                    }
                });
            };
            _pipWindow.SurfaceHandleReady = (childHwnd) =>
            {
                if (_mpvHandle != IntPtr.Zero && childHwnd != IntPtr.Zero)
                {
                    IntPtr localMpv = _mpvHandle;
                    Task.Run(() =>
                    {
                        if (localMpv != IntPtr.Zero)
                        {
                            SetMpvPropertyString(localMpv, "wid", childHwnd.ToString());
                            Log($"HandoffToPip: Re-parented mpv rendering target to child surface HWND {childHwnd}");
                            PipLogger.Log($"HandoffToPip Callback: Re-parented mpv rendering target to child surface HWND {childHwnd}");
                        }
                    });
                }
            };
        }

        double aspect = GetMpvPropertyDouble("video-params/aspect");
        if (double.IsNaN(aspect) || double.IsInfinity(aspect) || aspect <= 0)
        {
            aspect = 16.0 / 9.0;
        }
        _pipWindow.AspectRatio = aspect;

        string fileTitle = Path.GetFileName(_currentLoadedFilePath ?? "BlueJay Media Stream");
        _pipWindow.UpdatePlayState(!_isPaused);
        _pipWindow.UpdateTime(_playbackPosition, _playbackDuration, fileTitle);

        _pipWindow.Show();

        // Unconditional handoff for subsequent plays (cached window reuse)
        if (_pipWindow.ChildSurfaceHandle != IntPtr.Zero)
        {
            IntPtr localMpv = _mpvHandle;
            IntPtr localSurface = _pipWindow.ChildSurfaceHandle;
            Task.Run(() =>
            {
                if (localMpv != IntPtr.Zero && localSurface != IntPtr.Zero)
                {
                    SetMpvPropertyString(localMpv, "wid", localSurface.ToString());
                    Log($"HandoffToPip (Unconditional): Re-parented mpv rendering target to cached surface HWND {localSurface}");
                    PipLogger.Log($"HandoffToPip Unconditional: Re-parented mpv rendering target to cached surface HWND {localSurface}");
                }
            });
        }

        if (_videoSurface != null) _videoSurface.IsVisible = false;
        if (_pipActivePlaceholder != null) _pipActivePlaceholder.IsVisible = true;
        _isPipActive = true;
    }

    private void RedockFromPip()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        if (_cachedHwnd != IntPtr.Zero)
        {
            SetMpvPropertyString(_mpvHandle, "wid", _cachedHwnd.ToString());
            Log($"RedockFromPip: Successfully re-parented video rendering surface back to main HWND {_cachedHwnd}");
        }

        if (_pipActivePlaceholder != null) _pipActivePlaceholder.IsVisible = false;
        if (_videoSurface != null) _videoSurface.IsVisible = true;
        _isPipActive = false;

        if (_pipWindow != null)
        {
            _pipWindow.Hide();
        }
        this.Focus();
    }

    private void PlayPreviousInQueue()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_playbackQueueItems.Count > 0 && _currentQueueIndex > 0)
            {
                PlayQueueItem(_currentQueueIndex - 1);
            }
            else if (_playbackQueueItems.Count > 0)
            {
                PlayQueueItem(0);
            }
        });
    }

    private void StopPlayback()
    {
        if (_mpvHandle != IntPtr.Zero && _isEngineInitialized)
        {
            SendCommand(_mpvHandle, "stop");
        }
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

    // --- Video Engine Matrix Applicators & Helpers ---
    private void ApplyHdrConfig(bool enable)
    {
        _manualHdrActive = enable;
        _isHdrActive = enable;
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) 
        {
            UpdateHdrUI();
            return;
        }
        
        Log($"Applying HDR configuration -> Active: {enable}");

        // Save play state and pause the player temporarily if playing (Pause-Guard)
        bool wasPlaying = !_isPaused;
        if (wasPlaying)
        {
            SetMpvPropertyString(_mpvHandle, "pause", "yes");
        }

        if (enable)
        {
            SetMpvPropertyString(_mpvHandle, "vo", "gpu-next");
            SetMpvPropertyString(_mpvHandle, "gpu-api", "d3d11");
            SetMpvPropertyString(_mpvHandle, "d3d11-output-csp", "pq");
            SetMpvPropertyString(_mpvHandle, "target-colorspace-hint", "yes");
            SetMpvPropertyString(_mpvHandle, "hdr-compute-peak", _disableHdrPeak ? "no" : "yes");
        }
        else
        {
            // Standard fallback
            SetMpvPropertyString(_mpvHandle, "d3d11-output-csp", "srgb");
            SetMpvPropertyString(_mpvHandle, "target-colorspace-hint", "no");
            SetMpvPropertyString(_mpvHandle, "hdr-compute-peak", "no");
        }

        // Resume playback if it was playing previously
        if (wasPlaying)
        {
            SetMpvPropertyString(_mpvHandle, "pause", "no");
        }

        UpdateHdrUI();
    }

    private void ApplyDebandConfig(string mode)
    {
        _manualDebandMode = mode;
        _currentDebandMode = mode;
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized)
        {
            UpdateDebandUI();
            return;
        }

        Log($"Applying Deband configuration -> Mode: {mode}");
        switch (mode)
        {
            case "Disabled":
            case "Off":
                SetMpvPropertyString(_mpvHandle, "deband", "no");
                break;
            case "Low":
                SetMpvPropertyString(_mpvHandle, "deband", "yes");
                SetMpvPropertyString(_mpvHandle, "deband-iterations", "1");
                SetMpvPropertyString(_mpvHandle, "deband-threshold", "32");
                break;
            case "Reference":
                SetMpvPropertyString(_mpvHandle, "deband", "yes");
                SetMpvPropertyString(_mpvHandle, "deband-iterations", "3");
                SetMpvPropertyString(_mpvHandle, "deband-threshold", "48");
                SetMpvPropertyString(_mpvHandle, "deband-range", "16");
                SetMpvPropertyString(_mpvHandle, "deband-grain", "32");
                break;
        }
        
        UpdateDebandUI();
    }

    private void ApplyScalingConfig(bool highFidelity)
    {
        _manualCinematicScaling = highFidelity;
        _isCinematicScaling = highFidelity;
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized)
        {
            UpdateScalingUI();
            return;
        }

        Log($"Applying Scaling configuration -> HighFidelity (Cinematic): {highFidelity}");
        if (highFidelity)
        {
            SetMpvPropertyString(_mpvHandle, "scale", "ewa_lanczos");
            SetMpvPropertyString(_mpvHandle, "cscale", "ewa_lanczos");
            SetMpvPropertyString(_mpvHandle, "dscale", "mitchell");
            SetMpvPropertyString(_mpvHandle, "scale-antiring", "0.7");
            SetMpvPropertyString(_mpvHandle, "cscale-antiring", "0.7");
            SetMpvPropertyString(_mpvHandle, "sigmoid-upscaling", "yes");
        }
        else
        {
            SetMpvPropertyString(_mpvHandle, "scale", "mitchell");
            SetMpvPropertyString(_mpvHandle, "cscale", "mitchell");
            SetMpvPropertyString(_mpvHandle, "scale-antiring", "0.0");
            SetMpvPropertyString(_mpvHandle, "cscale-antiring", "0.0");
            SetMpvPropertyString(_mpvHandle, "sigmoid-upscaling", "no");
        }
        
        UpdateScalingUI();
    }

    private void ApplyToneMappingConfig(string algorithm)
    {
        _manualToneMapping = algorithm;
        _currentToneMapping = algorithm;
        
        if (_toneMappingSelectedText != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_toneMappingSelectedText != null)
                {
                    _toneMappingSelectedText.Text = algorithm;
                }
            });
        }

        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        Log($"Applying Tone-Mapping configuration -> Algorithm: {algorithm}");
        SetMpvPropertyString(_mpvHandle, "tone-mapping", algorithm);
    }

    private void UpdateHdrUI()
    {
        Dispatcher.UIThread.Post(() =>
        {
            bool enable = _isHdrActive;
            // Indicators
            if (_hdrSdrIndicator != null) _hdrSdrIndicator.IsVisible = !enable;
            if (_hdrActiveIndicator != null) _hdrActiveIndicator.IsVisible = enable;
            
            // Buttons active classes
            if (_hdrSdrButton != null) ToggleClass(_hdrSdrButton, "active", !enable);
            if (_hdrActiveButton != null) ToggleClass(_hdrActiveButton, "active", enable);
            
            // HUD Fullscreen/HDR Integration
            if (_singleFullscreenButton != null) ToggleClass(_singleFullscreenButton, "hdr-active", enable);
            if (_deckFullscreenButton != null) ToggleClass(_deckFullscreenButton, "hdr-active", enable);
            
            if (_singleFullscreenIcon != null)
            {
                if (enable) _singleFullscreenIcon.Foreground = SolidColorBrush.Parse("#66AFFF");
                else _singleFullscreenIcon.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty);
            }
            if (_deckFullscreenIcon != null)
            {
                if (enable) _deckFullscreenIcon.Foreground = SolidColorBrush.Parse("#66AFFF");
                else _deckFullscreenIcon.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty);
            }

            // Update sub-text dynamically
            if (_renderingProfileSubText != null)
            {
                _renderingProfileSubText.Text = enable ? "[gpu-next // pq 10-bit]" : "[d3d11 // srgb]";
            }
        });
    }
    
    private void UpdateDebandUI()
    {
        Dispatcher.UIThread.Post(() =>
        {
            string mode = _currentDebandMode;
            if (_debandOffIndicator != null) _debandOffIndicator.IsVisible = (mode == "Off" || mode == "Disabled");
            if (_debandLowIndicator != null) _debandLowIndicator.IsVisible = (mode == "Low");
            if (_debandRefIndicator != null) _debandRefIndicator.IsVisible = (mode == "Reference");
            
            if (_debandOffButton != null) ToggleClass(_debandOffButton, "active", (mode == "Off" || mode == "Disabled"));
            if (_debandLowButton != null) ToggleClass(_debandLowButton, "active", (mode == "Low"));
            if (_debandRefButton != null) ToggleClass(_debandRefButton, "active", (mode == "Reference"));

            // Update sub-text dynamically
            if (_gradientSmoothingSubText != null)
            {
                if (mode == "Low")
                    _gradientSmoothingSubText.Text = "1-pass balanced filter";
                else if (mode == "Reference")
                    _gradientSmoothingSubText.Text = "3-pass reference filter";
                else
                    _gradientSmoothingSubText.Text = "Banding filter disabled";
            }
        });
    }
    
    private void UpdateScalingUI()
    {
        Dispatcher.UIThread.Post(() =>
        {
            bool isCinematic = _isCinematicScaling;
            if (_scalePerfIndicator != null) _scalePerfIndicator.IsVisible = !isCinematic;
            if (_scaleCinematicIndicator != null) _scaleCinematicIndicator.IsVisible = isCinematic;
            
            if (_scalePerfButton != null) ToggleClass(_scalePerfButton, "active", !isCinematic);
            if (_scaleCinematicButton != null) ToggleClass(_scaleCinematicButton, "active", isCinematic);

            // Update sub-text dynamically
            if (_imageSharpnessSubText != null)
            {
                _imageSharpnessSubText.Text = isCinematic ? "Cinematic EWA Lanczos" : "Fast Mitchell scaler";
            }
        });
    }
    
    private void ToggleClass(Button button, string className, bool add)
    {
        if (add)
        {
            if (!button.Classes.Contains(className))
                button.Classes.Add(className);
        }
        else
        {
            button.Classes.Remove(className);
        }
    }

    private void ApplyEnvironmentProfile(string profile)
    {
        Log($"> ApplyEnvironmentProfile: {profile}");
        _isUpdatingProfileProgrammatically = true;

        try
        {
            // Toggle active classes on Eco/Desktop/Enthusiast buttons
            if (_envEcoButton != null) ToggleClass(_envEcoButton, "active", profile == "Eco");
            if (_envDesktopButton != null) ToggleClass(_envDesktopButton, "active", profile == "Desktop");
            if (_envEnthusiastButton != null) ToggleClass(_envEnthusiastButton, "active", profile == "Enthusiast");

            // Update indicators visibility
            if (_envEcoIndicator != null) _envEcoIndicator.IsVisible = (profile == "Eco");
            if (_envDesktopIndicator != null) _envDesktopIndicator.IsVisible = (profile == "Desktop");
            if (_envEnthusiastIndicator != null) _envEnthusiastIndicator.IsVisible = (profile == "Enthusiast");

            // Update ComboBox selections based on profile
            int scaleIdx = 1; // Default Spline36
            int cscaleIdx = 1; // Default Spline36
            int interpolationIdx = 0; // Default Off

            if (profile == "Eco")
            {
                scaleIdx = 0; // Mitchell
                cscaleIdx = 0; // Mitchell
                interpolationIdx = 0; // Off
                if (_envProfileSubText != null) _envProfileSubText.Text = "Eco Balance";
            }
            else if (profile == "Desktop")
            {
                scaleIdx = 1; // Spline36
                cscaleIdx = 1; // Spline36
                interpolationIdx = 0; // Off
                if (_envProfileSubText != null) _envProfileSubText.Text = "Standard Balance";
            }
            else if (profile == "Enthusiast")
            {
                scaleIdx = 2; // EWA Lanczos
                cscaleIdx = 2; // EWA Lanczos
                interpolationIdx = 1; // On
                if (_envProfileSubText != null) _envProfileSubText.Text = "High Fidelity Ultra";
            }

            if (_scaleComboBox != null) _scaleComboBox.SelectedIndex = scaleIdx;
            if (_cScaleComboBox != null) _cScaleComboBox.SelectedIndex = cscaleIdx;
            if (_interpolationComboBox != null) _interpolationComboBox.SelectedIndex = interpolationIdx;

            ApplyAdvancedTuningToMpv();
        }
        finally
        {
            _isUpdatingProfileProgrammatically = false;
        }
    }

    private void ApplyAdvancedTuningFromUI()
    {
        if (_isUpdatingProfileProgrammatically) return;

        Log("> ApplyAdvancedTuningFromUI: User modified ComboBox values manually.");

        // Clear Environment Profile active classes
        if (_envEcoButton != null) ToggleClass(_envEcoButton, "active", false);
        if (_envDesktopButton != null) ToggleClass(_envDesktopButton, "active", false);
        if (_envEnthusiastButton != null) ToggleClass(_envEnthusiastButton, "active", false);

        // Clear environment indicators
        if (_envEcoIndicator != null) _envEcoIndicator.IsVisible = false;
        if (_envDesktopIndicator != null) _envDesktopIndicator.IsVisible = false;
        if (_envEnthusiastIndicator != null) _envEnthusiastIndicator.IsVisible = false;

        if (_envProfileSubText != null) _envProfileSubText.Text = "Custom Profile";

        ApplyAdvancedTuningToMpv();
    }

    private void ApplyAdvancedTuningToMpv()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;

        int scaleIdx = _scaleComboBox != null ? _scaleComboBox.SelectedIndex : 1;
        int cscaleIdx = _cScaleComboBox != null ? _cScaleComboBox.SelectedIndex : 1;
        int interpolationIdx = _interpolationComboBox != null ? _interpolationComboBox.SelectedIndex : 0;

        Log($"Applying Advanced Tuning to MPV -> Scale index: {scaleIdx}, CScale index: {cscaleIdx}, Interpolation: {interpolationIdx}");

        // Map Scale index
        string scaleVal = "spline36";
        if (scaleIdx == 0) scaleVal = "mitchell";
        else if (scaleIdx == 2) scaleVal = "ewa_lanczos";

        // Map CScale index
        string cscaleVal = "spline36";
        if (cscaleIdx == 0) cscaleVal = "mitchell";
        else if (cscaleIdx == 2) cscaleVal = "ewa_lanczos";

        // Apply scale, cscale, and dscale/sigmoid upscaling fallback
        SetMpvPropertyString(_mpvHandle, "scale", scaleVal);
        SetMpvPropertyString(_mpvHandle, "cscale", cscaleVal);
        
        if (scaleVal == "ewa_lanczos")
        {
            SetMpvPropertyString(_mpvHandle, "dscale", "mitchell");
            SetMpvPropertyString(_mpvHandle, "scale-antiring", "0.7");
            SetMpvPropertyString(_mpvHandle, "sigmoid-upscaling", "yes");
        }
        else
        {
            SetMpvPropertyString(_mpvHandle, "scale-antiring", "0.0");
            SetMpvPropertyString(_mpvHandle, "sigmoid-upscaling", "no");
        }

        if (cscaleVal == "ewa_lanczos")
        {
            SetMpvPropertyString(_mpvHandle, "cscale-antiring", "0.7");
        }
        else
        {
            SetMpvPropertyString(_mpvHandle, "cscale-antiring", "0.0");
        }

        // Map and apply Interpolation index
        if (interpolationIdx == 1)
        {
            SetMpvPropertyString(_mpvHandle, "interpolation", "yes");
            SetMpvPropertyString(_mpvHandle, "video-sync", "display-resample");
        }
        else
        {
            SetMpvPropertyString(_mpvHandle, "interpolation", "no");
            SetMpvPropertyString(_mpvHandle, "video-sync", "audio");
        }
    }


    // --- Phase 2: MANUAL MONITOR PROFILE OVERRIDES & UTILITIES ---
    private string FormatBreadcrumbPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ❯ ", parts.Select(p => p.ToUpperInvariant()));
    }


    private void LoadSettings()
    {
        try
        {
            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            if (File.Exists(settingsPath))
            {
                string json = File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<UserSettings>(json);
                if (settings != null)
                {
                    _isTacticalDeckMode = settings.IsTacticalDeckMode;
                    _showSystemClock = settings.ShowSystemClock;
                    _use24HourClock = settings.Use24HourClock;
                    _defaultBootDirectoryPath = settings.DefaultBootDirectoryPath ?? "";
                    _rememberLastDirectoryPath = settings.RememberLastDirectoryPath;
                    _lastDirectoryPath = settings.LastDirectoryPath ?? "";
                    _allowMultipleInstances = settings.AllowMultipleInstances;
                    _disableHdrPeak = settings.DisableHdrPeak;
                    _forceSoftwareDecoding = settings.ForceSoftwareDecoding;
                    _defaultAudioLanguage = settings.DefaultAudioLanguage ?? "eng";
                    _passthroughAc3 = settings.PassthroughAc3;
                    _passthroughDts = settings.PassthroughDts;
                    _passthroughDtsHd = settings.PassthroughDtsHd;
                    _passthroughTrueHd = settings.PassthroughTrueHd;
                    _wasapiExclusive = settings.WasapiExclusive;
                    _subtitleFontFamily = settings.SubtitleFontFamily ?? "";
                    _subtitleBorderSize = settings.SubtitleBorderSize;
                    _subtitleShadowOffset = settings.SubtitleShadowOffset;
                    _defaultWorkspaceTab = settings.DefaultWorkspaceTab ?? "Queue";
                    _disableOsdNotifications = settings.DisableOsdNotifications;
                    _disableSeekingPreviews = settings.DisableSeekingPreviews;
                    _directorySortMode = settings.DirectorySortMode;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading settings: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new UserSettings
            {
                IsTacticalDeckMode = _isTacticalDeckMode,
                ShowSystemClock = _showSystemClock,
                Use24HourClock = _use24HourClock,
                DefaultBootDirectoryPath = _defaultBootDirectoryPath,
                RememberLastDirectoryPath = _rememberLastDirectoryPath,
                LastDirectoryPath = _rememberLastDirectoryPath ? _currentDirectoryPath : "",
                AllowMultipleInstances = _allowMultipleInstances,
                DisableHdrPeak = _disableHdrPeak,
                ForceSoftwareDecoding = _forceSoftwareDecoding,
                DefaultAudioLanguage = _defaultAudioLanguage,
                PassthroughAc3 = _passthroughAc3,
                PassthroughDts = _passthroughDts,
                PassthroughDtsHd = _passthroughDtsHd,
                PassthroughTrueHd = _passthroughTrueHd,
                WasapiExclusive = _wasapiExclusive,
                SubtitleFontFamily = _subtitleFontFamily,
                SubtitleBorderSize = _subtitleBorderSize,
                SubtitleShadowOffset = _subtitleShadowOffset,
                DefaultWorkspaceTab = _defaultWorkspaceTab,
                DisableOsdNotifications = _disableOsdNotifications,
                DisableSeekingPreviews = _disableSeekingPreviews,
                DirectorySortMode = _directorySortMode
            };
            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json);
        }
        catch (Exception ex)
        {
            Log($"Error saving settings: {ex.Message}");
        }
    }

    private void ApplyAudioSettings()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;
        Log($"Applying custom settings -> alang={_defaultAudioLanguage}, audio-exclusive={_wasapiExclusive}");
        SetMpvOptionString(_mpvHandle, "alang", _defaultAudioLanguage ?? "eng");
        SetMpvOptionString(_mpvHandle, "audio-exclusive", _wasapiExclusive ? "yes" : "no");
        
        var spdif = new System.Collections.Generic.List<string>();
        if (_passthroughAc3) spdif.Add("ac3");
        if (_passthroughDts) spdif.Add("dts");
        if (_passthroughDtsHd) spdif.Add("dts-hd");
        if (_passthroughTrueHd) spdif.Add("truehd");
        string spdifStr = spdif.Count > 0 ? string.Join(",", spdif) : "no";
        SetMpvOptionString(_mpvHandle, "audio-spdif", spdifStr);
    }

    private void ApplyTypographySettings()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;
        Log($"Applying custom settings -> sub-border-size={_subtitleBorderSize}, sub-shadow-offset={_subtitleShadowOffset}");
        if (!string.IsNullOrEmpty(_subtitleFontFamily))
        {
            SetMpvPropertyString(_mpvHandle, "sub-font", _subtitleFontFamily);
        }
        SetMpvPropertyDouble(_mpvHandle, "sub-border-size", _subtitleBorderSize);
        SetMpvPropertyDouble(_mpvHandle, "sub-shadow-offset", _subtitleShadowOffset);
    }

    private void ApplyDecodingSettings()
    {
        if (_mpvHandle == IntPtr.Zero || !_isEngineInitialized) return;
        Log($"Applying custom settings -> hwdec={(_forceSoftwareDecoding ? "no" : "auto")}");
        SetMpvPropertyString(_mpvHandle, "hwdec", _forceSoftwareDecoding ? "no" : "auto");
    }

    private void OpenSettings()
    {
        if (_masterSettingsOverlay == null) return;
        
        // Populate settings elements from fields
        if (_defaultBootDirTextBox != null) _defaultBootDirTextBox.Text = _defaultBootDirectoryPath;
        if (_rememberLastDirToggle != null) _rememberLastDirToggle.IsChecked = _rememberLastDirectoryPath;
        if (_allowMultipleInstancesToggle != null) _allowMultipleInstancesToggle.IsChecked = _allowMultipleInstances;
        if (_disableHdrPeakToggle != null) _disableHdrPeakToggle.IsChecked = _disableHdrPeak;
        if (_hardwareAccelerationToggle != null) _hardwareAccelerationToggle.IsChecked = !_forceSoftwareDecoding;
        if (_defaultAudioLangTextBox != null) _defaultAudioLangTextBox.Text = _defaultAudioLanguage;
        
        if (_passthroughAc3CheckBox != null) _passthroughAc3CheckBox.IsChecked = _passthroughAc3;
        if (_passthroughDtsCheckBox != null) _passthroughDtsCheckBox.IsChecked = _passthroughDts;
        if (_passthroughDtsHdCheckBox != null) _passthroughDtsHdCheckBox.IsChecked = _passthroughDtsHd;
        if (_passthroughTrueHdCheckBox != null) _passthroughTrueHdCheckBox.IsChecked = _passthroughTrueHd;
        if (_wasapiExclusiveToggle != null) _wasapiExclusiveToggle.IsChecked = _wasapiExclusive;
        
        if (_subtitleFontComboBox != null) _subtitleFontComboBox.SelectedItem = _subtitleFontFamily;
        if (_subBorderSizeSlider != null) _subBorderSizeSlider.Value = _subtitleBorderSize;
        if (_subShadowOffsetSlider != null) _subShadowOffsetSlider.Value = _subtitleShadowOffset;
        
        if (_defaultWorkspaceTabComboBox != null)
        {
            _defaultWorkspaceTabComboBox.SelectedIndex = _defaultWorkspaceTab switch
            {
                "Queue" => 0,
                "Directory" => 1,
                "Engine" => 2,
                _ => 0
            };
        }
        
        if (_showOsdNotificationsToggle != null) _showOsdNotificationsToggle.IsChecked = !_disableOsdNotifications;
        if (_disableSeekingPreviewsToggle != null) _disableSeekingPreviewsToggle.IsChecked = _disableSeekingPreviews;
        if (_tacticalDeckToggle != null) _tacticalDeckToggle.IsChecked = _isTacticalDeckMode;
        if (_showSystemClockToggle != null) _showSystemClockToggle.IsChecked = _showSystemClock;
        if (_clockFormatComboBox != null) _clockFormatComboBox.SelectedIndex = _use24HourClock ? 0 : 1;
        if (_clockFormatCard != null) _clockFormatCard.IsVisible = _showSystemClock;

        if (_videoSurface != null) _videoSurface.IsVisible = false;
        _masterSettingsOverlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => _masterSettingsOverlay.Opacity = 1.0);
        Log("Fullscreen Settings Overlay opened.");
    }

    private async void CloseSettings()
    {
        if (_masterSettingsOverlay == null) return;

        // Retrieve settings elements to fields
        if (_defaultBootDirTextBox != null) _defaultBootDirectoryPath = _defaultBootDirTextBox.Text ?? "";
        if (_rememberLastDirToggle != null) _rememberLastDirectoryPath = _rememberLastDirToggle.IsChecked ?? true;
        if (_allowMultipleInstancesToggle != null) _allowMultipleInstances = _allowMultipleInstancesToggle.IsChecked ?? false;
        
        if (_disableHdrPeakToggle != null) _disableHdrPeak = _disableHdrPeakToggle.IsChecked ?? false;
        if (_hardwareAccelerationToggle != null) _forceSoftwareDecoding = !(_hardwareAccelerationToggle.IsChecked ?? true);
        if (_defaultAudioLangTextBox != null) _defaultAudioLanguage = _defaultAudioLangTextBox.Text ?? "eng";
        
        if (_passthroughAc3CheckBox != null) _passthroughAc3 = _passthroughAc3CheckBox.IsChecked ?? false;
        if (_passthroughDtsCheckBox != null) _passthroughDts = _passthroughDtsCheckBox.IsChecked ?? false;
        if (_passthroughDtsHdCheckBox != null) _passthroughDtsHd = _passthroughDtsHdCheckBox.IsChecked ?? false;
        if (_passthroughTrueHdCheckBox != null) _passthroughTrueHd = _passthroughTrueHdCheckBox.IsChecked ?? false;
        if (_wasapiExclusiveToggle != null) _wasapiExclusive = _wasapiExclusiveToggle.IsChecked ?? false;
        
        if (_subtitleFontComboBox != null) _subtitleFontFamily = _subtitleFontComboBox.SelectedItem as string ?? "";
        if (_subBorderSizeSlider != null) _subtitleBorderSize = _subBorderSizeSlider.Value;
        if (_subShadowOffsetSlider != null) _subtitleShadowOffset = _subShadowOffsetSlider.Value;
        
        if (_defaultWorkspaceTabComboBox != null)
        {
            _defaultWorkspaceTab = _defaultWorkspaceTabComboBox.SelectedIndex switch
            {
                0 => "Queue",
                1 => "Directory",
                2 => "Engine",
                _ => "Queue"
            };
        }
        
        if (_showOsdNotificationsToggle != null) _disableOsdNotifications = !(_showOsdNotificationsToggle.IsChecked ?? true);
        if (_disableSeekingPreviewsToggle != null) _disableSeekingPreviews = _disableSeekingPreviewsToggle.IsChecked ?? false;

        bool oldMode = _isTacticalDeckMode;
        if (_tacticalDeckToggle != null) _isTacticalDeckMode = _tacticalDeckToggle.IsChecked ?? false;
        if (_isTacticalDeckMode != oldMode)
        {
            ApplyLayoutMode();
        }

        bool oldShowClock = _showSystemClock;
        bool old24Hour = _use24HourClock;
        if (_showSystemClockToggle != null) _showSystemClock = _showSystemClockToggle.IsChecked ?? false;
        if (_clockFormatComboBox != null) _use24HourClock = _clockFormatComboBox.SelectedIndex == 0;
        if (_showSystemClock != oldShowClock || _use24HourClock != old24Hour)
        {
            if (_showSystemClock)
            {
                UpdateSystemClockText();
                _systemClockTimer?.Start();
            }
            else
            {
                SetSystemClockVisibility(false);
                _systemClockTimer?.Stop();
            }
        }

        // Apply dynamic parameters to running media engine instance
        ApplyAudioSettings();
        ApplyTypographySettings();
        ApplyDecodingSettings();



        SaveSettings();

        _masterSettingsOverlay.Opacity = 0.0;
        await Task.Delay(250);
        _masterSettingsOverlay.IsVisible = false;
        if (_videoSurface != null) _videoSurface.IsVisible = true;
        WakeTransportHUD();
        Log("Fullscreen Settings Overlay closed and properties synchronized.");
    }

    private void PopulateSubtitleFonts()
    {
        if (_subtitleFontComboBox == null) return;
        try
        {
            var fontFamilies = Avalonia.Media.FontManager.Current.SystemFonts.Select(f => f.Name).OrderBy(n => n).ToList();
            _subtitleFontComboBox.ItemsSource = fontFamilies;
        }
        catch (Exception ex)
        {
            Log($"Error loading system fonts: {ex.Message}");
        }
    }

    private void SetupSettingsPanel()
    {
        _masterSettingsOverlay = this.FindControl<Grid>("MasterSettingsOverlay");
        _settingsCloseButton = this.FindControl<Button>("SettingsCloseButton");
        _settingsCarousel = this.FindControl<Carousel>("SettingsCarousel");
        
        _navGeneralButton = this.FindControl<Button>("NavGeneralButton");
        _navEngineButton = this.FindControl<Button>("NavEngineButton");
        _navAudioButton = this.FindControl<Button>("NavAudioButton");
        _navSubtitlesButton = this.FindControl<Button>("NavSubtitlesButton");
        _navUiButton = this.FindControl<Button>("NavUiButton");

        _defaultBootDirTextBox = this.FindControl<TextBox>("DefaultBootDirTextBox");
        _browseBootDirButton = this.FindControl<Button>("BrowseBootDirButton");
        _rememberLastDirToggle = this.FindControl<ToggleSwitch>("RememberLastDirToggle");
        _allowMultipleInstancesToggle = this.FindControl<ToggleSwitch>("AllowMultipleInstancesToggle");
        
        _disableHdrPeakToggle = this.FindControl<ToggleSwitch>("DisableHdrPeakToggle");
        _hardwareAccelerationToggle = this.FindControl<ToggleSwitch>("HardwareAccelerationToggle");
        
        _defaultAudioLangTextBox = this.FindControl<TextBox>("DefaultAudioLangTextBox");
        _passthroughAc3CheckBox = this.FindControl<CheckBox>("PassthroughAc3CheckBox");
        _passthroughDtsCheckBox = this.FindControl<CheckBox>("PassthroughDtsCheckBox");
        _passthroughDtsHdCheckBox = this.FindControl<CheckBox>("PassthroughDtsHdCheckBox");
        _passthroughTrueHdCheckBox = this.FindControl<CheckBox>("PassthroughTrueHdCheckBox");
        _wasapiExclusiveToggle = this.FindControl<ToggleSwitch>("WasapiExclusiveToggle");
        
        _subtitleFontComboBox = this.FindControl<ComboBox>("SubtitleFontComboBox");
        _subBorderSizeSlider = this.FindControl<Slider>("SubBorderSizeSlider");
        _subBorderSizeValueText = this.FindControl<TextBlock>("SubBorderSizeValueText");
        _subShadowOffsetSlider = this.FindControl<Slider>("SubShadowOffsetSlider");
        _subShadowOffsetValueText = this.FindControl<TextBlock>("SubShadowOffsetValueText");
        
        _defaultWorkspaceTabComboBox = this.FindControl<ComboBox>("DefaultWorkspaceTabComboBox");
        _showOsdNotificationsToggle = this.FindControl<ToggleSwitch>("ShowOsdNotificationsToggle");
        _disableSeekingPreviewsToggle = this.FindControl<ToggleSwitch>("DisableSeekingPreviewsToggle");
        _tacticalDeckToggle = this.FindControl<ToggleSwitch>("TacticalDeckToggle");
        _showSystemClockToggle = this.FindControl<ToggleSwitch>("ShowSystemClockToggle");
        _clockFormatComboBox = this.FindControl<ComboBox>("ClockFormatComboBox");
        _clockFormatCard = this.FindControl<Border>("ClockFormatCard");
        
        // Settings close button click
        if (_settingsCloseButton != null)
        {
            _settingsCloseButton.Click += (s, e) => CloseSettings();
        }

        // Browse boot directory picker click
        if (_browseBootDirButton != null)
        {
            _browseBootDirButton.Click += async (s, e) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = "Select Default Boot Directory",
                        AllowMultiple = false
                    });
                    if (folders != null && folders.Count > 0)
                    {
                        var path = folders[0].Path.LocalPath;
                        if (!string.IsNullOrEmpty(path) && _defaultBootDirTextBox != null)
                        {
                            _defaultBootDirTextBox.Text = path;
                        }
                    }
                }
            };
        }

        // Dynamic System Clock Format card visibility toggle
        if (_showSystemClockToggle != null)
        {
            _showSystemClockToggle.IsCheckedChanged += (s, e) =>
            {
                if (_clockFormatCard != null)
                {
                    _clockFormatCard.IsVisible = _showSystemClockToggle.IsChecked ?? false;
                }
            };
        }

        // Wire up nav clicks to Carousel page switching
        var navButtons = new[] { _navGeneralButton, _navEngineButton, _navAudioButton, _navSubtitlesButton, _navUiButton };

        for (int i = 0; i < navButtons.Length; i++)
        {
            var btn = navButtons[i];
            if (btn == null) continue;
            
            int pageIndex = i;
            btn.Click += (s, e) =>
            {
                foreach (var b in navButtons)
                {
                    if (b != null) ToggleClass(b, "active", b == btn);
                }
                
                if (_settingsCarousel != null)
                {
                    _settingsCarousel.SelectedIndex = pageIndex;
                }
            };
        }

        // Sliders updates
        if (_subBorderSizeSlider != null)
        {
            _subBorderSizeSlider.ValueChanged += (s, e) =>
            {
                if (_subBorderSizeValueText != null)
                {
                    _subBorderSizeValueText.Text = $"{e.NewValue:F1} px";
                }
            };
        }

        if (_subShadowOffsetSlider != null)
        {
            _subShadowOffsetSlider.ValueChanged += (s, e) =>
            {
                if (_subShadowOffsetValueText != null)
                {
                    _subShadowOffsetValueText.Text = $"{e.NewValue:F1} px";
                }
            };
        }

        // Settings gear buttons in main views (TitleBar)
        var titlebarSettings = this.FindControl<Button>("SettingsButton");
        if (titlebarSettings != null)
        {
            titlebarSettings.Click += (s, e) => OpenSettings();
        }

        PopulateSubtitleFonts();
    }

    private void LogTelemetry(string message)
    {
        Log($"Telemetry Monitor: {message}");
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
        set { _isDirectory = value; OnPropertyChanged(nameof(IsDirectory)); OnPropertyChanged(nameof(IsVideo)); }
    }

    private bool _isAudio;
    public bool IsAudio
    {
        get => _isAudio;
        set { _isAudio = value; OnPropertyChanged(nameof(IsAudio)); OnPropertyChanged(nameof(IsVideo)); }
    }

    private bool _isGridView;
    public bool IsGridView
    {
        get => _isGridView;
        set { _isGridView = value; OnPropertyChanged(nameof(IsGridView)); }
    }

    public bool IsVideo => !IsDirectory && !IsAudio;

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

    private string _sizeText = "";
    public string SizeText
    {
        get => _sizeText;
        set { _sizeText = value; OnPropertyChanged(nameof(SizeText)); }
    }

    private Avalonia.Media.Imaging.Bitmap? _thumbnail;
    public Avalonia.Media.Imaging.Bitmap? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(nameof(Thumbnail)); }
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

public class UserSettings
{
    public bool IsTacticalDeckMode { get; set; } = false;
    public bool ShowSystemClock { get; set; } = false;
    public bool Use24HourClock { get; set; } = true;
    public string DefaultBootDirectoryPath { get; set; } = "";
    public bool RememberLastDirectoryPath { get; set; } = true;
    public string LastDirectoryPath { get; set; } = "";
    public bool AllowMultipleInstances { get; set; } = false;
    public bool DisableHdrPeak { get; set; } = false;
    public bool ForceSoftwareDecoding { get; set; } = false;
    public string DefaultAudioLanguage { get; set; } = "eng";
    public bool PassthroughAc3 { get; set; } = false;
    public bool PassthroughDts { get; set; } = false;
    public bool PassthroughDtsHd { get; set; } = false;
    public bool PassthroughTrueHd { get; set; } = false;
    public bool WasapiExclusive { get; set; } = false;
    public string SubtitleFontFamily { get; set; } = "";
    public double SubtitleBorderSize { get; set; } = 3.0;
    public double SubtitleShadowOffset { get; set; } = 1.0;
    public string DefaultWorkspaceTab { get; set; } = "Queue";
    public bool DisableOsdNotifications { get; set; } = false;
    public bool DisableSeekingPreviews { get; set; } = false;
    public int DirectorySortMode { get; set; } = 0;
}
