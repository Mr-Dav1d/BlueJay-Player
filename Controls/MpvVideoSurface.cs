using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace BlueJayPlayer.Controls;

public class MpvVideoSurface : NativeControlHost
{
    public Action<IntPtr>? HandleReady { get; set; }
    public Action<object?, DragEventArgs>? DropHandler { get; set; }
    public Action<object?, TappedEventArgs>? DoubleTapHandler { get; set; }

    private Window? _overlayWindow;
    private Window? _parentWindow;

    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<MpvVideoSurface, object?>(nameof(Content));

    [Content]
    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public void EnsureOverlayVisible()
    {
        if (_overlayWindow == null)
        {
            InitializeNativeOverlay();
        }
        else
        {
            if (_parentWindow != null)
                _overlayWindow.Show(_parentWindow);
            else
                _overlayWindow.Show();
            SyncOverlayGeometry();
        }
    }

    public void HideOverlay()
    {
        _overlayWindow?.Hide();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        BlueJayPlayer.Views.PipLogger.Log("MpvVideoSurface: CreateNativeControlCore started");
        var handle = base.CreateNativeControlCore(parent);
        if (handle != null)
        {
            BlueJayPlayer.Views.PipLogger.Log($"MpvVideoSurface: CreateNativeControlCore succeeded, handle = {handle.Handle}");
            HandleReady?.Invoke(handle.Handle);
            return handle;
        }
        BlueJayPlayer.Views.PipLogger.Log("MpvVideoSurface: CreateNativeControlCore failed");
        throw new InvalidOperationException("Failed to create native control platform handle.");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _parentWindow = TopLevel.GetTopLevel(this) as Window;

        if (_parentWindow != null)
        {
            InitializeNativeOverlay();

            _parentWindow.PositionChanged += OnParentWindowMoved;
            _parentWindow.LayoutUpdated += OnParentWindowLayoutUpdated;

            this.PropertyChanged += OnSurfacePropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TeardownOverlay();
    }

    private void InitializeNativeOverlay()
    {
        if (_parentWindow == null) return;
        BlueJayPlayer.Views.PipLogger.Log($"MpvVideoSurface: InitializeNativeOverlay started, parentWindow = {_parentWindow.GetType().Name}");

        _overlayWindow = new Window
        {
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Background = Brushes.Transparent,
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            ShowInTaskbar = false,
            IsHitTestVisible = true,
            Focusable = false
        };

        _overlayWindow.Topmost = _parentWindow.Topmost;

        _overlayWindow.Bind(ContentControl.ContentProperty, this.GetObservable(ContentProperty));

        DragDrop.SetAllowDrop(_overlayWindow, true);
        DragDrop.AddDragOverHandler(_overlayWindow, OnOverlayDragOver);
        DragDrop.AddDropHandler(_overlayWindow, OnOverlayFileDropped);

        _overlayWindow.PointerMoved += OnOverlayPointerMoved;
        _overlayWindow.PointerPressed += OnOverlayPointerPressed;
        _overlayWindow.PointerReleased += OnOverlayPointerReleased;
        _overlayWindow.DoubleTapped += OnOverlayDoubleTapped;
        _overlayWindow.KeyDown += OnOverlayKeyDown;

        _overlayWindow.Show(_parentWindow);
        BlueJayPlayer.Views.PipLogger.Log("MpvVideoSurface: OverlayWindow shown");
        SyncOverlayGeometry();
    }

    public void SyncOverlayGeometry()
    {
        if (_overlayWindow == null || _parentWindow == null || !this.IsAttachedToVisualTree()) return;

        var originPoint = new Point(0, 0);
        var screenCoordinates = this.PointToScreen(originPoint);

        _overlayWindow.Position = screenCoordinates;
        _overlayWindow.Width = Bounds.Width;
        _overlayWindow.Height = Bounds.Height;
    }

    private void OnOverlayDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        e.DragEffects = DragDropEffects.Copy;
    }

    private void OnOverlayFileDropped(object? sender, DragEventArgs e)
    {
        DropHandler?.Invoke(_parentWindow ?? sender, e);
    }

    private void OnOverlayPointerMoved(object? sender, PointerEventArgs e)
    {
        RaiseEvent(e);

        if (_overlayWindow != null && _parentWindow != null)
        {
            if (_parentWindow.WindowState == WindowState.Maximized || _parentWindow.WindowState == WindowState.FullScreen)
            {
                _overlayWindow.Cursor = _parentWindow.Cursor;
                return;
            }

            var point = e.GetPosition(this);
            double w = this.Bounds.Width;
            double h = this.Bounds.Height;
            double margin = 12.0;

            bool left = point.X < margin;
            bool right = point.X > w - margin;
            bool top = point.Y < margin;
            bool bottom = point.Y > h - margin;

            if (top && left) _overlayWindow.Cursor = new Cursor(StandardCursorType.TopLeftCorner);
            else if (top && right) _overlayWindow.Cursor = new Cursor(StandardCursorType.TopRightCorner);
            else if (bottom && left) _overlayWindow.Cursor = new Cursor(StandardCursorType.BottomLeftCorner);
            else if (bottom && right) _overlayWindow.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
            else if (top) _overlayWindow.Cursor = new Cursor(StandardCursorType.TopSide);
            else if (bottom) _overlayWindow.Cursor = new Cursor(StandardCursorType.BottomSide);
            else if (left) _overlayWindow.Cursor = new Cursor(StandardCursorType.LeftSide);
            else if (right) _overlayWindow.Cursor = new Cursor(StandardCursorType.RightSide);
            else _overlayWindow.Cursor = _parentWindow.Cursor;
        }
    }

