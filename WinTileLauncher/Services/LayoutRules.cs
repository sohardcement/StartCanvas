using System.Collections.ObjectModel;
using WinTileLauncher.Models;

namespace WinTileLauncher.Services;

internal static class LayoutRules
{
    public const int CurrentVersion = 11;
    public const int DefaultGridColumns = 4;
    public const int DefaultGridRows = 11;
    public const double GridCellSize = 80;
    public const double MinimumCanvasZoom = 0.5;
    public const double MaximumCanvasZoom = 1.6;
    public const double CanvasZoomStep = 0.1;
    private const double GridExtentTolerance = 2;
    private const double GroupPitch = 354;
    private const double GroupGap = 34;

    public static List<TileLayoutItem> Normalize(IEnumerable<TileLayoutItem> source)
    {
        var normalized = new List<TileLayoutItem>();
        foreach (var tile in source.OrderBy(tile => tile.Order))
            AppendNormalized(tile, tile.Group, normalized);

        ResolveGroupDimensions(normalized);
        ResolveGroupColumns(normalized);
        foreach (var group in normalized.Select(tile => tile.Group).Distinct(StringComparer.Ordinal))
            ResolveGroupLayout(normalized, group);

        for (var index = 0; index < normalized.Count; index++)
            normalized[index].Order = index;
        return normalized;
    }

    public static bool Move(ObservableCollection<TileLayoutItem> tiles,
        TileLayoutItem source, TileLayoutItem target) =>
        Place(tiles, source, target.Group, target.GridColumn, target.GridRow);

    public static (int Column, int Row, int GroupColumns, int GroupRows) FitDropTarget(
        TileLayoutItem source, int groupColumns, int groupRows, int column, int row)
    {
        var (columnSpan, rowSpan) = CellSpan(source);
        groupColumns = Math.Clamp(Math.Max(groupColumns, columnSpan), 2, 12);
        groupRows = Math.Clamp(Math.Max(groupRows, rowSpan), 4, 32);
        return (
            Math.Clamp(column, 0, Math.Max(0, groupColumns - columnSpan)),
            Math.Clamp(row, 0, Math.Max(0, groupRows - rowSpan)),
            groupColumns,
            groupRows);
    }

    public static (int Column, int Row, int GroupColumns, int GroupRows) FitNewGroupDropTarget(
        TileLayoutItem source, int column, int row) =>
        FitDropTarget(source, DefaultGridColumns, DefaultGridRows, column, row);

    public static (int Column, int Row, int GroupColumns, int GroupRows)? FitAvailableDropTarget(
        IEnumerable<TileLayoutItem> tiles, TileLayoutItem source, string? targetGroup,
        int groupColumns, int groupRows, int column, int row)
    {
        var fitted = FitDropTarget(source, groupColumns, groupRows, column, row);
        if (targetGroup is null)
            return fitted;

        var occupied = new HashSet<(int Column, int Row)>();
        foreach (var tile in tiles.Where(tile => !ReferenceEquals(tile, source) &&
                                                tile.Group.Equals(targetGroup, StringComparison.Ordinal)))
        {
            var span = CellSpan(tile);
            Reserve(occupied, tile.GridColumn, tile.GridRow, span.Columns, span.Rows);
        }

        var sourceSpan = CellSpan(source);
        for (var rows = fitted.GroupRows; rows <= 32; rows++)
        {
            var preferredColumn = Math.Clamp(column, 0,
                Math.Max(0, fitted.GroupColumns - sourceSpan.Columns));
            var preferredRow = Math.Clamp(row, 0, Math.Max(0, rows - sourceSpan.Rows));
            var available = FindNearestAvailable(occupied, sourceSpan.Columns, sourceSpan.Rows,
                fitted.GroupColumns, rows, preferredColumn, preferredRow);
            if (available is not null)
                return (available.Value.Column, available.Value.Row, fitted.GroupColumns, rows);
        }

        return null;
    }

    public static double NormalizeCanvasZoom(double zoom)
    {
        if (!double.IsFinite(zoom))
            return 1;
        var stepped = Math.Round(zoom / CanvasZoomStep,
            MidpointRounding.AwayFromZero) * CanvasZoomStep;
        return Math.Round(Math.Clamp(stepped, MinimumCanvasZoom, MaximumCanvasZoom),
            1, MidpointRounding.AwayFromZero);
    }

