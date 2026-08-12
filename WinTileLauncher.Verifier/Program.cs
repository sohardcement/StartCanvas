using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using WinTileLauncher;
using WinTileLauncher.Controls;
using WinTileLauncher.Models;
using WinTileLauncher.Services;

var checks = new (string Name, Func<bool> Run)[]
{
    ("Windows key alone and Win+R / Win+I forwarding", GlobalKeyboardHook.RunStateMachineSelfTest),
    ("App discovery defers icon loading until rows are visible", VerifyLazyAppIcons),
    ("All Apps drawer stays responsive across display widths", VerifyDrawerWidth),
    ("Explicitly empty and malformed tile layouts remain empty", VerifyEmptyLayoutPersistence),
    ("Keyboard app launch respects the current selection", VerifyKeyboardLaunchSelection),
    ("Global undo preserves text editing and tile order", VerifyUndoBehavior),
    ("Startup registration must point to the current executable", VerifyStartupRegistration),
    ("Legacy folders flatten without losing apps", VerifyFolderMigration),
    ("Tiles keep free grid positions within and across groups", VerifyTilePlacement),
    ("Blank horizontal space creates a positioned group", VerifyBlankSpaceGroup),
    ("Group width and height resize persist without overlap", VerifyGroupResize),
    ("Variable-width groups pack without phantom slots", VerifyPackedGroupOffsets),
    ("Win10 tile presets and freeform size persist", VerifyTileSizes),
    ("Tile resizing snaps to the alignment grid", VerifyTileResizeGrid),
    ("Legacy near-grid sizes migrate without phantom cells", VerifyLegacyGridExtentMigration),
    ("Near-grid width no longer blocks an adjacent drop", VerifyNearGridDrop),
    ("Pointer-based resizing stays stable after handle reflow", VerifyStablePointerResize),
    ("Large tiles can fill an expanded group", VerifyLargeTileSizing),
    ("Oversized tiles safely cross narrower groups", VerifyOversizedTileDrop),
    ("Collisions snap the moving tile without squeezing neighbors", VerifyCollisionSnap),
    ("Oversized tiles safely create a new group", VerifyOversizedBlankGroupDrop),
    ("Canvas zoom stays on supported steps", VerifyCanvasZoom),
    ("Clock typography scales with large tiles", VerifyClockTypography),
    ("Approved translucent glass brush is active", VerifyGlassBrush),
    ("Custom tile color and style survive normalization", VerifyCustomAppearance),
    ("Tile appearance modes render distinct brushes", VerifyAppearanceBrushes)
};

var passed = true;
foreach (var check in checks)
{
    var checkPassed = check.Run();
    Console.WriteLine($"[{(checkPassed ? "PASS" : "FAIL")}] {check.Name}");
    passed &= checkPassed;
}

return passed ? 0 : 1;

static bool VerifyLazyAppIcons()
{
    var apps = new AppDiscoveryService().DiscoverAsync().GetAwaiter().GetResult();
    return apps.Count > 0 && apps.All(app => app.Icon is null);
}

static bool VerifyDrawerWidth()
{
    var converter = new DrawerWidthConverter();
    return (double)converter.Convert(800d, typeof(double), null!, CultureInfo.InvariantCulture) == 440 &&
           (double)converter.Convert(1200d, typeof(double), null!, CultureInfo.InvariantCulture) == 576 &&
           (double)converter.Convert(2000d, typeof(double), null!, CultureInfo.InvariantCulture) == 720;
}

static bool VerifyEmptyLayoutPersistence()
{
    var empty = new LayoutData { Version = 1, Tiles = [] };
    var malformed = LayoutService.NormalizeLoadedLayout(new LayoutData { Tiles = null! });
    var legacyTile = Tile("legacy", "Legacy", "legacy.exe", "Legacy", 0);
    var legacyClock = new TileLayoutItem { Kind = TileKind.Clock };
    return MainWindow.ShouldSeedDefaultLayout(null) &&
           !MainWindow.ShouldSeedDefaultLayout(empty) &&
           !MainWindow.ShouldAddLegacyClock(empty, empty.Tiles) &&
           MainWindow.ShouldAddLegacyClock(empty, [legacyTile]) &&
           !MainWindow.ShouldAddLegacyClock(empty, [legacyTile, legacyClock]) &&
           malformed is { Tiles.Count: 0 };
}