    private void OnOverlayDoubleTapped(object? sender, TappedEventArgs e)
    {
        var source = e.Source as Avalonia.Visual;
        while (source != null && source != _overlayWindow)
        {
            if (source is Button || source is Slider || source is ComboBox || source is TextBox)
            {
                return;
            }
            source = source.GetVisualParent();
        }

        DoubleTapHandler?.Invoke(sender, e);
    }

    private void OnOverlayPointerPressed(object? sender, PointerEventArgs e)
    {
        _parentWindow?.Activate();
        
        if (e is PointerPressedEventArgs pressedEvt)
        {
            var source = pressedEvt.Source as Avalonia.Visual;
            bool isInteractive = false;
            while (source != null && source != _overlayWindow)
            {
                if (source is Button || source is Slider)
                {
                    isInteractive = true;
                    break;
                }
                source = source.GetVisualParent();
            }

            if (!isInteractive && pressedEvt.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (_parentWindow != null && _parentWindow.WindowState != WindowState.Maximized && _parentWindow.WindowState != WindowState.FullScreen)
                {
                    var point = pressedEvt.GetPosition(this);
                    double w = this.Bounds.Width;
                    double h = this.Bounds.Height;
                    double margin = 12.0;

                    bool left = point.X < margin;
                    bool right = point.X > w - margin;
                    bool top = point.Y < margin;
                    bool bottom = point.Y > h - margin;

                    if (left || right || top || bottom)
                    {
                        WindowEdge? edge = null;
                        if (top && left) edge = WindowEdge.NorthWest;
                        else if (top && right) edge = WindowEdge.NorthEast;
                        else if (bottom && left) edge = WindowEdge.SouthWest;
                        else if (bottom && right) edge = WindowEdge.SouthEast;
                        else if (top) edge = WindowEdge.North;
                        else if (bottom) edge = WindowEdge.South;
                        else if (left) edge = WindowEdge.West;
                        else if (right) edge = WindowEdge.East;

                        if (edge.HasValue)
                        {
                            _parentWindow.BeginResizeDrag(edge.Value, pressedEvt);
                            return;
                        }
                    }
                }

                _parentWindow?.BeginMoveDrag(pressedEvt);
            }
        }
    }

    private void OnOverlayPointerReleased(object? sender, PointerEventArgs e)
    {
        // Let overlay content handle releases natively — do not forward to parent
    }

    private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            _parentWindow?.RaiseEvent(e);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = false;
            return;
        }

        // Forward the event to the parent window's handler
        _parentWindow?.RaiseEvent(e);
    }

    private void OnParentWindowMoved(object? sender, PixelPointEventArgs e) => SyncOverlayGeometry();
    private void OnParentWindowLayoutUpdated(object? sender, EventArgs e) => SyncOverlayGeometry();

    private void OnSurfacePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
        {
            SyncOverlayGeometry();
        }
        else if (e.Property == IsVisibleProperty)
        {
            if (_overlayWindow != null)
            {
                if (IsVisible)
                {
                    if (_parentWindow != null)
                        _overlayWindow.Show(_parentWindow);
                    else
                        _overlayWindow.Show();
                    SyncOverlayGeometry();
                }
                else
                {
                    _overlayWindow.Hide();
                }
            }
        }
    }

    private void TeardownOverlay()
    {
        BlueJayPlayer.Views.PipLogger.Log("MpvVideoSurface: TeardownOverlay started");
        if (_parentWindow != null)
        {
            _parentWindow.PositionChanged -= OnParentWindowMoved;
            _parentWindow.LayoutUpdated -= OnParentWindowLayoutUpdated;
        }

        this.PropertyChanged -= OnSurfacePropertyChanged;

        if (_overlayWindow != null)
        {
            DragDrop.RemoveDragOverHandler(_overlayWindow, OnOverlayDragOver);
            DragDrop.RemoveDropHandler(_overlayWindow, OnOverlayFileDropped);

            _overlayWindow.PointerMoved -= OnOverlayPointerMoved;
            _overlayWindow.PointerPressed -= OnOverlayPointerPressed;
            _overlayWindow.PointerReleased -= OnOverlayPointerReleased;
            _overlayWindow.KeyDown -= OnOverlayKeyDown;

            _overlayWindow.Content = null;
            _overlayWindow.Close();
            _overlayWindow = null;
            BlueJayPlayer.Views.PipLogger.Log("MpvVideoSurface: OverlayWindow closed and set to null");
        }
    }
}