    public static double SnapTileExtent(double extent, int maximumCells)
    {
        maximumCells = Math.Max(1, maximumCells);
        if (!double.IsFinite(extent))
            extent = TileLayoutItem.MinimumTileExtent;
        extent = Math.Clamp(extent, TileLayoutItem.MinimumTileExtent,
            maximumCells * GridCellSize - 8);
        var cells = Math.Clamp(
            (int)Math.Round((extent + 8) / GridCellSize,
                MidpointRounding.AwayFromZero),
            1, maximumCells);
        return cells * GridCellSize - 8;
    }

    public static bool NormalizeLegacyGridExtents(IEnumerable<TileLayoutItem> source)
    {
        var changed = false;
        foreach (var tile in source)
        {
            if (tile.Kind == TileKind.Folder)
                changed |= NormalizeLegacyGridExtents(tile.Children);
            if (tile.Size != TileSize.Custom)
                continue;

            var width = NormalizeNearGridExtent(tile.TileWidth, 12);
            var height = NormalizeNearGridExtent(tile.TileHeight, 32);
            if (tile.TileWidth != width)
            {
                tile.TileWidth = width;
                changed = true;
            }
            if (tile.TileHeight != height)
            {
                tile.TileHeight = height;
                changed = true;
            }
        }
        return changed;
    }

    public static double PointerResizeDelta(double startCoordinate,
        double currentCoordinate, double zoom)
    {
        if (!double.IsFinite(startCoordinate) || !double.IsFinite(currentCoordinate))
            return 0;
        if (!double.IsFinite(zoom) || zoom <= 0)
            zoom = 1;
        return (currentCoordinate - startCoordinate) / zoom;
    }

    public static bool ExpandGroupSize(IEnumerable<TileLayoutItem> source,
        string group, int minimumColumns, int minimumRows)
    {
        var tiles = source.Where(tile => tile.Group.Equals(group, StringComparison.Ordinal)).ToList();
        if (tiles.Count == 0)
            return false;

        var columns = Math.Clamp(Math.Max(
            tiles.Max(tile => tile.GroupWidthColumns), minimumColumns), 2, 12);
        var rows = Math.Clamp(Math.Max(
            tiles.Max(tile => tile.GroupHeightRows), minimumRows), 4, 32);
        var changed = false;
        foreach (var tile in tiles)
        {
            if (tile.GroupWidthColumns != columns)
            {
                tile.GroupWidthColumns = columns;
                changed = true;
            }
            if (tile.GroupHeightRows != rows)
            {
                tile.GroupHeightRows = rows;
                changed = true;
            }
        }

        changed |= ResolveGroupColumns(source, group);
        return changed;
    }

    public static bool Place(ObservableCollection<TileLayoutItem> tiles,
        TileLayoutItem source, string targetGroup, int column, int row,
        int? targetGroupColumn = null)
    {
        var sourceIndex = tiles.IndexOf(source);
        if (sourceIndex < 0 || string.IsNullOrWhiteSpace(targetGroup))
            return false;

        var oldGroup = source.Group;
        var existingTarget = tiles.FirstOrDefault(tile =>
            !ReferenceEquals(tile, source) && tile.Group.Equals(targetGroup, StringComparison.Ordinal));
        if (existingTarget is null && oldGroup.Equals(targetGroup, StringComparison.Ordinal))
            existingTarget = source;
        var groupColumn = existingTarget?.GroupColumn ?? targetGroupColumn ?? source.GroupColumn;
        if (groupColumn < 0)
            groupColumn = FindFirstFreeGroupColumn(tiles, targetGroup);
        var fitted = FitAvailableDropTarget(tiles, source, targetGroup,
            existingTarget?.GroupWidthColumns ?? DefaultGridColumns,
            existingTarget?.GroupHeightRows ?? DefaultGridRows,
            column, row);
        if (fitted is null)
            return false;
        var groupWidth = fitted.Value.GroupColumns;
        var groupHeight = fitted.Value.GroupRows;
        column = fitted.Value.Column;
        row = fitted.Value.Row;
        var changed = !oldGroup.Equals(targetGroup, StringComparison.Ordinal) ||
                      source.GridColumn != column || source.GridRow != row ||
                      source.GroupWidthColumns != groupWidth || source.GroupHeightRows != groupHeight ||
                      tiles.Any(tile => tile.Group.Equals(targetGroup, StringComparison.Ordinal) &&
                                        (tile.GroupWidthColumns != groupWidth ||
                                         tile.GroupHeightRows != groupHeight));

        if (!oldGroup.Equals(targetGroup, StringComparison.Ordinal))
        {
            tiles.RemoveAt(sourceIndex);
            source.Group = targetGroup;
            var targetIndex = tiles.ToList().FindIndex(tile =>
                tile.Group.Equals(targetGroup, StringComparison.Ordinal));
            tiles.Insert(targetIndex < 0 ? tiles.Count : targetIndex, source);
        }

        source.GridColumn = column;
        source.GridRow = row;
        source.GroupColumn = groupColumn;
        source.GroupWidthColumns = groupWidth;
        source.GroupHeightRows = groupHeight;
        foreach (var tile in tiles.Where(tile => tile.Group.Equals(targetGroup, StringComparison.Ordinal)))
        {
            tile.GroupColumn = groupColumn;
            tile.GroupWidthColumns = groupWidth;
            tile.GroupHeightRows = groupHeight;
        }
        changed |= ResolveGroupColumns(tiles, targetGroup);
        changed |= ResolveGroupLayout(tiles, targetGroup);
        if (!oldGroup.Equals(targetGroup, StringComparison.Ordinal))
            changed |= ResolveGroupLayout(tiles, oldGroup);

        for (var index = 0; index < tiles.Count; index++)
            tiles[index].Order = index;
        return changed;
    }

