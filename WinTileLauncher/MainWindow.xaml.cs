using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinTileLauncher.Controls;
using WinTileLauncher.Models;
using WinTileLauncher.Services;
using DrawingColor = System.Drawing.Color;
using WinForms = System.Windows.Forms;

namespace WinTileLauncher;

public partial class MainWindow : Window, IDisposable
{
    private readonly ObservableCollection<TileLayoutItem> _tiles = [];
    private readonly AppDiscoveryService _discoveryService = new();
    private readonly LayoutService _layoutService = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _liveTileTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly ICollectionView _tileView;

    private GlobalKeyboardHook? _keyboardHook;
    private List<LauncherItem> _apps = [];
    private readonly ConcurrentDictionary<string, byte> _appIconLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _appIconLoadGate = new(3, 3);
    private ICollectionView? _appsView;
    private Point _dragStart;
    private Point _dragPointerOffset;
    private TileLayoutItem? _pressedTile;
    private Border? _pressedTileBorder;
    private Border? _pointerDropBorder;
    private string? _pointerDropGroup;
    private int _pointerDropGroupColumn;
    private int _pointerDropColumn;
    private int _pointerDropRow;
    private bool _pointerCreatesGroup;
    private bool _isPointerDragging;
    private bool _mouseMoved;
    private bool _isResizing;
    private Dictionary<string, Point>? _resizeStartPositions;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private Point _resizeStartPointer;
    private double _resizeStartCanvasZoom = 1;
    private string? _resizingGroup;
    private int _groupResizeStartColumns;
    private int _groupResizeStartRows;
    private Point _groupResizeStartPointer;
    private double _groupResizeStartCanvasZoom = 1;
    private bool _transientUiOpen;
    private bool _allowClose;
    private DateTime _suppressTileLaunchUntil;
    private DateTime _ignoreDeactivationUntil;
    private int _transitionVersion;
    private bool _isHiding;
    private bool _windowedTestMode;
    private double _canvasZoom = 1;
    private TileLayoutItem? _pendingUnpinnedTile;
    private int _pendingUnpinnedIndex = -1;

    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    public MainWindow()
    {
        InitializeComponent();

        _tileView = new ListCollectionView(_tiles);
        _tileView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TileLayoutItem.Group)));
        TilesItems.ItemsSource = _tileView;

        UserNameText.Text = Environment.UserName;
        StartWithWindowsCheckBox.IsChecked = StartupService.IsEnabled();
        UpdateZoomText();
        UpdateClock();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        _liveTileTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _liveTileTimer.Tick += (_, _) => UpdateLiveTiles();
        _liveTileTimer.Start();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) =>
        {
            StatusText.Text = string.Empty;
            StatusToast.Visibility = Visibility.Collapsed;
            ClearPendingUnpin();
            _statusTimer.Stop();
        };
    }

    public void InitializeLauncher(bool processInjectedInput = false)
    {
        _keyboardHook = new GlobalKeyboardHook(() =>
            Dispatcher.BeginInvoke(DispatcherPriority.Send, ToggleLauncher),
            processInjectedInput);

        try
        {
            _keyboardHook.Install();
        }
        catch (Win32Exception ex)
        {
            SetStatus($"Windows 键监听失败：{ex.Message}");
        }

        _ = LoadAppsAndLayoutAsync();
    }

    public void EnableWindowedTestMode()
    {
        _windowedTestMode = true;
        ShowInTaskbar = true;
        Topmost = false;
        Width = 1280;
        Height = 800;
        Left = 80;
        Top = 80;
    }

    public void ShowInitial() => ShowLauncher();

    private async Task LoadAppsAndLayoutAsync()
    {
        var apps = await _discoveryService.DiscoverAsync();
        await Dispatcher.InvokeAsync(() =>
        {
            _apps = apps;
            _appsView = new ListCollectionView(_apps) { Filter = FilterApp };
            _appsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LauncherItem.SortLetter)));
            AppsList.ItemsSource = _appsView;
            AppsCountText.Text = $"{_apps.Count} 个应用";

            var savedLayout = _layoutService.Load();
            SetCanvasZoom(savedLayout?.CanvasZoom ?? 1);
            var rawTiles = ShouldSeedDefaultLayout(savedLayout)
                ? CreateDefaultLayout(_apps)
                : savedLayout!.Tiles;

            if (ShouldAddLegacyClock(savedLayout, rawTiles))
                rawTiles.Add(CreateClockTile(rawTiles.Count));

            if (savedLayout is { Version: < 11 })
                LayoutRules.NormalizeLegacyGridExtents(rawTiles);

            var tiles = LayoutRules.Normalize(rawTiles);

            _tiles.Clear();
            foreach (var tile in tiles.OrderBy(tile => tile.Order))
            {
                HydrateTile(tile);
                _tiles.Add(tile);
            }

            UpdateLiveTiles();
            UpdatePinnedAppStates();
            RenumberAndSave();
            LoadingPanel.Visibility = Visibility.Collapsed;
            UpdatePinnedEmptyState();
            UpdateSearchEmptyState();
            SetStatus($"已找到 {_apps.Count} 个应用");
        });
    }

    internal static bool ShouldSeedDefaultLayout(LayoutData? savedLayout) => savedLayout is null;

    internal static bool ShouldAddLegacyClock(LayoutData? savedLayout,
        IReadOnlyCollection<TileLayoutItem> tiles) =>
        savedLayout is { Version: < 2 } && tiles.Count > 0 &&
        tiles.All(tile => tile.Kind != TileKind.Clock);

    private static List<TileLayoutItem> CreateDefaultLayout(IReadOnlyList<LauncherItem> apps)
    {
        var preferredTargets = new[]
        {
            "explorer.exe", "ms-settings:", "wt.exe", "calc.exe", "notepad.exe", "ms-windows-store:"
        };
        var preferredNames = new[]
        {
            "Edge", "Chrome", "Firefox", "微信", "WeChat", "QQ", "Steam", "Spotify", "网易云"
        };

        var selected = new List<LauncherItem>();
        foreach (var target in preferredTargets)
        {
            var match = apps.FirstOrDefault(app => app.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                selected.Add(match);
        }

        foreach (var name in preferredNames)
        {
            var match = apps.FirstOrDefault(app => app.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null && selected.All(item => !item.Target.Equals(match.Target, StringComparison.OrdinalIgnoreCase)))
                selected.Add(match);
        }

        foreach (var app in apps.Where(app => Path.GetExtension(app.Target).Equals(".lnk", StringComparison.OrdinalIgnoreCase)))
        {
            if (selected.Count >= 13)
                break;
            if (selected.All(item => !item.Target.Equals(app.Target, StringComparison.OrdinalIgnoreCase)))
                selected.Add(app);
        }

        var layout = selected.Select((app, index) => new TileLayoutItem
        {
            Id = app.Id,
            Name = app.Name,
            Target = app.Target,
            Arguments = app.Arguments,
            Glyph = app.Glyph,
            Accent = app.Accent,
            Icon = app.Icon,
            Group = index < 6 ? "系统工具" : "常用应用",
            VisualStyle = TileVisualStyle.Glass,
            Size = index switch
            {
                0 => TileSize.Wide,
                2 => TileSize.Wide,
                5 => TileSize.Wide,
                3 => TileSize.Small,
                _ => TileSize.Medium
            },
            Order = index
        }).ToList();
        layout.Add(CreateClockTile(layout.Count));
        return layout;
    }

    private static TileLayoutItem CreateClockTile(int order) => new()
    {
        Id = "TILE10_CLOCK",
        Name = "时钟",
        Group = "生活动态",
        Size = TileSize.Wide,
        Kind = TileKind.Clock,
        VisualStyle = TileVisualStyle.Glass,
        Accent = "#0063B1",
        Glyph = "\uE823",
        Order = order
    };

    private void HydrateTile(TileLayoutItem tile)
    {
        if (tile.Kind != TileKind.App)
            return;
        var matchingApp = _apps.FirstOrDefault(app => SameTarget(app.Target, app.Arguments, tile.Target, tile.Arguments));
        tile.Icon = matchingApp?.Icon ?? IconService.GetLauncherIcon(tile.Target, tile.Arguments);
    }

    private static bool SameTarget(string firstTarget, string firstArguments, string secondTarget, string secondArguments) =>
        firstTarget.Equals(secondTarget, StringComparison.OrdinalIgnoreCase) &&
        firstArguments.Equals(secondArguments, StringComparison.OrdinalIgnoreCase);

    private void UpdateLiveTiles()
    {
        var now = DateTime.Now;
        foreach (var tile in _tiles.Where(tile => tile.Kind == TileKind.Clock && tile.IsLiveEnabled))
        {
            tile.LivePrimaryText = now.ToString("HH:mm");
            tile.LiveSecondaryText = now.ToString("M月d日 dddd");
        }
    }

    private void ToggleLauncher()
    {
        if (IsVisible && !_isHiding)
            HideLauncher();
        else
            ShowLauncher();
    }

    private void ShowLauncher()
    {
        _transitionVersion++;
        _isHiding = false;
        ClearLauncherAnimations();
        _ignoreDeactivationUntil = DateTime.UtcNow.AddSeconds(2);
        SearchBox.Clear();
        ShowPinnedTiles(false);
        WindowState = WindowState.Normal;
        if (!_windowedTestMode)
            NativeWindow.PlaceOnCursorMonitor(this);
        if (!IsVisible)
            Show();
        Activate();
        Focus();
        Keyboard.Focus(this);

        TilesItems.UpdateLayout();
        if (AnimationsEnabled)
            AnimateTilesIn();

        LauncherRoot.Opacity = 1;
        LauncherScale.ScaleX = 1;
        LauncherScale.ScaleY = 1;
        LauncherTranslate.Y = 0;

        if (!AnimationsEnabled)
            return;

        var rootDuration = TimeSpan.FromMilliseconds(460);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        LauncherScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            CreateAnimation(0.972, 1, rootDuration, ease));
        LauncherScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            CreateAnimation(0.972, 1, rootDuration, ease));
        LauncherTranslate.BeginAnimation(TranslateTransform.YProperty,
            CreateAnimation(32, 0, rootDuration, ease));
    }

    private async void HideLauncher()
    {
        if (!IsVisible || _isHiding)
            return;

        _isHiding = true;
        var transition = ++_transitionVersion;
        PowerPopup.IsOpen = false;
        AppsPanel.Visibility = Visibility.Collapsed;
        SearchHost.Visibility = Visibility.Collapsed;
        SearchBox.Clear();
        ClearTileEntryAnimations();

        if (!AnimationsEnabled)
        {
            Hide();
            ClearLauncherAnimations();
            LauncherRoot.Opacity = 1;
            LauncherScale.ScaleX = 1;
            LauncherScale.ScaleY = 1;
            LauncherTranslate.Y = 0;
            _isHiding = false;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(135);
        LauncherRoot.BeginAnimation(OpacityProperty, CreateAnimation(1, 0, duration, ease, true));
        LauncherScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            CreateAnimation(1, 0.992, duration, ease, true));
        LauncherScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            CreateAnimation(1, 0.992, duration, ease, true));
        LauncherTranslate.BeginAnimation(TranslateTransform.YProperty,
            CreateAnimation(0, 12, duration, ease, true));

        await Task.Delay(duration + TimeSpan.FromMilliseconds(15));
        if (transition != _transitionVersion)
            return;

        Hide();
        ClearLauncherAnimations();
        LauncherRoot.Opacity = 1;
        LauncherScale.ScaleX = 1;
        LauncherScale.ScaleY = 1;
        LauncherTranslate.Y = 0;
        _isHiding = false;
    }

    private static DoubleAnimation CreateAnimation(double from, double to, TimeSpan duration,
        IEasingFunction easing, bool holdEnd = false, TimeSpan? delay = null) => new(from, to, duration)
    {
        BeginTime = delay ?? TimeSpan.Zero,
        EasingFunction = easing,
        FillBehavior = holdEnd ? FillBehavior.HoldEnd : FillBehavior.Stop
    };

    private static DoubleAnimationUsingKeyFrames CreateDelayedEntryAnimation(double from, double to,
        TimeSpan delay, TimeSpan duration, IEasingFunction easing)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + duration))
        {
            EasingFunction = easing
        });
        return animation;
    }

    private void ClearLauncherAnimations()
    {
        LauncherRoot.BeginAnimation(OpacityProperty, null);
        LauncherScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        LauncherScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LauncherTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ClearTileEntryAnimations();
    }

    private void ClearTileEntryAnimations()
    {
        foreach (var border in FindVisualChildren<Border>(TilesItems)
                     .Where(border => border.Tag is TileLayoutItem))
        {
            border.BeginAnimation(OpacityProperty, null);
            border.Opacity = 1;
            if (border.Child is not Grid body || body.RenderTransform is not TranslateTransform translate)
                continue;
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
        }
    }

    private void AnimateTilesIn()
    {
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var tileBorders = FindVisualChildren<Border>(TilesItems)
            .Where(border => border.Tag is TileLayoutItem)
            .OrderBy(border => ((TileLayoutItem)border.Tag).Order)
            .ToList();

        for (var index = 0; index < tileBorders.Count; index++)
        {
            var border = tileBorders[index];
            var delay = TimeSpan.FromMilliseconds(Math.Min(index, 14) * 14 + 30);
            border.Opacity = 1;
            border.BeginAnimation(OpacityProperty,
                CreateDelayedEntryAnimation(0, 1, delay, TimeSpan.FromMilliseconds(280), ease));

            if (border.Child is Grid body)
            {
                var translate = body.RenderTransform as TranslateTransform ?? new TranslateTransform();
                body.RenderTransform = translate;
                translate.Y = 0;
                translate.BeginAnimation(TranslateTransform.YProperty,
                    CreateDelayedEntryAnimation(22, 0, delay, TimeSpan.FromMilliseconds(380), ease));
            }
        }
    }

    private static void AnimateViewIn(FrameworkElement element, double offset = 14)
    {
        if (!AnimationsEnabled)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            if (element.RenderTransform is TranslateTransform existingTranslate)
            {
                existingTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                existingTranslate.Y = 0;
            }
            return;
        }

        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        element.Opacity = 1;
        element.BeginAnimation(OpacityProperty,
            CreateAnimation(0, 1, TimeSpan.FromMilliseconds(175), ease));
        var translate = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = translate;
        translate.Y = 0;
        translate.BeginAnimation(TranslateTransform.YProperty,
            CreateAnimation(offset, 0, TimeSpan.FromMilliseconds(220), ease));
    }

    private bool FilterApp(object candidate)
    {
        if (candidate is not LauncherItem app)
            return false;
        var query = SearchBox.Text.Trim();
        return query.Length == 0 || app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _appsView?.Refresh();
        AppsCountText.Text = SearchBox.Text.Length == 0
            ? $"{_apps.Count} 个应用"
            : $"{AppsList.Items.Count} 个结果";
        if (AppsList.Items.Count == 0)
        {
            AppsList.SelectedIndex = -1;
        }
        else if (AppsList.SelectedItem is not LauncherItem selected || !AppsList.Items.Contains(selected))
        {
            AppsList.SelectedIndex = 0;
        }
        UpdateSearchEmptyState();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            MoveAppSelection(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        var item = SelectAppForKeyboardLaunch(
            AppsList.SelectedItem as LauncherItem,
            AppsList.Items.OfType<LauncherItem>());
        if (item is not null)
            Launch(item);
        e.Handled = true;
    }

    private void MoveAppSelection(int direction)
    {
        if (AppsList.Items.Count == 0)
            return;

        var current = AppsList.SelectedIndex;
        var next = current < 0
            ? (direction > 0 ? 0 : AppsList.Items.Count - 1)
            : Math.Clamp(current + direction, 0, AppsList.Items.Count - 1);
        AppsList.SelectedIndex = next;
        AppsList.ScrollIntoView(AppsList.SelectedItem);
    }

    internal static LauncherItem? SelectAppForKeyboardLaunch(
        LauncherItem? selected, IEnumerable<LauncherItem> visibleApps) =>
        selected ?? visibleApps.FirstOrDefault();

    private void UpdateSearchEmptyState()
    {
        var isEmpty = LoadingPanel.Visibility == Visibility.Collapsed && AppsList.Items.Count == 0;
        EmptySearchState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (!isEmpty)
            return;

        var searching = !string.IsNullOrWhiteSpace(SearchBox.Text);
        EmptySearchTitle.Text = searching ? "没有找到匹配的应用" : "暂时没有发现应用";
        EmptySearchDescription.Text = searching
            ? "尝试更短的名称或检查拼写"
            : "安装的应用会在完成扫描后显示在这里";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isPointerDragging)
        {
            CancelPointerDrag();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (Keyboard.FocusedElement is TextBox { Tag: string oldGroupName } groupNameEditor)
            {
                groupNameEditor.Text = oldGroupName;
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }

            if (AppsPanel.Visibility == Visibility.Visible && SearchBox.Text.Length > 0)
            {
                SearchBox.Clear();
                SearchBox.Focus();
                e.Handled = true;
                return;
            }

            if (AppsPanel.Visibility == Visibility.Visible)
            {
                ShowPinnedTiles();
            }
            else
            {
                HideLauncher();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OpenSearch();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            ShouldHandleGlobalUndo(_pendingUnpinnedTile is not null,
                Keyboard.FocusedElement is TextBoxBase))
        {
            UndoPendingUnpin();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement == this && e.Key >= Key.A && e.Key <= Key.Z)
        {
            OpenSearch();
            SearchBox.Text += e.Key.ToString();
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }
    }

    internal static bool ShouldHandleGlobalUndo(bool hasPendingUndo, bool focusIsTextEditable) =>
        hasPendingUndo && !focusIsTextEditable;

    private void TilesScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        e.Handled = true;
        if (_isPointerDragging || _isResizing || e.Delta == 0)
            return;

        var nextZoom = LayoutRules.NormalizeCanvasZoom(_canvasZoom +
            Math.Sign(e.Delta) * LayoutRules.CanvasZoomStep);
        if (Math.Abs(nextZoom - _canvasZoom) < 0.001)
        {
            SetStatus($"画布缩放已到 {nextZoom:P0}");
            return;
        }

        var pointer = e.GetPosition(TilesScroll);
        var logicalX = (TilesScroll.HorizontalOffset + pointer.X) / _canvasZoom;
        var logicalY = (TilesScroll.VerticalOffset + pointer.Y) / _canvasZoom;
        SetCanvasZoom(nextZoom);
        TilesScroll.UpdateLayout();
        TilesScroll.ScrollToHorizontalOffset(logicalX * _canvasZoom - pointer.X);
        TilesScroll.ScrollToVerticalOffset(logicalY * _canvasZoom - pointer.Y);
        RenumberAndSave();
        SetStatus($"画布缩放 {_canvasZoom:P0}");
    }

    private void SetCanvasZoom(double zoom)
    {
        _canvasZoom = LayoutRules.NormalizeCanvasZoom(zoom);
        TilesZoomTransform.ScaleX = _canvasZoom;
        TilesZoomTransform.ScaleY = _canvasZoom;
        UpdateZoomText();
    }

    private void UpdateZoomText()
    {
        ZoomText.Text = $"{_canvasZoom:P0} · Ctrl + 滚轮缩放";
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (_isPointerDragging || _isResizing)
            return;
        if (Math.Abs(_canvasZoom - 1) < 0.001)
        {
            SetStatus("画布已经是 100%");
            return;
        }

        var logicalCenterX = (TilesScroll.HorizontalOffset + TilesScroll.ViewportWidth / 2) / _canvasZoom;
        var logicalCenterY = (TilesScroll.VerticalOffset + TilesScroll.ViewportHeight / 2) / _canvasZoom;
        SetCanvasZoom(1);
        TilesScroll.UpdateLayout();
        TilesScroll.ScrollToHorizontalOffset(logicalCenterX - TilesScroll.ViewportWidth / 2);
        TilesScroll.ScrollToVerticalOffset(logicalCenterY - TilesScroll.ViewportHeight / 2);
        RenumberAndSave();
        SetStatus("画布缩放已恢复为 100%");
    }

    private async void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isPointerDragging)
            CancelPointerDrag();
        if (_windowedTestMode)
            return;

        var transition = _transitionVersion;
        var remainingGracePeriod = _ignoreDeactivationUntil - DateTime.UtcNow;
        if (remainingGracePeriod > TimeSpan.Zero)
            await Task.Delay(remainingGracePeriod + TimeSpan.FromMilliseconds(30));

        if (transition != _transitionVersion)
            return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (IsVisible && !IsActive && !_transientUiOpen && DateTime.UtcNow >= _ignoreDeactivationUntil)
                HideLauncher();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void AllApps_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        OpenAppsDrawer(true);
    }

    private void PinnedTiles_Click(object sender, RoutedEventArgs e) => ShowPinnedTiles();

    private void OpenSearch() => OpenAppsDrawer(true);

    private void OpenAppsDrawer(bool focusSearch)
    {
        SearchHost.Visibility = Visibility.Visible;
        DateText.Visibility = Visibility.Visible;
        AppsPanelTitle.Text = "所有应用";
        UpdateNavigationState(true);
        PinnedEmptyState.Visibility = Visibility.Collapsed;
        var wasVisible = AppsPanel.Visibility == Visibility.Visible;
        AppsPanel.Visibility = Visibility.Visible;
        SetTileCanvasBehindDrawer(true);
        if (!wasVisible)
            AnimateDrawerIn();
        if (focusSearch)
        {
            Dispatcher.BeginInvoke(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                SearchBox.CaretIndex = SearchBox.Text.Length;
            }, DispatcherPriority.Input);
        }
    }

    private void ShowPinnedTiles(bool clearSearch = true)
    {
        if (clearSearch)
            SearchBox.Clear();
        AppsPanel.Visibility = Visibility.Collapsed;
        SetTileCanvasBehindDrawer(false);
        DateText.Visibility = Visibility.Visible;
        UpdateNavigationState(false);
        UpdatePinnedEmptyState();
        if (clearSearch)
            AnimateViewIn(TilesScroll, 8);
    }

    private void UpdateNavigationState(bool appsOpen)
    {
        PinnedNavigationButton.Tag = appsOpen ? null : "Selected";
        AppsNavigationButton.Tag = appsOpen ? "Selected" : null;
        PageTitleText.Text = appsOpen ? "应用" : "开始";
        PageSubtitleText.Text = appsOpen
            ? "查找、启动或固定应用"
            : "你的空间，按你的方式排列";
    }

    private void UpdatePinnedEmptyState()
    {
        PinnedEmptyState.Visibility = _tiles.Count == 0 &&
                                      LoadingPanel.Visibility == Visibility.Collapsed &&
                                      AppsPanel.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CloseApps_Click(object sender, RoutedEventArgs e)
    {
        ShowPinnedTiles();
    }

    private void SetTileCanvasBehindDrawer(bool drawerOpen)
    {
        TilesScroll.Visibility = Visibility.Visible;
        TilesScroll.IsHitTestVisible = true;
        if (drawerOpen)
            AppsPanel.UpdateLayout();
        TilesScroll.Margin = drawerOpen
            ? DrawerCanvasMargin()
            : new Thickness(32, 0, 26, 0);
        TilesScroll.Opacity = drawerOpen ? 0.72 : 1;
    }

    private Thickness DrawerCanvasMargin() =>
        new(AppsPanel.Margin.Left + AppsPanel.ActualWidth + 18, 0, 26, 0);

    private void AppsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AppsPanel.Visibility == Visibility.Visible)
            TilesScroll.Margin = DrawerCanvasMargin();
    }

    private static void AnimateDrawerIn(FrameworkElement element, double offset = 24)
    {
        if (!AnimationsEnabled)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            if (element.RenderTransform is TranslateTransform existingTranslate)
            {
                existingTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                existingTranslate.X = 0;
            }
            return;
        }

        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        element.Opacity = 1;
        element.BeginAnimation(OpacityProperty,
            CreateAnimation(0, 1, TimeSpan.FromMilliseconds(160), ease));
        var translate = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = translate;
        translate.X = 0;
        translate.BeginAnimation(TranslateTransform.XProperty,
            CreateAnimation(-offset, 0, TimeSpan.FromMilliseconds(210), ease));
    }

    private void AnimateDrawerIn() => AnimateDrawerIn(AppsPanel);

    private async void AppListItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            await EnsureAppIconLoadedAsync(element);
    }

    private async void AppListItem_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && element.IsLoaded)
            await EnsureAppIconLoadedAsync(element);
    }

    private async Task EnsureAppIconLoadedAsync(FrameworkElement element)
    {
        if (element.DataContext is not LauncherItem app || app.Icon is not null ||
            !_appIconLoads.TryAdd(app.Id, 0))
            return;

        try
        {
            await _appIconLoadGate.WaitAsync();
            try
            {
                var icon = await Task.Run(() => IconService.GetLauncherIcon(app.Target, app.Arguments));
                if (icon is not null)
                    app.Icon = icon;
            }
            finally
            {
                _appIconLoadGate.Release();
            }
        }
        finally
        {
            _appIconLoads.TryRemove(app.Id, out _);
        }
    }

    private void GroupName_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string oldName } textBox)
            return;

        if (e.Key == Key.Escape)
        {
            textBox.Text = oldName;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void GroupName_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string oldName } textBox)
            return;
        var newName = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            textBox.Text = oldName;
            return;
        }
        if (newName.Equals(oldName, StringComparison.CurrentCulture))
            return;
        if (_tiles.Any(tile => !tile.Group.Equals(oldName, StringComparison.CurrentCulture) &&
                               tile.Group.Equals(newName, StringComparison.CurrentCultureIgnoreCase)))
        {
            textBox.Text = oldName;
            SetStatus($"分组“{newName}”已存在，请使用其他名称");
            return;
        }

        foreach (var tile in _tiles.Where(tile => tile.Group.Equals(oldName, StringComparison.CurrentCulture)))
            tile.Group = newName;
        _tileView.Refresh();
        RenumberAndSave();
        SetStatus($"分组已重命名为“{newName}”");
    }

    private void Explorer_Click(object sender, RoutedEventArgs e) => LaunchTarget("explorer.exe");

    private void Settings_Click(object sender, RoutedEventArgs e) => LaunchTarget("ms-settings:");

    private void AppsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        var row = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is LauncherItem item)
            Launch(item);
    }

    private void AppsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        if (e.Key != Key.Enter || AppsList.SelectedItem is not LauncherItem item)
            return;
        Launch(item);
        e.Handled = true;
    }

    private void PinApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LauncherItem item })
            return;

        if (item.IsPinned || _tiles.Any(tile => ContainsTarget(tile, item.Target, item.Arguments)))
        {
            SetStatus($"“{item.Name}”已经固定");
            return;
        }

        _tiles.Add(new TileLayoutItem
        {
            Id = item.Id,
            Name = item.Name,
            Target = item.Target,
            Arguments = item.Arguments,
            Glyph = item.Glyph,
            Accent = item.Accent,
            Icon = item.Icon,
            Group = "我的应用",
            Size = TileSize.Medium,
            VisualStyle = TileVisualStyle.Glass,
            Order = _tiles.Count
        });
        LayoutRules.ResolveGroupColumns(_tiles);
        LayoutRules.ResolveGroupLayout(_tiles, "我的应用");
        _tileView.Refresh();
        UpdatePinnedAppStates();
        RenumberAndSave();
        SetStatus($"已固定“{item.Name}”");
    }

    private void UpdatePinnedAppStates()
    {
        foreach (var app in _apps)
            app.IsPinned = _tiles.Any(tile => ContainsTarget(tile, app.Target, app.Arguments));
    }

    private static bool ContainsTarget(TileLayoutItem tile, string target, string arguments)
    {
        return tile.Kind == TileKind.App && SameTarget(tile.Target, tile.Arguments, target, arguments);
    }

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null ||
            sender is not Border { Tag: TileLayoutItem tile } border)
            return;
        _dragStart = e.GetPosition(this);
        _dragPointerOffset = e.GetPosition(border);
        _pressedTile = tile;
        _pressedTileBorder = border;
        _mouseMoved = false;
    }

    private void Tile_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Border { Tag: TileLayoutItem tile } ||
            e.Key is not (Key.Enter or Key.Space))
            return;

        if (tile.Kind == TileKind.App)
            Launch(tile);
        e.Handled = true;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isResizing || _pressedTile is null || _pressedTileBorder is null)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelPointerDrag();
            return;
        }

        var position = e.GetPosition(this);
        if (!_isPointerDragging &&
            Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (!_isPointerDragging)
            BeginPointerDrag();
        if (!_isPointerDragging)
            return;

        UpdateDragPreview(e.GetPosition(LauncherRoot));
        UpdatePointerDropTarget(e);
        e.Handled = true;
    }

    private void BeginPointerDrag()
    {
        if (_pressedTileBorder is null)
            return;

        _isPointerDragging = true;
        _mouseMoved = true;
        _transientUiOpen = true;
        _suppressTileLaunchUntil = DateTime.UtcNow.AddMilliseconds(500);

        _pressedTileBorder.UpdateLayout();
        DragPreview.Width = _pressedTileBorder.ActualWidth * _canvasZoom;
        DragPreview.Height = _pressedTileBorder.ActualHeight * _canvasZoom;
        DragPreview.Background = CreateDragPreviewBrush(_pressedTileBorder);
        DragPreview.Visibility = Visibility.Visible;
        _pressedTileBorder.Opacity = 0.24;
        _pressedTileBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(219, 243, 255));
        _pressedTileBorder.BorderThickness = new Thickness(2);
        SetAlignmentGridVisible(true);
        LauncherRoot.Cursor = Cursors.SizeAll;
        Mouse.Capture(LauncherRoot, CaptureMode.SubTree);
    }

    private void UpdateDragPreview(Point pointerPosition)
    {
        Canvas.SetLeft(DragPreview, pointerPosition.X - _dragPointerOffset.X * _canvasZoom);
        Canvas.SetTop(DragPreview, pointerPosition.Y - _dragPointerOffset.Y * _canvasZoom);
    }

    private void UpdatePointerDropTarget(MouseEventArgs e)
    {
        var pointerPosition = e.GetPosition(TilesItems);
        var hit = TilesItems.InputHitTest(pointerPosition) as DependencyObject;
        var border = FindTileBorder(hit);
        var panel = FindVisualAncestor<TilePositionPanel>(hit);
        if (_pressedTile is null || _pressedTileBorder is null)
        {
            ClearPointerDropTarget();
            return;
        }

        UpdatePointerDropBorder(border);
        if (panel is not null && TryGetGroupName(panel, out var groupName))
        {
            SetExistingGroupDropTarget(e, panel, groupName);
            return;
        }

        var groupPanel = FindVisualAncestor<GroupPositionPanel>(hit);
        if (groupPanel is null)
        {
            ClearPointerDropTarget();
            return;
        }

        var groupPoint = e.GetPosition(groupPanel);
        if (groupPanel.TryGetGroupAtX(groupPoint.X, out var existingGroup))
        {
            var existingPanel = FindVisualChildren<TilePositionPanel>(groupPanel)
                .FirstOrDefault(candidate => TryGetGroupName(candidate, out var name) &&
                                             name.Equals(existingGroup, StringComparison.Ordinal));
            if (existingPanel is not null)
            {
                SetExistingGroupDropTarget(e, existingPanel, existingGroup);
                return;
            }
        }

        var groupColumn = groupPanel.GetInsertionColumn(groupPoint.X);
        var insertionX = groupPanel.GetInsertionX(groupColumn);
        var column = (int)Math.Round(
            (groupPoint.X - insertionX -
             _dragPointerOffset.X - 4) / LayoutRules.GridCellSize,
            MidpointRounding.AwayFromZero);
        var row = (int)Math.Round(
            (groupPoint.Y - GroupPositionPanel.GroupHeaderHeight -
             _dragPointerOffset.Y - 4) / LayoutRules.GridCellSize,
            MidpointRounding.AwayFromZero);
        var fitted = LayoutRules.FitAvailableDropTarget(_tiles, _pressedTile, null,
            LayoutRules.DefaultGridColumns, LayoutRules.DefaultGridRows, column, row);
        if (fitted is null)
        {
            ClearPointerDropTarget();
            return;
        }
        _pointerDropGroup = null;
        _pointerDropGroupColumn = groupColumn;
        _pointerDropColumn = fitted.Value.Column;
        _pointerDropRow = fitted.Value.Row;
        _pointerCreatesGroup = true;
        ShowNewGroupDropSlotPreview(groupPanel, groupColumn, _pointerDropColumn, _pointerDropRow);
    }

    private void UpdatePointerDropBorder(Border? border)
    {
        if (ReferenceEquals(border, _pointerDropBorder))
            return;
        if (_pointerDropBorder is not null)
            ResetDragHighlight(_pointerDropBorder);
        _pointerDropBorder = ReferenceEquals(border, _pressedTileBorder) ? null : border;
        if (_pointerDropBorder is not null)
        {
            _pointerDropBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 250, 255));
            _pointerDropBorder.BorderThickness = new Thickness(3);
        }
    }

    private void SetExistingGroupDropTarget(MouseEventArgs e, TilePositionPanel panel, string groupName)
    {
        var point = e.GetPosition(panel);
        var groupTile = _tiles.First(tile =>
            tile.Group.Equals(groupName, StringComparison.Ordinal));
        var column = (int)Math.Round(
            (point.X - _dragPointerOffset.X - 4) / LayoutRules.GridCellSize,
            MidpointRounding.AwayFromZero);
        var row = (int)Math.Round(
            (point.Y - _dragPointerOffset.Y - 4) / LayoutRules.GridCellSize,
            MidpointRounding.AwayFromZero);
        var fitted = LayoutRules.FitAvailableDropTarget(_tiles, _pressedTile!, groupName,
            groupTile.GroupWidthColumns, groupTile.GroupHeightRows, column, row);
        if (fitted is null)
        {
            ClearPointerDropTarget();
            return;
        }
        _pointerDropGroup = groupName;
        _pointerDropGroupColumn = groupTile.GroupColumn;
        _pointerDropColumn = fitted.Value.Column;
        _pointerDropRow = fitted.Value.Row;
        _pointerCreatesGroup = false;
        ShowDropSlotPreview(panel, _pointerDropColumn, _pointerDropRow);
    }

    private static bool TryGetGroupName(TilePositionPanel panel, out string groupName)
    {
        var groupItem = FindVisualAncestor<GroupItem>(panel);
        if (groupItem?.DataContext is CollectionViewGroup { Name: string name })
        {
            groupName = name;
            return true;
        }
        groupName = string.Empty;
        return false;
    }

    private void ShowDropSlotPreview(TilePositionPanel panel, int column, int row)
    {
        if (_pressedTileBorder is null)
            return;
        try
        {
            var position = panel.TransformToAncestor(LauncherRoot).Transform(new Point(
                column * LayoutRules.GridCellSize + 4,
                row * LayoutRules.GridCellSize + 4));
            DropSlotPreview.Width = _pressedTileBorder.ActualWidth * _canvasZoom;
            DropSlotPreview.Height = _pressedTileBorder.ActualHeight * _canvasZoom;
            Canvas.SetLeft(DropSlotPreview, position.X);
            Canvas.SetTop(DropSlotPreview, position.Y);
            DropSlotPreview.Visibility = Visibility.Visible;
        }
        catch (InvalidOperationException)
        {
            DropSlotPreview.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowNewGroupDropSlotPreview(GroupPositionPanel panel,
        int groupColumn, int column, int row)
    {
        if (_pressedTileBorder is null)
            return;
        try
        {
            var position = panel.TransformToAncestor(LauncherRoot).Transform(new Point(
                panel.GetInsertionX(groupColumn) + column * LayoutRules.GridCellSize + 4,
                GroupPositionPanel.GroupHeaderHeight + row * LayoutRules.GridCellSize + 4));
            DropSlotPreview.Width = _pressedTileBorder.ActualWidth * _canvasZoom;
            DropSlotPreview.Height = _pressedTileBorder.ActualHeight * _canvasZoom;
            Canvas.SetLeft(DropSlotPreview, position.X);
            Canvas.SetTop(DropSlotPreview, position.Y);
            DropSlotPreview.Visibility = Visibility.Visible;
        }
        catch (InvalidOperationException)
        {
            DropSlotPreview.Visibility = Visibility.Collapsed;
        }
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPointerDragging)
            return;

        var source = _pressedTile;
        var createsGroup = _pointerCreatesGroup;
        var targetGroup = createsGroup ? NextGroupName() : _pointerDropGroup;
        var targetGroupColumn = _pointerDropGroupColumn;
        var targetColumn = _pointerDropColumn;
        var targetRow = _pointerDropRow;
        EndPointerDragVisuals();

        if (source is not null && targetGroup is not null &&
            PlaceTile(source, targetGroup, targetColumn, targetRow, targetGroupColumn))
        {
            RenumberAndSave();
            SetStatus(createsGroup
                ? $"已在右侧创建“{targetGroup}”并放入“{source.Name}”"
                : $"已将“{source.Name}”放到 {targetGroup} · 第 {targetRow + 1} 行");
        }

        ResetPointerState();
        e.Handled = true;
    }

    private void CancelPointerDrag()
    {
        if (!_isPointerDragging)
            return;
        EndPointerDragVisuals();
        ResetPointerState();
    }

    private void EndPointerDragVisuals()
    {
        _isPointerDragging = false;
        if (Mouse.Captured == LauncherRoot)
            Mouse.Capture(null);
        if (_pressedTileBorder is not null)
        {
            _pressedTileBorder.BeginAnimation(OpacityProperty, null);
            _pressedTileBorder.Opacity = 1;
            ResetDragHighlight(_pressedTileBorder);
        }
        SetAlignmentGridVisible(false);
        ClearPointerDropTarget();
        DragPreview.Visibility = Visibility.Collapsed;
        DragPreview.Background = null;
        LauncherRoot.ClearValue(CursorProperty);
    }

    private void SetAlignmentGridVisible(bool visible)
    {
        foreach (var panel in FindVisualChildren<TilePositionPanel>(TilesItems))
            panel.ShowAlignmentGrid = visible;
    }

    private void ClearPointerDropTarget()
    {
        if (_pointerDropBorder is not null)
            ResetDragHighlight(_pointerDropBorder);
        _pointerDropBorder = null;
        _pointerDropGroup = null;
        _pointerDropGroupColumn = 0;
        _pointerCreatesGroup = false;
        DropSlotPreview.Visibility = Visibility.Collapsed;
    }

    private void ResetPointerState()
    {
        _pressedTile = null;
        _pressedTileBorder = null;
        _mouseMoved = false;
        _transientUiOpen = false;
        _suppressTileLaunchUntil = DateTime.UtcNow.AddMilliseconds(350);
    }

    private static ImageBrush CreateDragPreviewBrush(Border source)
    {
        var dpi = VisualTreeHelper.GetDpi(source);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(source);
        bitmap.Freeze();
        return new ImageBrush(bitmap) { Stretch = Stretch.Fill };
    }

    private static void ResetDragHighlight(Border border)
    {
        border.ClearValue(Border.BorderBrushProperty);
        border.ClearValue(Border.BorderThicknessProperty);
    }

    private string NextGroupName()
    {
        const string root = "新分组";
        if (_tiles.All(tile => !tile.Group.Equals(root, StringComparison.Ordinal)))
            return root;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{root} {suffix}";
            if (_tiles.All(tile => !tile.Group.Equals(candidate, StringComparison.Ordinal)))
                return candidate;
        }
    }

    private bool PlaceTile(TileLayoutItem source, string group, int column, int row,
        int? groupColumn = null)
    {
        var previousPositions = CaptureTilePositions();
        if (!LayoutRules.Place(_tiles, source, group, column, row, groupColumn))
            return false;
        _tileView.Refresh();
        ScheduleTileReflow(previousPositions);
        return true;
    }

    private Dictionary<string, Point> CaptureTilePositions()
    {
        TilesItems.UpdateLayout();
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal);
        foreach (var border in FindVisualChildren<Border>(TilesItems)
                     .Where(border => border.Tag is TileLayoutItem))
        {
            try
            {
                var tile = (TileLayoutItem)border.Tag;
                positions[tile.Id] = border.TransformToAncestor(TilesItems).Transform(new Point());
            }
            catch (InvalidOperationException)
            {
                // A container can disappear between layout and a drag-over reorder.
            }
        }
        return positions;
    }

    private void ScheduleTileReflow(Dictionary<string, Point> previousPositions) =>
        Dispatcher.BeginInvoke(() => AnimateTileReflow(previousPositions), DispatcherPriority.Loaded);

    private void AnimateTileReflow(IReadOnlyDictionary<string, Point> previousPositions)
    {
        TilesItems.UpdateLayout();
        if (!AnimationsEnabled)
            return;
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        foreach (var border in FindVisualChildren<Border>(TilesItems)
                     .Where(border => border.Tag is TileLayoutItem))
        {
            var tile = (TileLayoutItem)border.Tag;
            if (border.Child is not Grid body)
                continue;

            if (!previousPositions.TryGetValue(tile.Id, out var previous))
            {
                border.BeginAnimation(OpacityProperty,
                    CreateAnimation(0, 1, TimeSpan.FromMilliseconds(135), ease));
                continue;
            }

            Point current;
            try
            {
                current = border.TransformToAncestor(TilesItems).Transform(new Point());
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var deltaX = previous.X - current.X;
            var deltaY = previous.Y - current.Y;
            if (Math.Abs(deltaX) < 0.5 && Math.Abs(deltaY) < 0.5)
                continue;

            var translate = body.RenderTransform as TranslateTransform ?? new TranslateTransform();
            body.RenderTransform = translate;
            translate.BeginAnimation(TranslateTransform.XProperty,
                CreateAnimation(deltaX, 0, TimeSpan.FromMilliseconds(210), ease));
            translate.BeginAnimation(TranslateTransform.YProperty,
                CreateAnimation(deltaY, 0, TimeSpan.FromMilliseconds(210), ease));
        }
    }

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isResizing || FindVisualAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null)
            return;

        if (sender is Border { Tag: TileLayoutItem tile } && !_mouseMoved &&
            DateTime.UtcNow >= _suppressTileLaunchUntil)
        {
            if (tile.Kind == TileKind.App)
                Launch(tile);
        }
        _pressedTile = null;
        _pressedTileBorder = null;
        _mouseMoved = false;
    }

    private static string TileSizeLabel(TileSize size) => size switch
    {
        TileSize.Small => "小磁贴",
        TileSize.Medium => "中磁贴",
        TileSize.Wide => "宽磁贴",
        TileSize.Large => "大磁贴",
        TileSize.Custom => "自定义磁贴",
        _ => "磁贴"
    };

    private void ResizeHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedTile = null;
        _pressedTileBorder = null;
        _mouseMoved = true;
        _isResizing = true;
        _suppressTileLaunchUntil = DateTime.UtcNow.AddMilliseconds(500);
    }

    private void ResizeHandle_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isResizing = true;
        _transientUiOpen = true;
        SetAlignmentGridVisible(true);
        _resizeStartPositions = CaptureTilePositions();
        _resizeStartPointer = Mouse.GetPosition(TilesScroll);
        _resizeStartCanvasZoom = _canvasZoom;
        if (sender is Thumb { DataContext: TileLayoutItem tile })
        {
            _resizeStartWidth = tile.TileWidth;
            _resizeStartHeight = tile.TileHeight;
        }
    }

    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { DataContext: TileLayoutItem tile })
            return;

        var pointer = Mouse.GetPosition(TilesScroll);
        var horizontalChange = LayoutRules.PointerResizeDelta(
            _resizeStartPointer.X, pointer.X, _resizeStartCanvasZoom);
        var verticalChange = LayoutRules.PointerResizeDelta(
            _resizeStartPointer.Y, pointer.Y, _resizeStartCanvasZoom);
        var requestedWidth = Math.Clamp(_resizeStartWidth + horizontalChange,
            TileLayoutItem.MinimumTileExtent, TileLayoutItem.MaximumTileWidth);
        var requestedHeight = Math.Clamp(_resizeStartHeight + verticalChange,
            TileLayoutItem.MinimumTileExtent, TileLayoutItem.MaximumTileHeight);
        var freeformResize = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var resizedWidth = freeformResize
            ? requestedWidth
            : LayoutRules.SnapTileExtent(requestedWidth, 12);
        var resizedHeight = freeformResize
            ? requestedHeight
            : LayoutRules.SnapTileExtent(requestedHeight, 32);
        var requiredColumns = Math.Clamp(Math.Max(0, tile.GridColumn) +
            (int)Math.Ceiling((resizedWidth + 8) / LayoutRules.GridCellSize), 2, 12);
        var requiredRows = Math.Clamp(Math.Max(0, tile.GridRow) +
            (int)Math.Ceiling((resizedHeight + 8) / LayoutRules.GridCellSize), 4, 32);
        if (requiredColumns > tile.GroupWidthColumns || requiredRows > tile.GroupHeightRows)
        {
            LayoutRules.ExpandGroupSize(_tiles, tile.Group,
                Math.Max(requiredColumns, tile.GroupWidthColumns),
                Math.Max(requiredRows, tile.GroupHeightRows));
            InvalidatePositionPanels();
        }

        var maxWidth = Math.Max(TileLayoutItem.MinimumTileExtent,
            (tile.GroupWidthColumns - Math.Max(0, tile.GridColumn)) * LayoutRules.GridCellSize - 8);
        var maxHeight = Math.Max(TileLayoutItem.MinimumTileExtent,
            (tile.GroupHeightRows - Math.Max(0, tile.GridRow)) * LayoutRules.GridCellSize - 8);
        tile.SetCustomSize(
            Math.Min(resizedWidth, maxWidth),
            Math.Min(resizedHeight, maxHeight));
        var span = LayoutRules.CellSpan(tile);
        SetStatus(freeformResize
            ? $"正在无级调整“{tile.Name}”：{tile.TileWidth:0} × {tile.TileHeight:0}（松开 Alt 恢复网格）"
            : $"正在按网格调整“{tile.Name}”：{span.Columns} 列 × {span.Rows} 行");
    }

    private void ResizeHandle_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Thumb { DataContext: TileLayoutItem tile })
        {
            LayoutRules.Place(_tiles, tile, tile.Group, tile.GridColumn, tile.GridRow);
            LayoutRules.ResolveGroupColumns(_tiles, tile.Group);
            _tileView.Refresh();
            if (_resizeStartPositions is not null)
                ScheduleTileReflow(_resizeStartPositions);
            RenumberAndSave();
            SetStatus($"“{tile.Name}”已调整为 {tile.TileWidth:0} × {tile.TileHeight:0}");
        }

        SetAlignmentGridVisible(false);
        _resizeStartPositions = null;
        _isResizing = false;
        _transientUiOpen = false;
        _mouseMoved = false;
        _suppressTileLaunchUntil = DateTime.UtcNow.AddMilliseconds(350);
    }

    private void GroupResize_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: string groupName } ||
            _tiles.FirstOrDefault(item => item.Group.Equals(groupName, StringComparison.Ordinal))
                is not { } tile)
            return;

        _resizingGroup = tile.Group;
        _groupResizeStartColumns = tile.GroupWidthColumns;
        _groupResizeStartRows = tile.GroupHeightRows;
        _groupResizeStartPointer = Mouse.GetPosition(TilesScroll);
        _groupResizeStartCanvasZoom = _canvasZoom;
        _isResizing = true;
        _transientUiOpen = true;
        _mouseMoved = true;
        _suppressTileLaunchUntil = DateTime.UtcNow.AddMilliseconds(500);
    }

    private void GroupResize_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Thumb { Tag: string groupName } ||
            e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down) ||
            _tiles.FirstOrDefault(item => item.Group.Equals(groupName, StringComparison.Ordinal))
                is not { } tile)
            return;

        var columns = tile.GroupWidthColumns + (e.Key == Key.Right ? 1 : e.Key == Key.Left ? -1 : 0);
        var rows = tile.GroupHeightRows + (e.Key == Key.Down ? 1 : e.Key == Key.Up ? -1 : 0);
        if (LayoutRules.SetGroupSize(_tiles, groupName, columns, rows))
        {
            InvalidatePositionPanels();
            if (RenumberAndSave())
                SetStatus($"“{groupName}”已调整为 {tile.GroupWidthColumns} 列 × {tile.GroupHeightRows} 行");
        }
        else
        {
            SetStatus($"“{groupName}”已经是当前可用尺寸");
        }
        e.Handled = true;
    }

    private void GroupResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_resizingGroup is null)
            return;

        var pointer = Mouse.GetPosition(TilesScroll);
        var horizontalChange = LayoutRules.PointerResizeDelta(
            _groupResizeStartPointer.X, pointer.X, _groupResizeStartCanvasZoom);
        var verticalChange = LayoutRules.PointerResizeDelta(
            _groupResizeStartPointer.Y, pointer.Y, _groupResizeStartCanvasZoom);
        var columns = (int)Math.Round(
            _groupResizeStartColumns +
            horizontalChange / LayoutRules.GridCellSize,
            MidpointRounding.AwayFromZero);
        var rows = (int)Math.Round(
            _groupResizeStartRows +
            verticalChange / LayoutRules.GridCellSize,
            MidpointRounding.AwayFromZero);
        LayoutRules.SetGroupSize(_tiles, _resizingGroup, columns, rows);
        InvalidatePositionPanels();

        var tile = _tiles.First(item => item.Group.Equals(_resizingGroup, StringComparison.Ordinal));
        SetStatus($"正在调整“{_resizingGroup}”：{tile.GroupWidthColumns} 列 × {tile.GroupHeightRows} 行");
    }

    private void GroupResize_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_resizingGroup is not null)
        {
            var tile = _tiles.FirstOrDefault(item =>
                item.Group.Equals(_resizingGroup, StringComparison.Ordinal));
            InvalidatePositionPanels();
            RenumberAndSave();
            if (tile is not null)
                SetStatus($"“{_resizingGroup}”已调整为 {tile.GroupWidthColumns} 列 × {tile.GroupHeightRows} 行");
        }

        _resizingGroup = null;
        _isResizing = false;
        _transientUiOpen = false;
        _mouseMoved = false;
        _suppressTileLaunchUntil = DateTime.UtcNow.AddMilliseconds(350);
    }

    private void InvalidatePositionPanels()
    {
        TilesItems.InvalidateMeasure();
        TilesItems.InvalidateArrange();
        foreach (var panel in FindVisualChildren<TilePositionPanel>(TilesItems))
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
        foreach (var panel in FindVisualChildren<GroupPositionPanel>(TilesItems))
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    private void ResizeTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag, DataContext: TileLayoutItem tile } ||
            !tag.StartsWith("size:", StringComparison.Ordinal) ||
            !Enum.TryParse<TileSize>(tag[5..], out var size))
            return;

        var index = _tiles.IndexOf(tile);
        if (index < 0)
            return;
        var previousPositions = CaptureTilePositions();
        var previousWidth = tile.TileWidth;
        var previousHeight = tile.TileHeight;
        tile.Size = size;
        LayoutRules.ResolveGroupLayout(_tiles, tile.Group, tile);
        _tileView.Refresh();
        ScheduleTileReflow(previousPositions);
        Dispatcher.BeginInvoke(() => AnimateTileResize(tile, previousWidth, previousHeight), DispatcherPriority.Loaded);
        RenumberAndSave();
        SetStatus($"“{tile.Name}”已调整为{TileSizeLabel(size)}");
    }

    private void TileColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag, DataContext: TileLayoutItem tile } ||
            !tag.StartsWith("color:#", StringComparison.OrdinalIgnoreCase))
            return;
        ApplyTileColor(tile, tag[6..]);
    }

    private void CustomTileColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: TileLayoutItem tile })
            return;

        var initial = Color.FromRgb(22, 116, 209);
        try
        {
            initial = (Color)ColorConverter.ConvertFromString(tile.Accent);
        }
        catch (FormatException)
        {
            // Keep the Tile10 blue fallback.
        }

        using var dialog = new WinForms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = false,
            Color = DrawingColor.FromArgb(initial.R, initial.G, initial.B)
        };

        _transientUiOpen = true;
        _ignoreDeactivationUntil = DateTime.UtcNow.AddSeconds(2);
        try
        {
            var owner = new NativeWin32Window(new WindowInteropHelper(this).Handle);
            if (dialog.ShowDialog(owner) == WinForms.DialogResult.OK)
                ApplyTileColor(tile, $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        }
        finally
        {
            _ignoreDeactivationUntil = DateTime.UtcNow.AddSeconds(1);
            _transientUiOpen = false;
        }
    }

    private void ApplyTileColor(TileLayoutItem tile, string accent)
    {
        tile.Accent = accent.ToUpperInvariant();
        tile.HasCustomAppearance = true;
        RenumberAndSave();
        SetStatus($"“{tile.Name}”颜色已更新");
    }

    private void TileStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag, DataContext: TileLayoutItem tile } ||
            !tag.StartsWith("style:", StringComparison.Ordinal) ||
            !Enum.TryParse<TileVisualStyle>(tag[6..], out var style))
            return;

        tile.VisualStyle = style;
        tile.HasCustomAppearance = true;
        RenumberAndSave();
        SetStatus($"“{tile.Name}”已切换为{TileStyleLabel(style)}");
    }

    private static string TileStyleLabel(TileVisualStyle style) => style switch
    {
        TileVisualStyle.Glass => "通透玻璃",
        TileVisualStyle.Classic => "经典纯色",
        TileVisualStyle.Minimal => "极简深色",
        TileVisualStyle.IconOnly => "仅显示图标",
        _ => "自定义风格"
    };

    private void AnimateTileResize(TileLayoutItem tile, double previousWidth, double previousHeight)
    {
        TilesItems.UpdateLayout();
        if (!AnimationsEnabled)
            return;
        var border = FindVisualChildren<Border>(TilesItems)
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Tag, tile));
        if (border is null)
            return;

        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(230);
        border.BeginAnimation(WidthProperty, CreateAnimation(previousWidth, tile.TileWidth, duration, ease));
        border.BeginAnimation(HeightProperty, CreateAnimation(previousHeight, tile.TileHeight, duration, ease));
    }

    private void ToggleLiveTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: TileLayoutItem tile } || tile.Kind == TileKind.App)
        {
            SetStatus("这个桌面应用没有可用的动态磁贴数据源");
            return;
        }
        tile.IsLiveEnabled = !tile.IsLiveEnabled;
        if (tile.Kind == TileKind.Clock && !tile.IsLiveEnabled)
        {
            tile.LivePrimaryText = "时钟";
            tile.LiveSecondaryText = "动态磁贴已关闭";
        }
        else
        {
            UpdateLiveTiles();
        }
        RenumberAndSave();
        SetStatus(tile.IsLiveEnabled ? "动态磁贴已开启" : "动态磁贴已关闭");
    }

    private void UnpinTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: TileLayoutItem tile })
            return;
        var removedIndex = _tiles.IndexOf(tile);
        if (removedIndex < 0)
            return;
        _tiles.Remove(tile);
        _tileView.Refresh();
        UpdatePinnedAppStates();
        if (!RenumberAndSave())
        {
            RestoreTileAtIndex(_tiles, tile, removedIndex);
            _tileView.Refresh();
            UpdatePinnedAppStates();
            UpdatePinnedEmptyState();
            return;
        }
        _pendingUnpinnedTile = tile;
        _pendingUnpinnedIndex = removedIndex;
        UpdatePinnedEmptyState();
        SetStatus($"已取消固定“{tile.Name}” · 可按 Ctrl+Z 撤销", showUndo: true);
    }

    private void UndoUnpin_Click(object sender, RoutedEventArgs e)
    {
        UndoPendingUnpin();
    }

    private void UndoPendingUnpin()
    {
        if (_pendingUnpinnedTile is not { } tile)
            return;

        var index = Math.Clamp(_pendingUnpinnedIndex, 0, _tiles.Count);
        ClearPendingUnpin();
        RestoreTileAtIndex(_tiles, tile, index);
        _tileView.Refresh();
        UpdatePinnedAppStates();
        if (!RenumberAndSave())
        {
            _tiles.Remove(tile);
            for (var itemIndex = 0; itemIndex < _tiles.Count; itemIndex++)
                _tiles[itemIndex].Order = itemIndex;
            _tileView.Refresh();
            UpdatePinnedAppStates();
            UpdatePinnedEmptyState();
            return;
        }
        UpdatePinnedEmptyState();
        SetStatus($"已恢复“{tile.Name}”");
    }

    internal static int RestoreTileAtIndex(IList<TileLayoutItem> tiles,
        TileLayoutItem tile, int requestedIndex)
    {
        var index = Math.Clamp(requestedIndex, 0, tiles.Count);
        tiles.Insert(index, tile);
        for (var itemIndex = 0; itemIndex < tiles.Count; itemIndex++)
            tiles[itemIndex].Order = itemIndex;
        return index;
    }

    private void StatusToast_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_pendingUnpinnedTile is not null)
            _statusTimer.Stop();
    }

    private void StatusToast_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_pendingUnpinnedTile is not null && !StatusActionButton.IsKeyboardFocusWithin)
            _statusTimer.Start();
    }

    private void StatusActionButton_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_pendingUnpinnedTile is not null)
            _statusTimer.Stop();
    }

    private void StatusActionButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_pendingUnpinnedTile is not null && !StatusToast.IsMouseOver)
            _statusTimer.Start();
    }

    private void ClearPendingUnpin()
    {
        _pendingUnpinnedTile = null;
        _pendingUnpinnedIndex = -1;
        StatusActionButton.Visibility = Visibility.Collapsed;
    }

    private void Launch(LauncherItem item)
    {
        if (LaunchService.TryLaunch(item, out var error))
            HideLauncher();
        else
            SetStatus($"无法启动“{item.Name}”：{error}");
    }

    private void Launch(TileLayoutItem item)
    {
        if (LaunchService.TryLaunch(item, out var error))
            HideLauncher();
        else
            SetStatus($"无法启动“{item.Name}”：{error}");
    }

    private void LaunchTarget(string target)
    {
        if (LaunchService.TryLaunch(new LauncherItem { Name = target, Target = target }, out var error))
            HideLauncher();
        else
            SetStatus($"无法启动：{error}");
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _transientUiOpen = true;
        if (sender is not ContextMenu { DataContext: TileLayoutItem tile } menu)
            return;

        foreach (var item in FindMenuItems(menu.Items))
        {
            if (item.Tag is not string tag)
                continue;
            item.IsChecked = tag switch
            {
                var value when value.StartsWith("size:", StringComparison.Ordinal) =>
                    tile.Size != TileSize.Custom && value[5..].Equals(tile.Size.ToString(), StringComparison.Ordinal),
                var value when value.StartsWith("color:", StringComparison.OrdinalIgnoreCase) =>
                    value[6..].Equals(tile.Accent, StringComparison.OrdinalIgnoreCase),
                var value when value.StartsWith("style:", StringComparison.Ordinal) =>
                    value[6..].Equals(tile.VisualStyle.ToString(), StringComparison.Ordinal),
                _ => false
            };
        }
    }

    private static IEnumerable<MenuItem> FindMenuItems(ItemCollection items)
    {
        foreach (var rawItem in items)
        {
            if (rawItem is not MenuItem item)
                continue;
            yield return item;
            foreach (var child in FindMenuItems(item.Items))
                yield return child;
        }
    }

    private void ContextMenu_Closed(object sender, RoutedEventArgs e) => _transientUiOpen = false;

    private void Popup_Opened(object sender, EventArgs e) => _transientUiOpen = true;

    private void Popup_Closed(object sender, EventArgs e) => _transientUiOpen = false;

    private void Power_Click(object sender, RoutedEventArgs e) => PowerPopup.IsOpen = !PowerPopup.IsOpen;

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupService.SetEnabled(StartWithWindowsCheckBox.IsChecked == true);
            SetStatus(StartWithWindowsCheckBox.IsChecked == true ? "已启用开机自动运行" : "已关闭开机自动运行");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            StartWithWindowsCheckBox.IsChecked = StartupService.IsEnabled();
            SetStatus($"无法修改开机启动设置：{ex.Message}");
        }
    }

    private void PowerAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action })
            return;

        PowerPopup.IsOpen = false;
        switch (action)
        {
            case "exit":
                _allowClose = true;
                Application.Current.Shutdown();
                break;
            case "sleep":
                if (NativeWindow.SetSuspendState(false, false, false))
                {
                    HideLauncher();
                }
                else
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    SetStatus($"无法使电脑进入睡眠：{error}");
                }
                break;
            case "restart":
                if (ConfirmPowerAction("确定要重新启动电脑吗？"))
                    StartPowerCommand("/r /t 0", "重新启动");
                break;
            case "shutdown":
                if (ConfirmPowerAction("确定要关闭电脑吗？"))
                    StartPowerCommand("/s /t 0", "关机");
                break;
        }
    }

    private async void StartPowerCommand(string arguments, string actionName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                SetStatus($"无法执行{actionName}：系统未启动关机命令");
                return;
            }

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                SetStatus($"无法执行{actionName}：关机命令返回错误 {process.ExitCode}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            SetStatus($"无法执行{actionName}：{ex.Message}");
        }
    }

    private bool ConfirmPowerAction(string message)
    {
        _transientUiOpen = true;
        try
        {
            return MessageBox.Show(this, message, "Tile10", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        finally
        {
            _transientUiOpen = false;
        }
    }

    private bool RenumberAndSave()
    {
        for (var index = 0; index < _tiles.Count; index++)
            _tiles[index].Order = index;
        try
        {
            _layoutService.Save(_tiles, _canvasZoom);
            return true;
        }
        catch (IOException ex)
        {
            SetStatus($"布局保存失败：{ex.Message}");
            return false;
        }
    }

    private void SetStatus(string message, bool showUndo = false)
    {
        if (!showUndo)
            ClearPendingUnpin();
        StatusActionButton.Visibility = showUndo ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = message;
        StatusText.ToolTip = message;
        StatusToast.Visibility = Visibility.Visible;
        StatusToast.Opacity = 1;
        UIElementAutomationPeer.CreatePeerForElement(StatusText)?
            .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        DateText.Text = $"{now:HH:mm}   {now:M月d日 dddd}";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideLauncher();
        }
        base.OnClosing(e);
    }

    public void Dispose()
    {
        _allowClose = true;
        _keyboardHook?.Dispose();
        _clockTimer.Stop();
        _liveTileTimer.Stop();
        _statusTimer.Stop();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static Border? FindTileBorder(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Border { Tag: TileLayoutItem } border)
                return border;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private sealed class NativeWin32Window(IntPtr handle) : WinForms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private static class NativeWindow
    {
        private const uint MonitorDefaultToNearest = 2;
        private static readonly IntPtr HwndTopmost = new(-1);

        public static void PlaceOnCursorMonitor(Window window)
        {
            if (!GetCursorPos(out var cursor))
                return;
            var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
                return;

            // Create and position the HWND while it is still hidden. Showing a default-sized
            // WPF window first lets DWM briefly present that intermediate frame.
            var handle = new WindowInteropHelper(window).EnsureHandle();
            // Desktop full-screen Start keeps the taskbar visible. Tablet mode would use Monitor instead.
            var bounds = info.WorkArea;
            SetWindowPos(handle, HwndTopmost, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PointNative
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RectNative
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            public int Size;
            public RectNative Monitor;
            public RectNative WorkArea;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out PointNative point);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(PointNative point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);

        [DllImport("PowrProf.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    }
}
