using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CwsEditor.Core;
using ImageMagick;
using Microsoft.Win32;

namespace CwsEditor.Wpf;

public partial class MainWindow : Window
{
    private const string DefaultSelectionText = "Shift+drag in the overview or main viewer to select a depth interval. Drag the yellow box to move the current viewport.";
    private const string DefaultHoverText = "Depth / wrap angle / file orientation";

    private readonly EditSession _session = new();
    private readonly DispatcherTimer? _tonePreviewTimer;

    private CwsDocument? _document;
    private CwsRenderService? _renderer;
    private DepthMapper? _depthMapper;
    private CancellationTokenSource? _overviewRenderCts;
    private CancellationTokenSource? _viewerRenderCts;

    private bool _isOverviewSelecting;
    private bool _isOverviewViewportDragging;
    private bool _isViewerSelecting;
    private bool _suppressExternalScrollbarChange;
    private bool _suppressViewerScrollRefresh;
    private bool _suppressTonePreview;

    private Point _selectionDragStartPoint;
    private double _overviewViewportDragStartVerticalOffset;
    private double? _selectionStartSourceY;
    private double? _selectionEndSourceY;
    private double _currentDisplayHeight;
    private double _currentOverviewVerticalScale;
    private double _overviewZoom = 1d;
    private WarpMap? _currentWarp;
    private HoverState? _currentHoverState;

    private Point? _pendingZoomAnchorViewportPoint;
    private double? _pendingZoomAnchorDisplayX;
    private double? _pendingZoomAnchorDisplayY;
    private double? _pendingOverviewZoomAnchorViewportY;
    private double? _pendingOverviewZoomAnchorDisplayY;
    private ToneTarget? _pendingTonePreviewTarget;
    private bool _suppressRegionSelectionNavigation;

    private SourceDepthUnit _sourceDepthUnit = SourceDepthUnit.Meters;
    private DisplayDepthUnit _displayDepthUnit = DisplayDepthUnit.Metric;
    private int _nextRegionNumber = 1;

    public MainWindow()
    {
        InitializeComponent();

        _tonePreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _tonePreviewTimer.Tick += TonePreviewTimer_Tick;

        ApplyUnitSelectionsFromControls();
        UpdateUnitToggleButtons();
        ResetToneControls();
        SetStatus("Open a .cws file to begin.");
        UpdateRegionList();
        UpdateSelectionSummary();
        UpdateToneValueLabels();
        ResetExportProgress();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Filter = "Composite Wellbore Stitch (*.cws)|*.cws|All files (*.*)|*.*",
            Title = "Open CWS file",
        };

        if (dialog.ShowDialog(this) == true)
        {
            await LoadDocumentAsync(dialog.FileName);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            MessageBox.Show(this, "Open a .cws file first.", "Missing input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryApplyGlobalControls())
        {
            return;
        }

        string outputPath = OutputPathTextBlock.Text;
        if (string.IsNullOrWhiteSpace(outputPath) || outputPath.StartsWith("Output path", StringComparison.OrdinalIgnoreCase))
        {
            outputPath = SuggestOutputPath(_document.SourcePath);
        }

        SaveFileDialog dialog = new()
        {
            Filter = "Composite Wellbore Stitch (*.cws)|*.cws|All files (*.*)|*.*",
            FileName = Path.GetFileName(outputPath),
            InitialDirectory = Path.GetDirectoryName(outputPath),
            Title = "Save edited CWS file",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        OutputPathTextBlock.Text = dialog.FileName;

        try
        {
            SetBusyUi(true);
            ResetExportProgress();
            Progress<SaveProgress> progress = new(UpdateExportProgress);
            await CwsArchive.SaveEditedAsync(_document, _session, dialog.FileName, progress);
            SetStatus($"Saved edited CWS: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus($"Save failed: {ex.Message}");
        }
        finally
        {
            ResetExportProgress();
            SetBusyUi(false);
        }
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        string candidate = _document is null ? string.Empty : SuggestOutputPath(_document.SourcePath);
        SaveFileDialog dialog = new()
        {
            Filter = "Composite Wellbore Stitch (*.cws)|*.cws|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(OutputPathTextBlock.Text) || OutputPathTextBlock.Text.StartsWith("Output path", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(candidate)
                : Path.GetFileName(OutputPathTextBlock.Text),
            InitialDirectory = !string.IsNullOrWhiteSpace(candidate) ? Path.GetDirectoryName(candidate) : null,
            Title = "Choose output CWS path",
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputPathTextBlock.Text = dialog.FileName;
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ResetEditSession();
        await RefreshAllRendersAsync(resetScrollOffset: false, refreshOverview: true);
    }

    private async void ApplyGlobalGeometryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseDouble(GlobalScaleTextBox.Text, out double scale))
        {
            MessageBox.Show(this, "Global scale must be numeric.", "Invalid scale", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _session.GlobalVerticalScale = scale;
        await RefreshAllRendersAsync(resetScrollOffset: false, refreshOverview: true);
    }

    private async void ApplySelectionGeometryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        if (!TryGetSelection(out double selectionStart, out double selectionEnd))
        {
            MessageBox.Show(this, "Create a selection first.", "Missing selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryParseDouble(SelectionScaleTextBox.Text, out double scale))
        {
            MessageBox.Show(this, "Region scale must be numeric.", "Invalid scale", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EditRegion region = FindMatchingRegion(selectionStart, selectionEnd) ??
            new(Guid.NewGuid(), NextRegionName(), selectionStart, selectionEnd, RegionGeometryMode.None, null, ToneAdjustment.Identity);
        _session.UpsertRegion(region.WithScale(scale));
        await RefreshAllRendersAsync(resetScrollOffset: false, refreshOverview: true);
    }

    private async void CropSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        if (!TryGetSelection(out double selectionStart, out double selectionEnd))
        {
            MessageBox.Show(this, "Create a selection first.", "Missing selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EditRegion region = FindMatchingRegion(selectionStart, selectionEnd) ??
            new(Guid.NewGuid(), NextRegionName(), selectionStart, selectionEnd, RegionGeometryMode.None, null, ToneAdjustment.Identity);
        _session.UpsertRegion(region.WithCrop());
        await RefreshAllRendersAsync(resetScrollOffset: false, refreshOverview: true);
    }

    private async void ApplyGlobalToneButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyToneFromControlsAsync(ToneTarget.Global, refreshOverview: true, showErrors: false);
    }

    private async void ApplySelectionToneButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyToneFromControlsAsync(ToneTarget.Selection, refreshOverview: true, showErrors: true);
    }

    private async void RemoveSelectedRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (RegionsListView.SelectedItem is not RegionListItem selected)
        {
            return;
        }

        _session.RemoveRegion(selected.Id);
        await RefreshAllRendersAsync(resetScrollOffset: false, refreshOverview: true);
    }

    private async void ClearRegionsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (EditRegion region in _session.Regions.ToArray())
        {
            _session.RemoveRegion(region.Id);
        }

        await RefreshAllRendersAsync(resetScrollOffset: false, refreshOverview: true);
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private async void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomValueTextBlock is not null)
        {
            ZoomValueTextBlock.Text = $"{e.NewValue * 100d:0}%";
        }

        if (!IsLoaded || _document is null || _renderer is null)
        {
            return;
        }

        await RefreshViewerAsync(resetScrollOffset: false);
    }

    private async void OverviewZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _overviewZoom = Math.Clamp(e.NewValue, 0.4d, 3d);
        if (OverviewZoomValueTextBlock is not null)
        {
            OverviewZoomValueTextBlock.Text = $"{_overviewZoom * 100d:0}%";
        }

        if (!IsLoaded || _document is null || _renderer is null)
        {
            return;
        }

        await RefreshOverviewAsync();
    }

    private void OverviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_document is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        double currentZoom = Math.Clamp(OverviewZoomSlider.Value, OverviewZoomSlider.Minimum, OverviewZoomSlider.Maximum);
        double zoomStep = e.Delta > 0 ? 0.1d : -0.1d;
        double nextZoom = Math.Clamp(currentZoom + zoomStep, OverviewZoomSlider.Minimum, OverviewZoomSlider.Maximum);
        if (Math.Abs(nextZoom - currentZoom) < 0.0001d)
        {
            return;
        }

        Point anchorViewport = e.GetPosition(OverviewScrollViewer);
        _pendingOverviewZoomAnchorViewportY = anchorViewport.Y;
        _pendingOverviewZoomAnchorDisplayY = _currentOverviewVerticalScale <= 0d
            ? null
            : (OverviewScrollViewer.VerticalOffset + anchorViewport.Y) / _currentOverviewVerticalScale;
        OverviewZoomSlider.Value = nextZoom;
    }

    private void OverviewScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_document is null)
        {
            UpdateOverviewScrollBar();
            return;
        }