    public static bool SetGroupSize(IEnumerable<TileLayoutItem> source,
        string group, int columns, int rows)
    {
        var tiles = source.Where(tile => tile.Group.Equals(group, StringComparison.Ordinal)).ToList();
        if (tiles.Count == 0)
            return false;
        columns = Math.Max(Math.Clamp(columns, 2, 12),
            tiles.Max(tile => CellSpan(tile).Columns));
        rows = Math.Max(Math.Clamp(rows, 4, 32),
            tiles.Max(tile => CellSpan(tile).Rows));
        var changed = false;
        foreach (var tile in tiles)
        {
            if (tile.GroupWidthColumns != columns)
            {
                tile.GroupWidthColumns = columns;
                changed = true;
            }
            if (tile.GroupHeightRows != rows)
            {
                tile.GroupHeightRows = rows;
                changed = true;
            }
        }
        changed |= ResolveGroupLayout(source, group);
        changed |= ResolveGroupColumns(source, group);
        return changed;
    }

    public static int GroupSlotSpan(TileLayoutItem tile) => Math.Max(1,
        (int)Math.Ceiling((tile.GroupWidthColumns * GridCellSize + GroupGap) / GroupPitch));

    public static bool ResolveGroupColumns(IEnumerable<TileLayoutItem> source,
        string? preferredGroup = null)
    {
        var groups = source.GroupBy(tile => tile.Group, StringComparer.Ordinal).ToList();
        var occupied = new HashSet<int>();
        var changed = false;

        if (preferredGroup is not null)
        {
            var preferred = groups.FirstOrDefault(group =>
                group.Key.Equals(preferredGroup, StringComparison.Ordinal));
            if (preferred is not null)
            {
                var column = Math.Max(0, preferred.First().GroupColumn);
                changed |= SetGroupColumn(preferred, column);
                ReserveSlots(occupied, column, GroupSlotSpan(preferred.First()));
            }
        }

        foreach (var group in groups)
        {
            if (preferredGroup is not null && group.Key.Equals(preferredGroup, StringComparison.Ordinal))
                continue;
            var requested = group.First().GroupColumn;
            var span = GroupSlotSpan(group.First());
            var column = requested >= 0 && CanReserveSlots(occupied, requested, span)
                ? requested
                : FirstFree(occupied, span);
            changed |= SetGroupColumn(group, column);
            ReserveSlots(occupied, column, span);
        }

        return changed;
    }

