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

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        if (handle != null)
        {
            HandleReady?.Invoke(handle.Handle);
            return handle;
        }
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

        _overlayWindow.Bind(Window.CursorProperty, _parentWindow.GetObservable(Window.CursorProperty));
        _overlayWindow.Bind(ContentControl.ContentProperty, this.GetObservable(ContentProperty));

        DragDrop.SetAllowDrop(_overlayWindow, true);
        DragDrop.AddDragOverHandler(_overlayWindow, OnOverlayDragOver);
        DragDrop.AddDropHandler(_overlayWindow, OnOverlayFileDropped);

        _overlayWindow.PointerMoved += OnOverlayPointerMoved;
        _overlayWindow.PointerPressed += OnOverlayPointerPressed;
        _overlayWindow.PointerReleased += OnOverlayPointerReleased;
        _overlayWindow.KeyDown += OnOverlayKeyDown;

        _overlayWindow.Show(_parentWindow);
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
    }

    private void OnOverlayPointerPressed(object? sender, PointerEventArgs e)
    {
        // Let overlay content handle presses natively — do not forward to parent
    }

    private void OnOverlayPointerReleased(object? sender, PointerEventArgs e)
    {
        // Let overlay content handle releases natively — do not forward to parent
    }

    private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
    {
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
    }

    private void TeardownOverlay()
    {
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
        }
    }
}