static bool VerifyKeyboardLaunchSelection()
{
    var first = new LauncherItem { Id = "first-app", Name = "First", Target = "first.exe" };
    var selected = new LauncherItem { Id = "selected-app", Name = "Selected", Target = "selected.exe" };
    return ReferenceEquals(MainWindow.SelectAppForKeyboardLaunch(selected, [first, selected]), selected) &&
           ReferenceEquals(MainWindow.SelectAppForKeyboardLaunch(null, [first, selected]), first) &&
           MainWindow.SelectAppForKeyboardLaunch(null, []) is null;
}

static bool VerifyUndoBehavior()
{
    var first = Tile("undo-first", "First", "first.exe", "Undo", 0);
    var restored = Tile("undo-restored", "Restored", "restored.exe", "Undo", 1);
    var last = Tile("undo-last", "Last", "last.exe", "Undo", 1);
    var tiles = new List<TileLayoutItem> { first, last };
    var index = MainWindow.RestoreTileAtIndex(tiles, restored, 1);

    return MainWindow.ShouldHandleGlobalUndo(true, false) &&
           !MainWindow.ShouldHandleGlobalUndo(true, true) &&
           !MainWindow.ShouldHandleGlobalUndo(false, false) &&
           index == 1 && tiles.Select(tile => tile.Id)
               .SequenceEqual(["undo-first", "undo-restored", "undo-last"]) &&
           tiles.Select(tile => tile.Order).SequenceEqual([0, 1, 2]);
}