    public static bool ResolveGroupLayout(IEnumerable<TileLayoutItem> source,
        string group, TileLayoutItem? preferred = null)
    {
        var tiles = source.Where(tile => tile.Group.Equals(group, StringComparison.Ordinal))
            .OrderBy(tile => tile.Order)
            .ToList();
        if (tiles.Count == 0)
            return false;

        var gridColumns = Math.Max(tiles[0].GroupWidthColumns,
            tiles.Max(tile => CellSpan(tile).Columns));
        var gridRows = Math.Max(tiles[0].GroupHeightRows,
            tiles.Max(tile => CellSpan(tile).Rows));
        var occupied = new HashSet<(int Column, int Row)>();
        var changed = false;
        if (preferred is not null && tiles.Contains(preferred))
        {
            var (columns, rows) = CellSpan(preferred);
            var column = Math.Clamp(preferred.GridColumn, 0, gridColumns - columns);
            var row = Math.Clamp(preferred.GridRow, 0, Math.Max(0, gridRows - rows));
            changed |= SetPosition(preferred, column, row);
            Reserve(occupied, column, row, columns, rows);
        }

        foreach (var tile in tiles)
        {
            if (ReferenceEquals(tile, preferred))
                continue;

            var (columns, rows) = CellSpan(tile);
            if (CanOccupy(occupied, tile.GridColumn, tile.GridRow, columns, rows,
                    gridColumns, gridRows))
            {
                Reserve(occupied, tile.GridColumn, tile.GridRow, columns, rows);
                continue;
            }

            var position = FindFirstAvailable(occupied, columns, rows, gridColumns, gridRows);
            while (position is null && gridRows < 32)
            {
                gridRows++;
                position = FindFirstAvailable(occupied, columns, rows, gridColumns, gridRows);
            }
            var resolved = position ?? (Column: 0, Row: Math.Max(0, gridRows - rows));
            changed |= SetPosition(tile, resolved.Column, resolved.Row);
            Reserve(occupied, resolved.Column, resolved.Row, columns, rows);
        }

        if (gridColumns != tiles[0].GroupWidthColumns || gridRows != tiles[0].GroupHeightRows)
        {
            foreach (var tile in tiles)
            {
                tile.GroupWidthColumns = gridColumns;
                tile.GroupHeightRows = gridRows;
            }
            changed = true;
        }

        return changed;
    }

    public static (int Columns, int Rows) CellSpan(TileLayoutItem tile) => (
        Math.Clamp((int)Math.Ceiling((NormalizeNearGridExtent(tile.TileWidth, 12) + 8) /
                                    GridCellSize), 1, 12),
        Math.Clamp((int)Math.Ceiling((NormalizeNearGridExtent(tile.TileHeight, 32) + 8) /
                                    GridCellSize), 1, 32));

    private static double NormalizeNearGridExtent(double extent, int maximumCells)
    {
        var snapped = SnapTileExtent(extent, maximumCells);
        return Math.Abs(extent - snapped) <= GridExtentTolerance ? snapped : extent;
    }

    private static void ResolveGroupDimensions(IEnumerable<TileLayoutItem> source)
    {
        foreach (var group in source.GroupBy(tile => tile.Group, StringComparer.Ordinal))
        {
            var columns = Math.Max(Math.Clamp(group.First().GroupWidthColumns, 2, 12),
                group.Max(tile => CellSpan(tile).Columns));
            var rows = Math.Max(Math.Clamp(group.First().GroupHeightRows, 4, 32),
                group.Max(tile => CellSpan(tile).Rows));
            foreach (var tile in group)
            {
                tile.GroupWidthColumns = columns;
                tile.GroupHeightRows = rows;
            }
        }
    }

    private static bool SetPosition(TileLayoutItem tile, int column, int row)
    {
        if (tile.GridColumn == column && tile.GridRow == row)
            return false;
        tile.GridColumn = column;
        tile.GridRow = row;
        return true;
    }

    private static bool SetGroupColumn(IEnumerable<TileLayoutItem> tiles, int column)
    {
        var changed = false;
        foreach (var tile in tiles)
        {
            if (tile.GroupColumn == column)
                continue;
            tile.GroupColumn = column;
            changed = true;
        }
        return changed;
    }

    private static int FindFirstFreeGroupColumn(IEnumerable<TileLayoutItem> tiles, string excludingGroup)
    {
        var occupied = new HashSet<int>();
        foreach (var group in tiles.Where(tile => !tile.Group.Equals(excludingGroup, StringComparison.Ordinal))
                     .GroupBy(tile => tile.Group, StringComparer.Ordinal))
        {
            var tile = group.First();
            if (tile.GroupColumn >= 0)
                ReserveSlots(occupied, tile.GroupColumn, GroupSlotSpan(tile));
        }
        return FirstFree(occupied, 1);
    }

    private static int FirstFree(HashSet<int> occupied, int span)
    {
        for (var column = 0; ; column++)
        {
            if (CanReserveSlots(occupied, column, span))
                return column;
        }
    }

    private static bool CanReserveSlots(HashSet<int> occupied, int column, int span)
    {
        for (var index = column; index < column + span; index++)
        {
            if (occupied.Contains(index))
                return false;
        }
        return true;
    }

    private static void ReserveSlots(HashSet<int> occupied, int column, int span)
    {
        for (var index = column; index < column + span; index++)
            occupied.Add(index);
    }