        UpdateOverviewScrollBar();
        UpdateSelectionVisuals();
    }

    private async void ViewerScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressViewerScrollRefresh || _document is null || _renderer is null)
        {
            return;
        }

        UpdateViewerScrollBars();
        UpdateOverviewScrollBar();
        UpdateSelectionVisuals();
        RefreshHoverInfoFromCurrentPointer();

        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
        {
            await RefreshViewerAsync(resetScrollOffset: false);
        }
    }

    private void ViewerVerticalScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressExternalScrollbarChange)
        {
            return;
        }

        ExecuteWithoutViewerScrollRefresh(() => ViewerScrollViewer.ScrollToVerticalOffset(e.NewValue));
        UpdateSelectionVisuals();
        RefreshHoverInfoFromCurrentPointer();
        _ = RefreshViewerAsync(resetScrollOffset: false);
    }

    private void ViewerHorizontalScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressExternalScrollbarChange)
        {
            return;
        }

        ExecuteWithoutViewerScrollRefresh(() => ViewerScrollViewer.ScrollToHorizontalOffset(e.NewValue));
        UpdateSelectionVisuals();
        RefreshHoverInfoFromCurrentPointer();
    }

    private void OverviewVerticalScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressExternalScrollbarChange)
        {
            return;
        }

        OverviewScrollViewer.ScrollToVerticalOffset(e.NewValue);
        UpdateSelectionVisuals();
    }

    private void ViewerScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_document is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        double currentZoom = Math.Clamp(ZoomSlider.Value, ZoomSlider.Minimum, ZoomSlider.Maximum);
        double zoomStep = e.Delta > 0 ? 0.1d : -0.1d;
        double nextZoom = Math.Clamp(currentZoom + zoomStep, ZoomSlider.Minimum, ZoomSlider.Maximum);
        if (Math.Abs(nextZoom - currentZoom) < 0.0001d)
        {
            return;
        }

        Point anchorViewport = e.GetPosition(ViewerScrollViewer);
        _pendingZoomAnchorViewportPoint = anchorViewport;
        _pendingZoomAnchorDisplayX = (ViewerScrollViewer.HorizontalOffset + anchorViewport.X) / currentZoom;
        _pendingZoomAnchorDisplayY = (ViewerScrollViewer.VerticalOffset + anchorViewport.Y) / currentZoom;
        ZoomSlider.Value = nextZoom;
    }

    private void UnitToggleButton_Click(object sender, RoutedEventArgs e)
    {
        NormalizeUnitToggleStates((FrameworkElement)sender);
        ApplyUnitSelectionsFromControls();
        UpdateSelectionSummary();
        UpdateSelectionVisuals();
        RefreshHoverInfoFromCurrentPointer();
    }

    private async void RegionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRegionSelectionNavigation || RegionsListView.SelectedItem is not RegionListItem item)
        {
            return;
        }

        EditRegion? region = _session.Regions.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (region is null)
        {
            return;
        }

        await NavigateToRegionAsync(region);
    }

    private async void ToggleRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Guid regionId)
        {
            return;
        }

        EditRegion? region = _session.Regions.FirstOrDefault(candidate => candidate.Id == regionId);
        if (region is null)
        {
            return;
        }

        _session.UpsertRegion(region.WithEnabled(!region.IsEnabled));
        await RefreshAllRendersAsync(resetScrollOffset: false, refreshOverview: true);
    }

    private void ToneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateToneValueLabels();
        if (!IsLoaded || _document is null || _suppressTonePreview)
        {
            return;
        }

        _pendingTonePreviewTarget = GetToneTargetFromControl((DependencyObject)sender);
        RestartTonePreviewTimer();
    }

    private async void ToneSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        await CommitTonePreviewAsync(GetToneTargetFromControl((DependencyObject)sender));
    }

    private async void ToneSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        await CommitTonePreviewAsync(GetToneTargetFromControl((DependencyObject)sender));
    }

    private async void NormalizeToneCheckBox_Click(object sender, RoutedEventArgs e)
    {
        ToneTarget target = GetToneTargetFromControl((DependencyObject)sender);
        await ApplyToneFromControlsAsync(target, refreshOverview: true, showErrors: target == ToneTarget.Selection);
    }

    private async void OverviewSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_document is null || OverviewCanvas.ActualHeight <= 0d)
        {
            return;
        }

        Point point = e.GetPosition(OverviewCanvas);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            _isOverviewSelecting = true;
            _selectionDragStartPoint = point;
            OverviewCanvas.CaptureMouse();
            UpdateSelectionFromOverviewSurface(point.Y, point.Y);
            return;
        }

        if (!IsPointInsideOverviewViewport(point))
        {
            CenterViewerOnOverviewPoint(point.Y);
            UpdateSelectionVisuals();
            RefreshHoverInfoFromCurrentPointer();
            await RefreshViewerAsync(resetScrollOffset: false);
            return;
        }

        _isOverviewViewportDragging = true;
        _selectionDragStartPoint = point;
        _overviewViewportDragStartVerticalOffset = GetViewerVerticalOffset();
        OverviewCanvas.CaptureMouse();
    }

    private void OverviewSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        Point point = e.GetPosition(OverviewCanvas);
        if (_isOverviewSelecting)
        {
            UpdateSelectionFromOverviewSurface(_selectionDragStartPoint.Y, point.Y);
            return;
        }

        if (_isOverviewViewportDragging)
        {
            DragOverviewViewport(point.Y);
        }
    }

    private async void OverviewSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isOverviewSelecting && !_isOverviewViewportDragging)
        {
            return;
        }

        _isOverviewSelecting = false;
        _isOverviewViewportDragging = false;
        OverviewCanvas.ReleaseMouseCapture();
        UpdateSelectionVisuals();
        RefreshHoverInfoFromCurrentPointer();
        await RefreshViewerAsync(resetScrollOffset: false);
    }

    private void ViewerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_document is null || Keyboard.Modifiers != ModifierKeys.Shift)
        {
            return;
        }

        _isViewerSelecting = true;
        _selectionDragStartPoint = e.GetPosition(ViewerCanvas);
        ViewerCanvas.CaptureMouse();
        UpdateSelectionFromViewer(_selectionDragStartPoint.Y, _selectionDragStartPoint.Y);
    }

    private async void ViewerCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isViewerSelecting)
        {
            return;
        }

        _isViewerSelecting = false;
        ViewerCanvas.ReleaseMouseCapture();
        await RefreshViewerAsync(resetScrollOffset: false);
    }

    private void ViewerCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_document is null || _depthMapper is null)
        {
            HoverInfoTextBlock.Text = DefaultHoverText;
            return;
        }

        Point point = e.GetPosition(ViewerCanvas);
        if (_isViewerSelecting)
        {
            UpdateSelectionFromViewer(_selectionDragStartPoint.Y, point.Y);
            return;
        }

        RefreshHoverInfoFromCanvasPoint(point);
    }

    private void ViewerCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isViewerSelecting)
        {
            _currentHoverState = null;
            HoverInfoTextBlock.Text = DefaultHoverText;
        }
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _tonePreviewTimer?.Stop();
        _overviewRenderCts?.Cancel();
        _viewerRenderCts?.Cancel();
        _renderer?.Dispose();
    }

    private async void TonePreviewTimer_Tick(object? sender, EventArgs e)
    {
        _tonePreviewTimer?.Stop();
        if (_pendingTonePreviewTarget is ToneTarget target)
        {
            await ApplyToneFromControlsAsync(target, refreshOverview: false, showErrors: false);
        }
    }

    private async Task LoadDocumentAsync(string path)
    {
        try
        {
            SetBusyUi(true);
            SetStatus("Loading CWS archive...");
            _overviewRenderCts?.Cancel();
            _viewerRenderCts?.Cancel();
            _renderer?.Dispose();

            _document = await CwsArchive.LoadAsync(path);
            _renderer = new CwsRenderService(_document);
            _depthMapper = new DepthMapper(_document);
            _currentHoverState = null;
            ResetUnitSelectors();
            ResetEditSession();
            InputPathTextBlock.Text = path;
            OutputPathTextBlock.Text = SuggestOutputPath(path);
            Title = $"Stitch Helper - {Path.GetFileName(path)}";
            await RefreshAllRendersAsync(resetScrollOffset: true, refreshOverview: true);
            HoverInfoTextBlock.Text = DefaultHoverText;
            SetStatus($"Loaded {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus($"Load failed: {ex.Message}");
        }
        finally
        {
            SetBusyUi(false);
        }
    }

    private async Task RefreshAllRendersAsync(bool resetScrollOffset, bool refreshOverview)
    {
        UpdateRegionList();
        UpdateSelectionSummary();
        if (refreshOverview)
        {
            await RefreshOverviewAsync();
        }

        await RefreshViewerAsync(resetScrollOffset);
    }

    private async Task RefreshOverviewAsync()
    {
        if (_renderer is null || _document is null)
        {
            OverviewImage.Source = null;
            return;
        }

        _overviewRenderCts?.Cancel();
        _overviewRenderCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _overviewRenderCts.Token;

        try
        {
            WarpMap warp = GetCurrentWarp();
            using MagickImage image = await _renderer.RenderOverviewImageAsync(_session, warp, _overviewZoom, cancellationToken);
            OverviewImage.Source = ToBitmapSource(image);

            double overviewViewportWidth = GetOverviewViewportWidth();
            double canvasWidth = Math.Max(1d, overviewViewportWidth > 0d
                ? overviewViewportWidth
                : Math.Max(OverviewScrollViewer.ActualWidth - 8d, 1d));
            OverviewCanvas.Width = canvasWidth;
            OverviewCanvas.Height = image.Height;
            OverviewImage.Width = canvasWidth;
            OverviewImage.Height = image.Height;
            _currentOverviewVerticalScale = _currentDisplayHeight <= 0d ? 0d : image.Height / _currentDisplayHeight;
            ApplyPendingOverviewZoomAnchorIfNeeded();
            UpdateOverviewScrollBar();
            UpdateSelectionVisuals();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus($"Overview render failed: {ex.Message}");
        }
    }

    private async Task RefreshViewerAsync(bool resetScrollOffset)
    {
        if (_document is null || _renderer is null)
        {
            return;
        }

        WarpMap warp = GetCurrentWarp();
        if (resetScrollOffset)
        {
            ExecuteWithoutViewerScrollRefresh(() =>
            {
                ViewerScrollViewer.ScrollToVerticalOffset(0d);
                ViewerScrollViewer.ScrollToHorizontalOffset(0d);
            });
        }

        double zoom = Math.Max(0.1d, ZoomSlider.Value);
        ViewerCanvas.Width = _document.CompositeWidth * zoom;
        ViewerCanvas.Height = Math.Max(1d, _currentDisplayHeight * zoom);
        ApplyPendingZoomAnchorIfNeeded(zoom);
        UpdateViewerScrollBars();
        UpdateSelectionVisuals();

        double viewerViewportHeight = GetViewerViewportHeight();
        double displayStart = Math.Max(0d, (GetViewerVerticalOffset() / zoom) - (220d / zoom));
        double displayHeight = Math.Min(_currentDisplayHeight - displayStart, (viewerViewportHeight / zoom) + (440d / zoom));
        if (displayHeight <= 0d)
        {
            displayHeight = Math.Max(1d, viewerViewportHeight / zoom);
        }

        _viewerRenderCts?.Cancel();
        _viewerRenderCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _viewerRenderCts.Token;

        try
        {
            using MagickImage image = await _renderer.RenderViewportImageAsync(_session, warp, displayStart, displayHeight, zoom, cancellationToken);
            ViewportImage.Source = ToBitmapSource(image);
            Canvas.SetLeft(ViewportImage, 0d);
            Canvas.SetTop(ViewportImage, displayStart * zoom);
            ViewportImage.Width = image.Width;
            ViewportImage.Height = image.Height;
            UpdateSelectionVisuals();
            RefreshHoverInfoFromCurrentPointer();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus($"Viewer render failed: {ex.Message}");
        }
    }

    private void UpdateSelectionFromOverviewSurface(double startY, double endY)
    {
        if (_document is null || OverviewCanvas.ActualHeight <= 0d || _currentOverviewVerticalScale <= 0d)
        {
            return;
        }

        double top = Math.Clamp(Math.Min(startY, endY), 0d, OverviewCanvas.ActualHeight);
        double bottom = Math.Clamp(Math.Max(startY, endY), 0d, OverviewCanvas.ActualHeight);
        WarpMap warp = GetCurrentWarp();
        _selectionStartSourceY = warp.Inverse(top / _currentOverviewVerticalScale);
        _selectionEndSourceY = warp.Inverse(bottom / _currentOverviewVerticalScale);
        UpdateSelectionSummary();
        UpdateSelectionVisuals();
    }

    private void UpdateSelectionFromViewer(double startY, double endY)
    {
        if (_document is null)
        {
            return;
        }

        double zoom = Math.Max(0.1d, ZoomSlider.Value);
        double top = Math.Clamp(Math.Min(startY, endY), 0d, ViewerCanvas.Height);
        double bottom = Math.Clamp(Math.Max(startY, endY), 0d, ViewerCanvas.Height);
        WarpMap warp = GetCurrentWarp();
        _selectionStartSourceY = warp.Inverse(top / zoom);
        _selectionEndSourceY = warp.Inverse(bottom / zoom);
        UpdateSelectionSummary();
        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        if (_document is null)
        {
            OverviewSelectionRect.Visibility = Visibility.Collapsed;
            OverviewViewportRect.Visibility = Visibility.Collapsed;
            ViewerSelectionRect.Visibility = Visibility.Collapsed;
            ClearDepthOverlays();
            return;
        }

        WarpMap warp = GetCurrentWarp();
        UpdateOverviewViewportVisual();
        UpdateDepthOverlays(warp);

        if (!TryGetSelection(out double startSourceY, out double endSourceY))
        {
            OverviewSelectionRect.Visibility = Visibility.Collapsed;
            ViewerSelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        if (OverviewCanvas.ActualHeight > 0d && _currentOverviewVerticalScale > 0d)
        {
            double overviewTop = warp.Forward(startSourceY) * _currentOverviewVerticalScale;
            double overviewBottom = warp.Forward(endSourceY) * _currentOverviewVerticalScale;
            OverviewSelectionRect.Visibility = Visibility.Visible;
            OverviewSelectionRect.Width = OverviewCanvas.ActualWidth;
            OverviewSelectionRect.Height = Math.Max(1d, overviewBottom - overviewTop);
            Canvas.SetLeft(OverviewSelectionRect, 0d);
            Canvas.SetTop(OverviewSelectionRect, overviewTop);
        }
        else
        {
            OverviewSelectionRect.Visibility = Visibility.Collapsed;
        }

        double zoom = Math.Max(0.1d, ZoomSlider.Value);
        double viewerTop = warp.Forward(startSourceY) * zoom;
        double viewerBottom = warp.Forward(endSourceY) * zoom;
        ViewerSelectionRect.Visibility = Visibility.Visible;
        ViewerSelectionRect.Width = ViewerCanvas.Width;
        ViewerSelectionRect.Height = Math.Max(1d, viewerBottom - viewerTop);
        Canvas.SetLeft(ViewerSelectionRect, 0d);
        Canvas.SetTop(ViewerSelectionRect, viewerTop);
    }

    private void UpdateOverviewViewportVisual()
    {
        if (_document is null || _currentOverviewVerticalScale <= 0d || OverviewCanvas.ActualWidth <= 0d)
        {
            OverviewViewportRect.Visibility = Visibility.Collapsed;
            return;
        }

        double zoom = Math.Max(0.1d, ZoomSlider.Value);
        double viewerViewportHeight = GetViewerViewportHeight();
        double viewportDisplayStart = GetViewerVerticalOffset() / zoom;
        double viewportDisplayHeight = Math.Max(1d, viewerViewportHeight / zoom);
        double viewportDisplayEnd = Math.Min(_currentDisplayHeight, viewportDisplayStart + viewportDisplayHeight);

        OverviewViewportRect.Visibility = Visibility.Visible;
        OverviewViewportRect.Width = OverviewCanvas.ActualWidth;
        OverviewViewportRect.Height = Math.Max(1d, (viewportDisplayEnd - viewportDisplayStart) * _currentOverviewVerticalScale);
        Canvas.SetLeft(OverviewViewportRect, 0d);
        Canvas.SetTop(OverviewViewportRect, viewportDisplayStart * _currentOverviewVerticalScale);
    }

    private void UpdateViewerScrollBars()
    {
        if (ViewerVerticalScrollBar is null || ViewerHorizontalScrollBar is null || ViewerScrollCorner is null)
        {
            return;
        }

        if (_document is null)
        {
            ViewerVerticalScrollBar.Visibility = Visibility.Collapsed;
            ViewerHorizontalScrollBar.Visibility = Visibility.Collapsed;
            ViewerScrollCorner.Visibility = Visibility.Collapsed;
            return;
        }

        double viewerExtentHeight = GetViewerExtentHeight();
        double viewerExtentWidth = GetViewerExtentWidth();
        double viewerViewportHeight = GetViewerViewportHeight();
        double viewerViewportWidth = GetViewerViewportWidth();
        double verticalMaximum = Math.Max(0d, viewerExtentHeight - viewerViewportHeight);
        double horizontalMaximum = Math.Max(0d, viewerExtentWidth - viewerViewportWidth);
        bool showVertical = verticalMaximum > 0.5d;
        bool showHorizontal = horizontalMaximum > 0.5d;

        _suppressExternalScrollbarChange = true;
        try
        {
            ViewerVerticalScrollBar.Visibility = showVertical ? Visibility.Visible : Visibility.Collapsed;
            ViewerVerticalScrollBar.Maximum = verticalMaximum;
            ViewerVerticalScrollBar.ViewportSize = Math.Max(0d, viewerViewportHeight);
            ViewerVerticalScrollBar.SmallChange = 48d;
            ViewerVerticalScrollBar.LargeChange = Math.Max(64d, viewerViewportHeight * 0.9d);
            ViewerVerticalScrollBar.Value = ClampFinite(GetViewerVerticalOffset(), 0d, verticalMaximum);

            ViewerHorizontalScrollBar.Visibility = showHorizontal ? Visibility.Visible : Visibility.Collapsed;
            ViewerHorizontalScrollBar.Maximum = horizontalMaximum;
            ViewerHorizontalScrollBar.ViewportSize = Math.Max(0d, viewerViewportWidth);
            ViewerHorizontalScrollBar.SmallChange = 48d;
            ViewerHorizontalScrollBar.LargeChange = Math.Max(64d, viewerViewportWidth * 0.9d);
            ViewerHorizontalScrollBar.Value = ClampFinite(GetViewerHorizontalOffset(), 0d, horizontalMaximum);

            ViewerScrollCorner.Visibility = showVertical && showHorizontal ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _suppressExternalScrollbarChange = false;
        }
    }

    private void UpdateDepthOverlays(WarpMap warp)
    {
        if (_depthMapper is null || _currentDisplayHeight <= 0d)
        {
            ClearDepthOverlays();
            return;
        }

        UpdateViewerDepthOverlays(warp);
        UpdateOverviewDepthOverlays(warp);
    }

    private void UpdateViewerDepthOverlays(WarpMap warp)
    {
        double zoom = Math.Max(0.1d, ZoomSlider.Value);
        double viewportHeight = GetViewerViewportHeight();
        double displayTop = Math.Clamp(GetViewerVerticalOffset() / zoom, 0d, _currentDisplayHeight);
        double displayBottom = Math.Clamp(displayTop + (viewportHeight / zoom), 0d, _currentDisplayHeight);
        SetDepthOverlayText(
            ViewerTopDepthTextBlock,
            ViewerBottomDepthTextBlock,
            warp.Inverse(displayTop),
            warp.Inverse(displayBottom));
    }

    private void UpdateOverviewDepthOverlays(WarpMap warp)
    {
        if (_currentOverviewVerticalScale <= 0d)
        {
            OverviewTopDepthTextBlock.Text = string.Empty;
            OverviewBottomDepthTextBlock.Text = string.Empty;
            return;
        }

        double viewportHeight = GetOverviewViewportHeight();
        double displayTop = Math.Clamp(GetOverviewVerticalOffset() / _currentOverviewVerticalScale, 0d, _currentDisplayHeight);
        double displayBottom = Math.Clamp(displayTop + (viewportHeight / _currentOverviewVerticalScale), 0d, _currentDisplayHeight);
        SetDepthOverlayText(
            OverviewTopDepthTextBlock,
            OverviewBottomDepthTextBlock,
            warp.Inverse(displayTop),
            warp.Inverse(displayBottom));
    }

    private void SetDepthOverlayText(TextBlock topTextBlock, TextBlock bottomTextBlock, double topSourceY, double bottomSourceY)
    {
        if (_depthMapper is null)
        {
            topTextBlock.Text = string.Empty;
            bottomTextBlock.Text = string.Empty;
            return;
        }

        DepthInfo topInfo = _depthMapper.GetDepthInfoAtSourceY(topSourceY);
        DepthInfo bottomInfo = _depthMapper.GetDepthInfoAtSourceY(bottomSourceY);
        string unitLabel = DepthDisplayConverter.GetUnitLabel(_displayDepthUnit);
        topTextBlock.Text = $"Top {ConvertDepth(topInfo.Depth):0.000} {unitLabel}";
        bottomTextBlock.Text = $"Bottom {ConvertDepth(bottomInfo.Depth):0.000} {unitLabel}";
    }

    private void ClearDepthOverlays()
    {
        ViewerTopDepthTextBlock.Text = string.Empty;
        ViewerBottomDepthTextBlock.Text = string.Empty;
        OverviewTopDepthTextBlock.Text = string.Empty;
        OverviewBottomDepthTextBlock.Text = string.Empty;
    }

    private void UpdateOverviewScrollBar()
    {
        if (OverviewVerticalScrollBar is null)
        {
            return;
        }

        if (_document is null)
        {
            OverviewVerticalScrollBar.Visibility = Visibility.Collapsed;
            return;
        }

        double overviewExtentHeight = GetOverviewExtentHeight();
        double overviewViewportHeight = GetOverviewViewportHeight();
        double verticalMaximum = Math.Max(0d, overviewExtentHeight - overviewViewportHeight);
        bool showVertical = verticalMaximum > 0.5d;

        _suppressExternalScrollbarChange = true;
        try
        {
            OverviewVerticalScrollBar.Visibility = showVertical ? Visibility.Visible : Visibility.Collapsed;
            OverviewVerticalScrollBar.Maximum = verticalMaximum;
            OverviewVerticalScrollBar.ViewportSize = Math.Max(0d, overviewViewportHeight);
            OverviewVerticalScrollBar.SmallChange = 32d;
            OverviewVerticalScrollBar.LargeChange = Math.Max(48d, overviewViewportHeight * 0.9d);
            OverviewVerticalScrollBar.Value = ClampFinite(GetOverviewVerticalOffset(), 0d, verticalMaximum);
        }
        finally
        {
            _suppressExternalScrollbarChange = false;
        }
    }

    private void UpdateSelectionSummary()
    {
        if (_document is null || _depthMapper is null || !TryGetSelection(out double startSourceY, out double endSourceY))
        {
            SelectionSummaryTextBlock.Text = DefaultSelectionText;
            return;
        }

        DepthInfo startInfo = _depthMapper.GetDepthInfoAtSourceY(startSourceY);
        DepthInfo endInfo = _depthMapper.GetDepthInfoAtSourceY(endSourceY);
        string depthUnit = DepthDisplayConverter.GetUnitLabel(_displayDepthUnit);
        double convertedStartDepth = ConvertDepth(startInfo.Depth);
        double convertedEndDepth = ConvertDepth(endInfo.Depth);
        SelectionSummaryTextBlock.Text =
            $"Source Y {startSourceY:0.0} to {endSourceY:0.0}\n" +
            $"Depth {convertedStartDepth:0.000} {depthUnit} to {convertedEndDepth:0.000} {depthUnit}\n" +
            "Wrap Angle 0.0 deg to 360.0 deg\n" +
            $"File Orientation {startInfo.FileOrientation:0.00} deg to {endInfo.FileOrientation:0.00} deg";
    }

    private void UpdateRegionList()
    {
        Guid? selectedId = (RegionsListView.SelectedItem as RegionListItem)?.Id;
        List<RegionListItem> items = _session.Regions
            .OrderBy(region => region.NormalizedStart)
            .Select(region => new RegionListItem(
                region.Id,
                region.Name,
                $"{region.NormalizedStart:0.0} - {region.NormalizedEnd:0.0}",
                BuildRegionSummary(region),
                region.IsEnabled ? "On" : "Off"))
            .ToList();

        _suppressRegionSelectionNavigation = true;
        try
        {
            RegionsListView.ItemsSource = items;
            if (selectedId.HasValue)
            {
                RegionsListView.SelectedItem = items.FirstOrDefault(item => item.Id == selectedId.Value);
            }
        }
        finally
        {
            _suppressRegionSelectionNavigation = false;
        }
    }

    private void ResetEditSession()
    {
        _session.GlobalVerticalScale = 1d;
        _session.GlobalTone = ToneAdjustment.Identity;
        _nextRegionNumber = 1;
        foreach (EditRegion region in _session.Regions.ToArray())
        {
            _session.RemoveRegion(region.Id);
        }

        GlobalScaleTextBox.Text = "1.000";
        SelectionScaleTextBox.Text = "1.000";
        ResetToneControls();
        ClearSelection();
        UpdateRegionList();
    }

    private void ResetToneControls()
    {
        _suppressTonePreview = true;
        try
        {
            GlobalBrightnessSlider.Value = 0d;
            GlobalContrastSlider.Value = 0d;
            GlobalSharpnessSlider.Value = 0d;
            GlobalNormalizeCheckBox.IsChecked = false;
            SelectionBrightnessSlider.Value = 0d;
            SelectionContrastSlider.Value = 0d;
            SelectionSharpnessSlider.Value = 0d;
            SelectionNormalizeCheckBox.IsChecked = false;
        }
        finally
        {
            _suppressTonePreview = false;
        }

        UpdateToneValueLabels();
    }

    private void ResetUnitSelectors()
    {
        _sourceDepthUnit = SourceDepthUnit.Meters;
        _displayDepthUnit = DisplayDepthUnit.Metric;
        UpdateUnitToggleButtons();
        ApplyUnitSelectionsFromControls();
    }

    private void ClearSelection()
    {
        _selectionStartSourceY = null;
        _selectionEndSourceY = null;
        UpdateSelectionSummary();
        UpdateSelectionVisuals();
    }

    private bool TryApplyGlobalControls()
    {
        if (!TryParseDouble(GlobalScaleTextBox.Text, out double scale))
        {
            MessageBox.Show(this, "Global scale must be numeric.", "Invalid scale", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _session.GlobalVerticalScale = scale;
        _session.GlobalTone = ReadToneFromControls(ToneTarget.Global);
        return true;
    }

    private bool TryGetSelection(out double selectionStart, out double selectionEnd)
    {
        selectionStart = 0d;
        selectionEnd = 0d;
        if (!_selectionStartSourceY.HasValue || !_selectionEndSourceY.HasValue)
        {
            return false;
        }

        selectionStart = Math.Min(_selectionStartSourceY.Value, _selectionEndSourceY.Value);
        selectionEnd = Math.Max(_selectionStartSourceY.Value, _selectionEndSourceY.Value);
        if (selectionEnd - selectionStart < 0.5d)
        {
            selectionEnd = selectionStart + 0.5d;
        }

        return true;
    }

    private EditRegion? FindMatchingRegion(double startSourceY, double endSourceY)
    {
        return _session.Regions.FirstOrDefault(region =>
            Math.Abs(region.NormalizedStart - startSourceY) < 0.5d &&
            Math.Abs(region.NormalizedEnd - endSourceY) < 0.5d);
    }

    private async Task ApplyToneFromControlsAsync(ToneTarget target, bool refreshOverview, bool showErrors)
    {
        if (_document is null)
        {
            return;
        }

        if (target == ToneTarget.Global)
        {
            _session.GlobalTone = ReadToneFromControls(ToneTarget.Global);
        }
        else
        {
            if (!TryGetSelection(out double selectionStart, out double selectionEnd))
            {
                if (showErrors)
                {
                    MessageBox.Show(this, "Create a selection first.", "Missing selection", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            ToneAdjustment adjustment = ReadToneFromControls(ToneTarget.Selection);
            EditRegion region = FindMatchingRegion(selectionStart, selectionEnd) ??
                new(Guid.NewGuid(), NextRegionName(), selectionStart, selectionEnd, RegionGeometryMode.None, null, adjustment);
            _session.UpsertRegion(region.WithTone(adjustment));
        }

        if (refreshOverview)
        {
            await RefreshAllRendersAsync(resetScrollOffset: false, refreshOverview: true);
        }
        else
        {
            UpdateRegionList();
            await RefreshViewerAsync(resetScrollOffset: false);
        }
    }

    private ToneAdjustment ReadToneFromControls(ToneTarget target)
    {
        return target == ToneTarget.Global
            ? new ToneAdjustment(
                GlobalBrightnessSlider.Value,
                GlobalContrastSlider.Value,
                GlobalSharpnessSlider.Value,
                GlobalNormalizeCheckBox.IsChecked == true)
            : new ToneAdjustment(
                SelectionBrightnessSlider.Value,
                SelectionContrastSlider.Value,
                SelectionSharpnessSlider.Value,
                SelectionNormalizeCheckBox.IsChecked == true);
    }

    private async Task CommitTonePreviewAsync(ToneTarget target)
    {
        if (_pendingTonePreviewTarget != target)
        {
            _pendingTonePreviewTarget = target;
        }

        _tonePreviewTimer?.Stop();
        await ApplyToneFromControlsAsync(target, refreshOverview: true, showErrors: false);
    }

    private void RestartTonePreviewTimer()
    {
        _tonePreviewTimer?.Stop();
        _tonePreviewTimer?.Start();
    }

    private void UpdateToneValueLabels()
    {
        GlobalBrightnessValueTextBlock.Text = $"{GlobalBrightnessSlider.Value:0}";
        GlobalContrastValueTextBlock.Text = $"{GlobalContrastSlider.Value:0}";
        GlobalSharpnessValueTextBlock.Text = $"{GlobalSharpnessSlider.Value:0}";
        SelectionBrightnessValueTextBlock.Text = $"{SelectionBrightnessSlider.Value:0}";
        SelectionContrastValueTextBlock.Text = $"{SelectionContrastSlider.Value:0}";
        SelectionSharpnessValueTextBlock.Text = $"{SelectionSharpnessSlider.Value:0}";
    }

    private void NormalizeUnitToggleStates(FrameworkElement sender)
    {
        if (sender == SourceMetersToggleButton || sender == SourceFeetToggleButton)
        {
            bool useFeet = sender == SourceFeetToggleButton;
            SourceFeetToggleButton.IsChecked = useFeet;
            SourceMetersToggleButton.IsChecked = !useFeet;
        }
        else if (sender == DisplayMetricToggleButton || sender == DisplayImperialToggleButton)
        {
            bool useImperial = sender == DisplayImperialToggleButton;
            DisplayImperialToggleButton.IsChecked = useImperial;
            DisplayMetricToggleButton.IsChecked = !useImperial;
        }
    }

    private void UpdateUnitToggleButtons()
    {
        if (SourceMetersToggleButton is null ||
            SourceFeetToggleButton is null ||
            DisplayMetricToggleButton is null ||
            DisplayImperialToggleButton is null)
        {
            return;
        }

        SourceMetersToggleButton.IsChecked = _sourceDepthUnit == SourceDepthUnit.Meters;
        SourceFeetToggleButton.IsChecked = _sourceDepthUnit == SourceDepthUnit.Feet;
        DisplayMetricToggleButton.IsChecked = _displayDepthUnit == DisplayDepthUnit.Metric;
        DisplayImperialToggleButton.IsChecked = _displayDepthUnit == DisplayDepthUnit.Imperial;
    }

    private void ApplyUnitSelectionsFromControls()
    {
        if (SourceMetersToggleButton is null ||
            SourceFeetToggleButton is null ||
            DisplayMetricToggleButton is null ||
            DisplayImperialToggleButton is null)
        {
            return;
        }

        _sourceDepthUnit = SourceFeetToggleButton.IsChecked == true ? SourceDepthUnit.Feet : SourceDepthUnit.Meters;
        _displayDepthUnit = DisplayImperialToggleButton.IsChecked == true ? DisplayDepthUnit.Imperial : DisplayDepthUnit.Metric;
    }

    private WarpMap GetCurrentWarp()
    {
        if (_document is null)
        {
            throw new InvalidOperationException("No CWS document is loaded.");
        }

        _currentWarp = _session.BuildWarpMap(_document.SourceHeight);
        _currentDisplayHeight = _currentWarp.TotalDisplayHeight;
        return _currentWarp;
    }

    private void ApplyPendingZoomAnchorIfNeeded(double zoom)
    {
        if (!_pendingZoomAnchorViewportPoint.HasValue || !_pendingZoomAnchorDisplayX.HasValue || !_pendingZoomAnchorDisplayY.HasValue)
        {
            return;
        }

        Point viewportPoint = _pendingZoomAnchorViewportPoint.Value;
        double targetHorizontalOffset = (_pendingZoomAnchorDisplayX.Value * zoom) - viewportPoint.X;
        double targetVerticalOffset = (_pendingZoomAnchorDisplayY.Value * zoom) - viewportPoint.Y;
        double maxHorizontalOffset = Math.Max(0d, GetViewerExtentWidth() - GetViewerViewportWidth());
        double maxVerticalOffset = Math.Max(0d, GetViewerExtentHeight() - GetViewerViewportHeight());

        ExecuteWithoutViewerScrollRefresh(() =>
        {
            ViewerScrollViewer.ScrollToHorizontalOffset(Math.Clamp(targetHorizontalOffset, 0d, maxHorizontalOffset));
            ViewerScrollViewer.ScrollToVerticalOffset(Math.Clamp(targetVerticalOffset, 0d, maxVerticalOffset));
        });

        _pendingZoomAnchorViewportPoint = null;
        _pendingZoomAnchorDisplayX = null;
        _pendingZoomAnchorDisplayY = null;
    }

    private void ApplyPendingOverviewZoomAnchorIfNeeded()
    {
        if (!_pendingOverviewZoomAnchorViewportY.HasValue || !_pendingOverviewZoomAnchorDisplayY.HasValue || _currentOverviewVerticalScale <= 0d)
        {
            return;
        }

        double targetOffset = (_pendingOverviewZoomAnchorDisplayY.Value * _currentOverviewVerticalScale) - _pendingOverviewZoomAnchorViewportY.Value;
        double maxVerticalOffset = Math.Max(0d, GetOverviewExtentHeight() - GetOverviewViewportHeight());
        OverviewScrollViewer.ScrollToVerticalOffset(Math.Clamp(targetOffset, 0d, maxVerticalOffset));
        _pendingOverviewZoomAnchorViewportY = null;
        _pendingOverviewZoomAnchorDisplayY = null;
    }

    private void ExecuteWithoutViewerScrollRefresh(Action action)
    {
        _suppressViewerScrollRefresh = true;
        try
        {
            action();
        }
        finally
        {
            _suppressViewerScrollRefresh = false;
        }
    }

    private ToneTarget GetToneTargetFromControl(DependencyObject control)
    {
        return control == SelectionBrightnessSlider ||
               control == SelectionContrastSlider ||
               control == SelectionSharpnessSlider ||
               control == SelectionNormalizeCheckBox
            ? ToneTarget.Selection
            : ToneTarget.Global;
    }

    private async Task NavigateToRegionAsync(EditRegion region)
    {
        if (_document is null)
        {
            return;
        }

        const double targetZoom = 0.4d;
        if (Math.Abs(ZoomSlider.Value - targetZoom) > 0.0001d)
        {
            ZoomSlider.Value = targetZoom;
        }

        await RefreshViewerAsync(resetScrollOffset: false);

        WarpMap warp = GetCurrentWarp();
        double displayCenter = (warp.Forward(region.NormalizedStart) + warp.Forward(region.NormalizedEnd)) / 2d;
        double viewerViewportHeight = GetViewerViewportHeight();
        double maxVerticalOffset = Math.Max(0d, GetViewerExtentHeight() - viewerViewportHeight);
        double targetOffset = (displayCenter * targetZoom) - (viewerViewportHeight / 2d);
        ExecuteWithoutViewerScrollRefresh(() =>
        {
            ViewerScrollViewer.ScrollToVerticalOffset(Math.Clamp(targetOffset, 0d, maxVerticalOffset));
        });

        UpdateSelectionVisuals();
        await RefreshViewerAsync(resetScrollOffset: false);
    }

    private double ConvertDepth(double rawDepth) =>
        DepthDisplayConverter.ConvertDepth(rawDepth, _sourceDepthUnit, _displayDepthUnit);

    private string BuildHoverInfo(DepthInfo info, double wrapAngle)
    {
        double convertedDepth = ConvertDepth(info.Depth);
        string unitLabel = DepthDisplayConverter.GetUnitLabel(_displayDepthUnit);
        return $"{convertedDepth:0.000} {unitLabel} | wrap {wrapAngle:0.0} deg | file {info.FileOrientation:0.00} deg | {info.TimestampUtc:HH:mm:ss.fff}";
    }

    private void RefreshHoverInfoFromCanvasPoint(Point point)
    {
        if (_document is null || _depthMapper is null)
        {
            HoverInfoTextBlock.Text = DefaultHoverText;
            return;
        }

        double zoom = Math.Max(0.1d, ZoomSlider.Value);
        WarpMap warp = GetCurrentWarp();
        double displayY = Math.Clamp(point.Y / zoom, 0d, _currentDisplayHeight);
        double sourceY = warp.Inverse(displayY);
        double wrapAngle = Math.Clamp(point.X / Math.Max(1d, ViewerCanvas.Width), 0d, 1d) * 360d;
        DepthInfo info = _depthMapper.GetDepthInfoAtSourceY(sourceY);
        _currentHoverState = new HoverState(point, info, wrapAngle);
        HoverInfoTextBlock.Text = BuildHoverInfo(info, wrapAngle);
    }

    private void RefreshHoverInfoFromCurrentPointer()
    {
        if (_document is null || _depthMapper is null)
        {
            HoverInfoTextBlock.Text = DefaultHoverText;
            return;
        }

        if (ViewerCanvas.IsMouseOver)
        {
            Point point = Mouse.GetPosition(ViewerCanvas);
            if (point.X >= 0d &&
                point.Y >= 0d &&
                point.X <= ViewerCanvas.Width &&
                point.Y <= ViewerCanvas.Height)
            {
                RefreshHoverInfoFromCanvasPoint(point);
                return;
            }
        }

        HoverInfoTextBlock.Text = _currentHoverState is null
            ? DefaultHoverText
            : BuildHoverInfo(_currentHoverState.Info, _currentHoverState.WrapAngle);
    }

    private void CenterViewerOnOverviewPoint(double overviewY)
    {
        if (_currentOverviewVerticalScale <= 0d)
        {
            return;
        }

        double zoom = Math.Max(0.1d, ZoomSlider.Value);
        double targetDisplayCenter = Math.Clamp(overviewY / _currentOverviewVerticalScale, 0d, _currentDisplayHeight);
        double viewerViewportHeight = GetViewerViewportHeight();
        double targetDisplayStart = targetDisplayCenter - (viewerViewportHeight / zoom / 2d);
        double maxVerticalOffset = Math.Max(0d, GetViewerExtentHeight() - viewerViewportHeight);
        ViewerScrollViewer.ScrollToVerticalOffset(Math.Clamp(targetDisplayStart * zoom, 0d, maxVerticalOffset));
        UpdateOverviewViewportVisual();
    }

    private void DragOverviewViewport(double currentOverviewY)
    {
        if (_currentOverviewVerticalScale <= 0d)
        {
            return;
        }

        double zoom = Math.Max(0.1d, ZoomSlider.Value);
        double deltaOverviewY = currentOverviewY - _selectionDragStartPoint.Y;
        double targetVerticalOffset = _overviewViewportDragStartVerticalOffset + ((deltaOverviewY / _currentOverviewVerticalScale) * zoom);
        double maxVerticalOffset = Math.Max(0d, GetViewerExtentHeight() - GetViewerViewportHeight());
        ViewerScrollViewer.ScrollToVerticalOffset(Math.Clamp(targetVerticalOffset, 0d, maxVerticalOffset));
        UpdateOverviewViewportVisual();
    }

    private double GetViewerViewportHeight() => Math.Max(1d, CoerceNonNegativeFinite(ViewerScrollViewer.ViewportHeight, ViewerScrollViewer.ActualHeight));

    private double GetViewerViewportWidth() => Math.Max(1d, CoerceNonNegativeFinite(ViewerScrollViewer.ViewportWidth, ViewerScrollViewer.ActualWidth));

    private double GetOverviewViewportHeight() => Math.Max(1d, CoerceNonNegativeFinite(OverviewScrollViewer.ViewportHeight, OverviewScrollViewer.ActualHeight));

    private double GetOverviewViewportWidth() => Math.Max(1d, CoerceNonNegativeFinite(OverviewScrollViewer.ViewportWidth, OverviewScrollViewer.ActualWidth));

    private double GetViewerExtentHeight() => CoerceNonNegativeFinite(ViewerCanvas.Height, ViewerCanvas.ActualHeight);

    private double GetViewerExtentWidth() => CoerceNonNegativeFinite(ViewerCanvas.Width, ViewerCanvas.ActualWidth);

    private double GetOverviewExtentHeight() => CoerceNonNegativeFinite(OverviewCanvas.Height, OverviewCanvas.ActualHeight);

    private double GetViewerVerticalOffset() => CoerceNonNegativeFinite(ViewerScrollViewer.VerticalOffset);

    private double GetViewerHorizontalOffset() => CoerceNonNegativeFinite(ViewerScrollViewer.HorizontalOffset);

    private double GetOverviewVerticalOffset() => CoerceNonNegativeFinite(OverviewScrollViewer.VerticalOffset);

    private static double CoerceNonNegativeFinite(double value, double fallback = 0d)
    {
        double finite = double.IsFinite(value) ? value : fallback;
        if (!double.IsFinite(fallback))
        {
            finite = double.IsFinite(value) ? value : 0d;
        }

        return Math.Max(0d, finite);
    }

    private static double ClampFinite(double value, double min, double max)
    {
        double safeMin = double.IsFinite(min) ? min : 0d;
        double safeMax = double.IsFinite(max) ? Math.Max(safeMin, max) : safeMin;
        double safeValue = double.IsFinite(value) ? value : safeMin;
        return Math.Clamp(safeValue, safeMin, safeMax);
    }

    private bool IsPointInsideOverviewViewport(Point point)
    {
        if (OverviewViewportRect.Visibility != Visibility.Visible)
        {
            return false;
        }

        double top = Canvas.GetTop(OverviewViewportRect);
        double left = Canvas.GetLeft(OverviewViewportRect);
        return point.X >= left &&
               point.X <= left + OverviewViewportRect.Width &&
               point.Y >= top &&
               point.Y <= top + OverviewViewportRect.Height;
    }

    private void UpdateExportProgress(SaveProgress report)
    {
        double percent = report.Total <= 0 ? 0d : (report.Completed / (double)report.Total) * 100d;
        ExportProgressBar.Visibility = Visibility.Visible;
        ExportProgressTextBlock.Visibility = Visibility.Visible;
        ExportProgressBar.Value = Math.Clamp(percent, 0d, 100d);
        ExportProgressTextBlock.Text = $"{percent:0}% - {report.Stage}";
        SetStatus($"{report.Stage} ({report.Completed}/{report.Total})");
    }

    private void ResetExportProgress()
    {
        if (ExportProgressBar is null || ExportProgressTextBlock is null)
        {
            return;
        }

        ExportProgressBar.Value = 0d;
        ExportProgressBar.Visibility = Visibility.Collapsed;
        ExportProgressTextBlock.Text = string.Empty;
        ExportProgressTextBlock.Visibility = Visibility.Collapsed;
    }

    private string NextRegionName()
    {
        string name = $"Region {_nextRegionNumber}";
        _nextRegionNumber++;
        return name;
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);

    private static BitmapSource ToBitmapSource(MagickImage image)
    {
        byte[] data = image.ToByteArray(MagickFormat.Png);
        using MemoryStream stream = new(data);
        BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    private static string SuggestOutputPath(string sourcePath)
    {
        string directory = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, $"{fileNameWithoutExtension}_edited.cws");
    }

    private static string BuildRegionSummary(EditRegion region)
    {
        List<string> parts = [];
        if (!region.IsEnabled)
        {
            parts.Add("Disabled");
        }

        if (region.IsCrop)
        {
            parts.Add("Crop");
        }
        else if (region.HasScaleEdit)
        {
            parts.Add($"Scale {region.VerticalScale!.Value:0.###}");
        }

        if (!region.Tone.IsIdentity)
        {
            parts.Add($"Tone B{region.Tone.Brightness:0.#} C{region.Tone.Contrast:0.#} S{region.Tone.Sharpness:0.#}{(region.Tone.NormalizeEnabled ? " N" : string.Empty)}");
        }

        return parts.Count == 0 ? "No edits" : string.Join(" | ", parts);
    }

    private void SetBusyUi(bool isBusy)
    {
        OpenButton.IsEnabled = !isBusy;
        SaveButton.IsEnabled = !isBusy;
        BrowseOutputButton.IsEnabled = !isBusy;
        ResetButton.IsEnabled = !isBusy;
        SourceMetersToggleButton.IsEnabled = !isBusy;
        SourceFeetToggleButton.IsEnabled = !isBusy;
        DisplayMetricToggleButton.IsEnabled = !isBusy;
        DisplayImperialToggleButton.IsEnabled = !isBusy;
        OverviewZoomSlider.IsEnabled = !isBusy;
    }

    private void SetStatus(string message) => StatusTextBlock.Text = message;

    private sealed record RegionListItem(Guid Id, string Name, string Interval, string Summary, string ToggleLabel);

    private sealed record HoverState(Point CanvasPoint, DepthInfo Info, double WrapAngle);

    private enum ToneTarget
    {
        Global,
        Selection,
    }
}
