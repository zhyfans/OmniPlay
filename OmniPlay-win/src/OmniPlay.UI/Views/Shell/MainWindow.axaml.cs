using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OmniPlay.Core.ViewModels;
using OmniPlay.Core.ViewModels.Library;

namespace OmniPlay.UI.Views.Shell;

public partial class MainWindow : Window
{
    private const double ResizeBorderThickness = 6;
    private const double ResizeCornerSize = 14;

    private ShellViewModel? currentViewModel;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureResizeCursors();
        AddHandler(PointerPressedEvent, MainWindow_OnPointerPressed, RoutingStrategies.Tunnel, true);
        DataContextChanged += MainWindow_OnDataContextChanged;
    }

    private void ConfigureResizeCursors()
    {
        ShellNorthResizeGrip.Cursor = new Cursor(StandardCursorType.TopSide);
        ShellSouthResizeGrip.Cursor = new Cursor(StandardCursorType.BottomSide);
        ShellWestResizeGrip.Cursor = new Cursor(StandardCursorType.LeftSide);
        ShellEastResizeGrip.Cursor = new Cursor(StandardCursorType.RightSide);
        ShellNorthWestResizeGrip.Cursor = new Cursor(StandardCursorType.TopLeftCorner);
        ShellNorthEastResizeGrip.Cursor = new Cursor(StandardCursorType.TopRightCorner);
        ShellSouthWestResizeGrip.Cursor = new Cursor(StandardCursorType.BottomLeftCorner);
        ShellSouthEastResizeGrip.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
    }

    private void MainWindow_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        TryBeginShellResizeDrag(e);
    }

    private void MainWindow_OnDataContextChanged(object? sender, EventArgs e)
    {
        if (currentViewModel is not null)
        {
            currentViewModel.PosterWall.PropertyChanged -= PosterWall_OnPropertyChanged;
        }

        currentViewModel = DataContext as ShellViewModel;

        if (currentViewModel is not null)
        {
            currentViewModel.PosterWall.PropertyChanged += PosterWall_OnPropertyChanged;
        }

        UpdateShellWindowControlsVisibility();
    }

    private void PosterWall_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PosterWallViewModel.IsPlayerOverlayOpen))
        {
            UpdateShellWindowControlsVisibility();
        }
    }

    private void ShellWindowControlsHotZone_OnPointerActivity(object? sender, PointerEventArgs e)
    {
        UpdateShellWindowControlsCursor(e);
        ShowShellWindowControls();
    }

    private void ShellWindowControlsBar_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        UpdateShellWindowControlsCursor(e);
        ShowShellWindowControls();
    }

    private void ShellWindowControlsBar_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        UpdateShellWindowControlsCursor(e);
        ShowShellWindowControls();
    }

    private void ShellWindowControlsBar_OnPointerExited(object? sender, PointerEventArgs e)
    {
        ResetShellWindowControlsCursor();
        UpdateShellWindowControlsVisibility();
    }

    private void ShellWindowControlsBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsWithinShellWindowButton(e.Source as Control) ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (TryBeginShellResizeDrag(e))
        {
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void ShellResizeBorder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanBeginShellResizeDrag(e))
        {
            return;
        }

        if (sender is not Control { Tag: string edgeName } ||
            !Enum.TryParse<WindowEdge>(edgeName, out var edge))
        {
            return;
        }

        BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private bool TryBeginShellResizeDrag(PointerPressedEventArgs e)
    {
        if (!CanBeginShellResizeDrag(e))
        {
            return false;
        }

        var edge = ResolveResizeEdge(e.GetPosition(this));
        if (edge is null)
        {
            return false;
        }

        BeginResizeDrag(edge.Value, e);
        e.Handled = true;
        return true;
    }

    private bool CanBeginShellResizeDrag(PointerPressedEventArgs e)
    {
        return WindowState == WindowState.Normal &&
               currentViewModel?.PosterWall.IsPlayerOverlayOpen != true &&
               e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
    }

    private WindowEdge? ResolveResizeEdge(Point point)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var north = point.Y <= ResizeBorderThickness;
        var south = point.Y >= height - ResizeBorderThickness;
        var west = point.X <= ResizeBorderThickness;
        var east = point.X >= width - ResizeBorderThickness;
        var northCorner = point.Y <= ResizeCornerSize;
        var southCorner = point.Y >= height - ResizeCornerSize;
        var westCorner = point.X <= ResizeCornerSize;
        var eastCorner = point.X >= width - ResizeCornerSize;

        if (northCorner && westCorner)
        {
            return WindowEdge.NorthWest;
        }

        if (northCorner && eastCorner)
        {
            return WindowEdge.NorthEast;
        }

        if (southCorner && westCorner)
        {
            return WindowEdge.SouthWest;
        }

        if (southCorner && eastCorner)
        {
            return WindowEdge.SouthEast;
        }

        if (north)
        {
            return WindowEdge.North;
        }

        if (south)
        {
            return WindowEdge.South;
        }

        if (west)
        {
            return WindowEdge.West;
        }

        return east ? WindowEdge.East : null;
    }

    private static bool IsWithinShellWindowButton(Control? control)
    {
        for (var current = control; current is not null; current = current.Parent as Control)
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateShellWindowControlsCursor(PointerEventArgs e)
    {
        if (WindowState != WindowState.Normal ||
            currentViewModel?.PosterWall.IsPlayerOverlayOpen == true ||
            IsWithinShellWindowButton(e.Source as Control))
        {
            ResetShellWindowControlsCursor();
            return;
        }

        var cursor = ResolveResizeCursor(ResolveResizeEdge(e.GetPosition(this)));
        Cursor = cursor;
        ShellWindowControlsLayer.Cursor = cursor;
        ShellWindowControlsBar.Cursor = cursor;
    }

    private void ResetShellWindowControlsCursor()
    {
        Cursor = null;
        ShellWindowControlsLayer.Cursor = null;
        ShellWindowControlsBar.Cursor = null;
    }

    private static Cursor? ResolveResizeCursor(WindowEdge? edge)
    {
        return edge switch
        {
            WindowEdge.North => new Cursor(StandardCursorType.TopSide),
            WindowEdge.South => new Cursor(StandardCursorType.BottomSide),
            WindowEdge.West => new Cursor(StandardCursorType.LeftSide),
            WindowEdge.East => new Cursor(StandardCursorType.RightSide),
            WindowEdge.NorthWest => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast => new Cursor(StandardCursorType.TopRightCorner),
            WindowEdge.SouthWest => new Cursor(StandardCursorType.BottomLeftCorner),
            WindowEdge.SouthEast => new Cursor(StandardCursorType.BottomRightCorner),
            _ => null
        };
    }

    private void ShellMinimizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ShellMaximizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void ShellCloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowShellWindowControls()
    {
        if (currentViewModel?.PosterWall.IsPlayerOverlayOpen == true)
        {
            UpdateShellWindowControlsVisibility();
            return;
        }

        UpdateShellWindowControlsVisibility();
    }

    private void HideShellWindowControls()
    {
        UpdateShellWindowControlsVisibility();
    }

    private void UpdateShellWindowControlsVisibility()
    {
        ShellWindowControlsBar.IsVisible = currentViewModel?.PosterWall.IsPlayerOverlayOpen != true;
    }
}