static bool VerifyStartupRegistration()
{
    const string executable = @"C:\Program Files\Tile10\Tile10.exe";
    return StartupService.IsCommandForExecutable(
               @"""C:\Program Files\Tile10\Tile10.exe"" --background", executable) &&
           StartupService.IsCommandForExecutable(
               @"""c:\program files\tile10\tile10.exe"" --background", executable) &&
           !StartupService.IsCommandForExecutable(
               @"""C:\Old Tile10\Tile10.exe"" --background", executable) &&
           !StartupService.IsCommandForExecutable(
               @"""C:\Program Files\Tile10\Tile10.exe""evil", executable) &&
           !StartupService.IsCommandForExecutable("\"unterminated", executable) &&
           !StartupService.IsCommandForExecutable(null, executable);
}

static bool VerifyFolderMigration()
{
    var explorer = Tile("explorer", "文件资源管理器", "explorer.exe", "旧子组", 2);
    var terminal = Tile("terminal", "终端", "wt.exe", "旧子组", 1);
    terminal.SetCustomSize(300, 148);
    var qq = Tile("qq", "QQ", "qq.exe", "旧子组", 3);
    qq.Accent = "#8764B8";
    var folder = new TileLayoutItem
    {
        Id = "legacy-folder",
        Name = "工具",
        Group = "高频工具",
        Kind = TileKind.Folder,
        Accent = "#5C2D91",
        Order = 0,
        Children = new ObservableCollection<TileLayoutItem> { explorer, terminal, qq }
    };
    var clock = new TileLayoutItem
    {
        Id = "clock",
        Name = "时钟",
        Group = "今天",
        Kind = TileKind.Clock,
        Size = TileSize.Wide,
        Order = 1
    };

    var normalized = LayoutRules.Normalize([folder, clock]);
    return normalized.Count == 4 &&
           normalized.All(tile => tile.Kind != TileKind.Folder) &&
           normalized[0].Id == "terminal" &&
           normalized[1].Id == "explorer" &&
           normalized[0].Group == "高频工具" &&
           normalized[1].Group == "高频工具" &&
           normalized[0].Size == TileSize.Custom &&
           normalized[0].TileWidth == 300 &&
           normalized[0].TileHeight == 148 &&
           normalized[2].Name == "QQ" &&
           normalized[2].Accent == "#2B70B3" &&
           normalized.All(tile => tile.VisualStyle == TileVisualStyle.Glass) &&
           normalized.All(tile => tile.GridColumn >= 0 && tile.GridRow >= 0) &&
           normalized.GroupBy(tile => tile.Group).Select(group => group.First().GroupColumn).Distinct().Count() == 2 &&
           normalized.Select(tile => tile.Order).SequenceEqual([0, 1, 2, 3]);
}

static bool VerifyTilePlacement()
{
    var first = Tile("first", "一", "one.exe", "甲", 0);
    var second = Tile("second", "二", "two.exe", "甲", 1);
    var third = Tile("third", "三", "three.exe", "乙", 2);
    var tiles = new ObservableCollection<TileLayoutItem>(LayoutRules.Normalize([first, second, third]));

    var emptySpace = LayoutRules.Place(tiles, first, "甲", 2, 6) &&
                     first.GridColumn == 2 && first.GridRow == 6 &&
                     second.GridRow == 0;
    var acrossGroups = LayoutRules.Place(tiles, first, "乙", 0, 0) &&
                       first.Group == "乙" && first.GridColumn == 2 && first.GridRow == 0 &&
                       third.GridColumn == 0 && third.GridRow == 0;
    return emptySpace && acrossGroups && !Overlaps(first, third) &&
           tiles.Select(tile => tile.Order).SequenceEqual([0, 1, 2]);
}

static bool VerifyBlankSpaceGroup()
{
    var first = Tile("first", "一", "one.exe", "甲", 0);
    var second = Tile("second", "二", "two.exe", "乙", 1);
    var tiles = new ObservableCollection<TileLayoutItem>(LayoutRules.Normalize([first, second]));

    var placed = LayoutRules.Place(tiles, first, "新分组", 2, 3, 5);
    var normalized = LayoutRules.Normalize(tiles);
    var moved = normalized.Single(tile => tile.Id == "first");
    var untouched = normalized.Single(tile => tile.Id == "second");
    return placed && moved.Group == "新分组" && moved.GroupColumn == 5 &&
           moved.GridColumn == 2 && moved.GridRow == 3 && untouched.GroupColumn == 1;
}

static bool VerifyGroupResize()
{
    var first = Tile("first", "一", "one.exe", "甲", 0);
    var second = Tile("second", "二", "two.exe", "甲", 1);
    var neighbor = Tile("neighbor", "三", "three.exe", "乙", 2);
    var tiles = new ObservableCollection<TileLayoutItem>(
        LayoutRules.Normalize([first, second, neighbor]));

    var resized = LayoutRules.SetGroupSize(tiles, "甲", 8, 12);
    var placed = LayoutRules.Place(tiles, first, "甲", 6, 9);
    var normalized = LayoutRules.Normalize(tiles);
    var group = normalized.Where(tile => tile.Group == "甲").ToList();
    var moved = group.Single(tile => tile.Id == "first");
    var otherGroup = normalized.Single(tile => tile.Id == "neighbor");

    return resized && placed &&
           group.All(tile => tile.GroupWidthColumns == 8 && tile.GroupHeightRows == 12) &&
           moved.GridColumn == 6 && moved.GridRow == 9 &&
           LayoutRules.GroupSlotSpan(group[0]) == 2 &&
           otherGroup.GroupColumn >= group[0].GroupColumn + 2;
}

static bool VerifyPackedGroupOffsets()
{
    var offsets = GroupPositionPanel.PackedOffsets([354, 434, 994]);
    return offsets.SequenceEqual([0d, 354d, 788d]);
}

static bool Overlaps(TileLayoutItem first, TileLayoutItem second)
{
    var firstSpan = LayoutRules.CellSpan(first);
    var secondSpan = LayoutRules.CellSpan(second);
    return first.GridColumn < second.GridColumn + secondSpan.Columns &&
           first.GridColumn + firstSpan.Columns > second.GridColumn &&
           first.GridRow < second.GridRow + secondSpan.Rows &&
           first.GridRow + firstSpan.Rows > second.GridRow;
}

static bool VerifyTileSizes()
{
    var custom = Tile("custom", "自定义", "custom.exe", "组", 0);
    custom.SetCustomSize(227, 119);
    var normalized = LayoutRules.Normalize([custom]).Single();

    return TileLayoutItem.PresetDimensions(TileSize.Small) == (72, 72) &&
           TileLayoutItem.PresetDimensions(TileSize.Medium) == (152, 152) &&
           TileLayoutItem.PresetDimensions(TileSize.Wide) == (312, 152) &&
           TileLayoutItem.PresetDimensions(TileSize.Large) == (312, 312) &&
           normalized.Size == TileSize.Custom &&
           normalized.TileWidth == 227 &&
           normalized.TileHeight == 119;
}

static bool VerifyTileResizeGrid()
{
    return LayoutRules.SnapTileExtent(72, 12) == 72 &&
           LayoutRules.SnapTileExtent(111, 12) == 72 &&
           LayoutRules.SnapTileExtent(112, 12) == 152 &&
           LayoutRules.SnapTileExtent(227, 12) == 232 &&
           LayoutRules.SnapTileExtent(900, 12) == 872 &&
           LayoutRules.SnapTileExtent(double.NaN, 32) == 72 &&
           LayoutRules.SnapTileExtent(double.MaxValue, 12) ==
               TileLayoutItem.MaximumTileWidth;
}

static bool VerifyLegacyGridExtentMigration()
{
    var tile = Tile("legacy-grid", "Legacy", "legacy.exe", "Grid", 0);
    tile.SetCustomSize(312.23185550081655, 310.47378215654174);

    var changed = LayoutRules.NormalizeLegacyGridExtents([tile]);
    return changed && tile.TileWidth == 312 && tile.TileHeight == 312 &&
           LayoutRules.CellSpan(tile) == (4, 4);
}

static bool VerifyNearGridDrop()
{
    const string group = "Near Grid";
    var edge = Tile("edge-near-grid", "Edge", "edge.exe", group, 0);
    edge.SetCustomSize(312.23185550081655, 310.47378215654174);
    edge.GridColumn = 0;
    edge.GridRow = 0;
    var moving = Tile("moving-near-grid", "Moving", "moving.exe", group, 1);
    moving.SetCustomSize(312, 152);
    moving.GridColumn = 4;
    moving.GridRow = 4;
    foreach (var tile in new[] { edge, moving })
    {
        tile.GroupWidthColumns = 8;
        tile.GroupHeightRows = 32;
    }
    var tiles = new ObservableCollection<TileLayoutItem> { edge, moving };

    var target = LayoutRules.FitAvailableDropTarget(tiles, moving, group, 8, 32, 4, 0);
    return LayoutRules.CellSpan(edge) == (4, 4) && target == (4, 0, 8, 32);
}

static bool VerifyStablePointerResize()
{
    var horizontalDelta = LayoutRules.PointerResizeDelta(1000, 928, 0.9);
    var verticalDelta = LayoutRules.PointerResizeDelta(500, 500, 0.9);
    var firstWidth = LayoutRules.SnapTileExtent(792 + horizontalDelta, 12);
    var repeatedWidth = LayoutRules.SnapTileExtent(792 +
        LayoutRules.PointerResizeDelta(1000, 928, 0.9), 12);
    var height = LayoutRules.SnapTileExtent(792 + verticalDelta, 32);

    return Math.Abs(horizontalDelta + 80) < 0.001 &&
           firstWidth == 712 && repeatedWidth == firstWidth && height == 792 &&
           LayoutRules.PointerResizeDelta(double.NaN, 928, 0.9) == 0 &&
           LayoutRules.PointerResizeDelta(1000, 928, 0) == -72;
}

static bool VerifyLargeTileSizing()
{
    var clock = new TileLayoutItem
    {
        Id = "large-clock",
        Name = "时钟",
        Group = "大画布",
        Kind = TileKind.Clock,
        Order = 0,
        GridColumn = 0,
        GridRow = 0
    };
    clock.SetCustomSize(872, 952);
    var normalized = LayoutRules.Normalize([clock]).Single();
    var span = LayoutRules.CellSpan(normalized);

    clock.SetCustomSize(2000, 3000);
    return normalized.TileWidth == 952 && normalized.TileHeight == 2552 &&
           span == (11, 12) && normalized.GroupWidthColumns >= 11 &&
           normalized.GroupHeightRows >= 12 &&
           clock.TileWidth == TileLayoutItem.MaximumTileWidth &&
           clock.TileHeight == TileLayoutItem.MaximumTileHeight;
}

static bool VerifyOversizedTileDrop()
{
    var clock = new TileLayoutItem
    {
        Id = "crash-clock",
        Name = "Clock",
        Group = "Source Group",
        Kind = TileKind.Clock,
        Order = 0,
        GridColumn = 0,
        GridRow = 0,
        GroupColumn = 2,
        GroupWidthColumns = 12,
        GroupHeightRows = 13
    };
    clock.SetCustomSize(898.0216748768253, 794.742857142969);
    var target = Tile("target", "Microsoft Edge", "edge.exe", "Target Group", 1);
    target.GridColumn = 0;
    target.GridRow = 0;
    target.GroupColumn = 1;
    target.GroupWidthColumns = 7;
    target.GroupHeightRows = 11;
    var tiles = new ObservableCollection<TileLayoutItem> { clock, target };

    var fitted = LayoutRules.FitDropTarget(clock, 7, 11, 5, 3);
    var available = LayoutRules.FitAvailableDropTarget(tiles, clock, "Target Group", 7, 11, 5, 3);
    var placed = LayoutRules.Place(tiles, clock, "Target Group", 5, 3, 1);

    return LayoutRules.CellSpan(clock) == (12, 11) &&
           fitted == (0, 0, 12, 11) && available == (0, 2, 12, 13) && placed &&
           clock.Group == "Target Group" && clock.GridColumn == 0 && clock.GridRow == 2 &&
           target.GridColumn == 0 && target.GridRow == 0 &&
           tiles.Where(tile => tile.Group == "Target Group")
               .All(tile => tile.GroupWidthColumns == 12 && tile.GroupHeightRows == 13) &&
           !Overlaps(clock, target);
}

static bool VerifyCollisionSnap()
{
    const string group = "Snap Group";
    var edge = Tile("edge", "Edge", "edge.exe", group, 0);
    edge.SetCustomSize(531, 327);
    edge.GridColumn = 0;
    edge.GridRow = 0;
    var side = Tile("side", "Side", "side.exe", group, 1);
    side.GridColumn = 4;
    side.GridRow = 5;
    var moving = Tile("moving", "Moving", "moving.exe", group, 2);
    moving.SetCustomSize(312, 152);
    moving.GridColumn = 0;
    moving.GridRow = 9;
    foreach (var tile in new[] { edge, side, moving })
    {
        tile.GroupColumn = 1;
        tile.GroupWidthColumns = 7;
        tile.GroupHeightRows = 16;
    }
    var tiles = new ObservableCollection<TileLayoutItem> { edge, side, moving };

    var target = LayoutRules.FitAvailableDropTarget(tiles, moving, group, 7, 16, 0, 3);
    var placed = LayoutRules.Place(tiles, moving, group, 0, 3);

    return target == (0, 5, 7, 16) && placed &&
           moving.GridColumn == 0 && moving.GridRow == 5 &&
           edge.GridColumn == 0 && edge.GridRow == 0 &&
           side.GridColumn == 4 && side.GridRow == 5 &&
           !Overlaps(moving, edge) && !Overlaps(moving, side);
}

static bool VerifyOversizedBlankGroupDrop()
{
    var clock = new TileLayoutItem
    {
        Id = "blank-group-crash-clock",
        Name = "Clock",
        Group = "Existing",
        Kind = TileKind.Clock,
        Order = 0,
        GridColumn = 0,
        GridRow = 0,
        GroupColumn = 1,
        GroupWidthColumns = 12,
        GroupHeightRows = 13
    };
    clock.SetCustomSize(898.0216748768253, 794.742857142969);
    var tiles = new ObservableCollection<TileLayoutItem> { clock };

    var fitted = LayoutRules.FitNewGroupDropTarget(clock, 6, 4);
    var placed = LayoutRules.Place(tiles, clock, "New Group", 6, 4, 5);

    return LayoutRules.CellSpan(clock) == (12, 11) &&
           fitted == (0, 0, 12, 11) && placed &&
           clock.Group == "New Group" && clock.GroupColumn == 5 &&
           clock.GridColumn == 0 && clock.GridRow == 0 &&
           clock.GroupWidthColumns == 12 && clock.GroupHeightRows == 11;
}

static bool VerifyCanvasZoom()
{
    return LayoutRules.NormalizeCanvasZoom(double.NaN) == 1 &&
           LayoutRules.NormalizeCanvasZoom(0.1) == LayoutRules.MinimumCanvasZoom &&
           LayoutRules.NormalizeCanvasZoom(3) == LayoutRules.MaximumCanvasZoom &&
           Math.Abs(LayoutRules.NormalizeCanvasZoom(0.56) - 0.6) < 0.001 &&
           Math.Abs(LayoutRules.NormalizeCanvasZoom(1.24) - 1.2) < 0.001;
}

static bool VerifyClockTypography()
{
    var converter = new ClockFontSizeConverter();
    var primary = (double)converter.Convert([872d, 952d], typeof(double), null!, CultureInfo.InvariantCulture);
    var detail = (double)converter.Convert([872d, 952d], typeof(double), "Detail", CultureInfo.InvariantCulture);
    return primary > 100 && primary <= 160 && detail > 20 && detail <= 30;
}

static bool VerifyGlassBrush()
{
    var converted = new StringToBrushConverter().Convert("#176BC2", typeof(Brush), null!, CultureInfo.InvariantCulture);
    return converted is LinearGradientBrush { GradientStops.Count: 3 } brush &&
           brush.GradientStops.All(stop => stop.Color.A < byte.MaxValue) &&
           brush.StartPoint == new System.Windows.Point(0, 0) &&
           brush.EndPoint == new System.Windows.Point(1, 1);
}

static bool VerifyCustomAppearance()
{
    var tile = Tile("custom-look", "自定义外观", "custom.exe", "组", 0);
    tile.Accent = "#A13B72";
    tile.VisualStyle = TileVisualStyle.Minimal;
    tile.HasCustomAppearance = true;
    var normalized = LayoutRules.Normalize([tile]).Single();
    return normalized.Accent == "#A13B72" &&
           normalized.VisualStyle == TileVisualStyle.Minimal &&
           normalized.HasCustomAppearance;
}

static bool VerifyAppearanceBrushes()
{
    var glass = TileAppearanceBrushConverter.CreateBrush("#176BC2", TileVisualStyle.Glass);
    var classic = TileAppearanceBrushConverter.CreateBrush("#176BC2", TileVisualStyle.Classic);
    var minimal = TileAppearanceBrushConverter.CreateBrush("#176BC2", TileVisualStyle.Minimal);
    var iconOnly = TileAppearanceBrushConverter.CreateBrush("#176BC2", TileVisualStyle.IconOnly);
    return glass is LinearGradientBrush { GradientStops.Count: 3 } &&
           classic is SolidColorBrush { Color: { R: 23, G: 107, B: 194 } } &&
           minimal is LinearGradientBrush { GradientStops.Count: 2 } minimalBrush &&
           iconOnly is LinearGradientBrush { GradientStops.Count: 2 } iconBrush &&
           minimalBrush.GradientStops[0].Color != iconBrush.GradientStops[0].Color;
}

static TileLayoutItem Tile(string id, string name, string target, string group, int order) => new()
{
    Id = id,
    Name = name,
    Target = target,
    Group = group,
    Order = order,
    Kind = TileKind.App,
    Size = TileSize.Medium
};