    private static (int Column, int Row)? FindFirstAvailable(
        HashSet<(int Column, int Row)> occupied, int columns, int rows,
        int gridColumns, int gridRows)
    {
        for (var row = 0; row <= gridRows - rows; row++)
        {
            for (var column = 0; column <= gridColumns - columns; column++)
            {
                if (CanOccupy(occupied, column, row, columns, rows, gridColumns, gridRows))
                    return (column, row);
            }
        }

        return null;
    }

    private static (int Column, int Row)? FindNearestAvailable(
        HashSet<(int Column, int Row)> occupied, int columns, int rows,
        int gridColumns, int gridRows, int preferredColumn, int preferredRow)
    {
        (int Column, int Row)? best = null;
        (int Distance, int Vertical, int Horizontal, int Row, int Column)? bestScore = null;
        for (var row = 0; row <= gridRows - rows; row++)
        {
            for (var column = 0; column <= gridColumns - columns; column++)
            {
                if (!CanOccupy(occupied, column, row, columns, rows, gridColumns, gridRows))
                    continue;
                var vertical = Math.Abs(row - preferredRow);
                var horizontal = Math.Abs(column - preferredColumn);
                var score = (vertical + horizontal, vertical, horizontal, row, column);
                if (bestScore is not null && score.CompareTo(bestScore.Value) >= 0)
                    continue;
                best = (column, row);
                bestScore = score;
            }
        }
        return best;
    }

    private static bool CanOccupy(HashSet<(int Column, int Row)> occupied,
        int column, int row, int columns, int rows, int gridColumns, int gridRows)
    {
        if (column < 0 || row < 0 || column + columns > gridColumns || row + rows > gridRows)
            return false;
        for (var y = row; y < row + rows; y++)
        for (var x = column; x < column + columns; x++)
        {
            if (occupied.Contains((x, y)))
                return false;
        }
        return true;
    }

    private static void Reserve(HashSet<(int Column, int Row)> occupied,
        int column, int row, int columns, int rows)
    {
        for (var y = row; y < row + rows; y++)
        for (var x = column; x < column + columns; x++)
            occupied.Add((x, y));
    }

    private static void AppendNormalized(TileLayoutItem tile, string inheritedGroup,
        ICollection<TileLayoutItem> destination)
    {
        if (tile.Kind == TileKind.Folder)
        {
            foreach (var child in tile.Children.OrderBy(child => child.Order))
                AppendNormalized(child, inheritedGroup, destination);
            return;
        }

        tile.Group = string.IsNullOrWhiteSpace(inheritedGroup) ? "我的应用" : inheritedGroup;
        if (!tile.HasCustomAppearance)
        {
            tile.VisualStyle = TileVisualStyle.Glass;
            tile.Accent = ApprovedAccent(tile);
        }
        tile.Children.Clear();

        if (tile.Size == TileSize.Custom)
        {
            tile.SetCustomSize(tile.TileWidth, tile.TileHeight);
        }
        else
        {
            var dimensions = TileLayoutItem.PresetDimensions(tile.Size);
            tile.TileWidth = dimensions.Width;
            tile.TileHeight = dimensions.Height;
        }

        destination.Add(tile);
    }

    private static string ApprovedAccent(TileLayoutItem tile)
    {
        if (tile.Target.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return "#C58B2A";
        if (tile.Target.Equals("ms-settings:", StringComparison.OrdinalIgnoreCase))
            return "#216EA8";
        if (tile.Target.Equals("wt.exe", StringComparison.OrdinalIgnoreCase))
            return "#172A3C";
        if (tile.Target.Equals("ms-windows-store:", StringComparison.OrdinalIgnoreCase))
            return "#176BC2";
        if (tile.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase))
            return "#0E7894";
        if (tile.Name.Contains("微信", StringComparison.OrdinalIgnoreCase) ||
            tile.Name.Contains("WeChat", StringComparison.OrdinalIgnoreCase))
            return "#21806D";
        if (tile.Name.Equals("QQ", StringComparison.OrdinalIgnoreCase))
            return "#2B70B3";
        if (tile.Kind == TileKind.Clock)
            return "#176C9F";

        return tile.Accent.Equals("#5C2D91", StringComparison.OrdinalIgnoreCase) ||
               tile.Accent.Equals("#8764B8", StringComparison.OrdinalIgnoreCase)
            ? "#3B6F9B"
            : tile.Accent;
    }
}
