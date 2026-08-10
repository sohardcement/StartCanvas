using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WinTileLauncher.Models;

namespace WinTileLauncher.Controls;

internal sealed class GroupPositionPanel : Panel
{
    public const double GroupPitch = 354;
    public const double GroupHeaderHeight = 30;
    public const int MinimumColumns = 5;

    protected override Size MeasureOverride(Size availableSize)
    {
        var requiredWidth = MinimumColumns * GroupPitch;
        var requiredHeight = 0d;
        var ordered = OrderedChildren().ToList();
        foreach (var child in ordered)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            requiredHeight = Math.Max(requiredHeight, child.DesiredSize.Height);
        }
        var offsets = PackedOffsets(ordered.Select(child => child.DesiredSize.Width));
        for (var index = 0; index < ordered.Count; index++)
            requiredWidth = Math.Max(requiredWidth,
                offsets[index] + ordered[index].DesiredSize.Width);

        return new Size(requiredWidth, requiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var ordered = OrderedChildren().ToList();
        var offsets = PackedOffsets(ordered.Select(child => child.DesiredSize.Width));
        for (var index = 0; index < ordered.Count; index++)
        {
            var child = ordered[index];
            child.Arrange(new Rect(
                offsets[index],
                0,
                child.DesiredSize.Width,
                child.DesiredSize.Height));
        }

        return finalSize;
    }

    public bool TryGetGroupAtX(double x, out string groupName)
    {
        foreach (var child in OrderedChildren())
        {
            var left = VisualTreeHelper.GetOffset(child).X;
            var width = Math.Max(child.RenderSize.Width, child.DesiredSize.Width);
            if (x < left || x >= left + width)
                continue;
            if (child is FrameworkElement { DataContext: CollectionViewGroup group } &&
                group.Name is string name)
            {
                groupName = name;
                return true;
            }
        }

        groupName = string.Empty;
        return false;
    }

    public int GetInsertionColumn(double x)
    {
        var ordered = OrderedChildren().ToList();
        foreach (var child in ordered)
        {
            var left = VisualTreeHelper.GetOffset(child).X;
            var width = Math.Max(child.RenderSize.Width, child.DesiredSize.Width);
            if (x < left + width / 2)
                return GroupColumn(child);
        }

        return ordered.Count == 0 ? 0 : ordered.Max(GroupColumn) + 1;
    }

    public double GetInsertionX(int groupColumn)
    {
        var right = 0d;
        foreach (var child in OrderedChildren())
        {
            var left = VisualTreeHelper.GetOffset(child).X;
            if (groupColumn <= GroupColumn(child))
                return left;
            right = left + Math.Max(child.RenderSize.Width, child.DesiredSize.Width);
        }
        return right;
    }

    internal static IReadOnlyList<double> PackedOffsets(IEnumerable<double> desiredWidths)
    {
        var offsets = new List<double>();
        var horizontalOffset = 0d;
        foreach (var width in desiredWidths)
        {
            offsets.Add(horizontalOffset);
            horizontalOffset += Math.Max(0, width);
        }
        return offsets;
    }

    private IEnumerable<UIElement> OrderedChildren() => InternalChildren
        .Cast<UIElement>()
        .Select((child, index) => (Child: child, Index: index))
        .OrderBy(item => GroupColumn(item.Child))
        .ThenBy(item => item.Index)
        .Select(item => item.Child);

    private static int GroupColumn(UIElement child)
    {
        if (child is FrameworkElement { DataContext: CollectionViewGroup group })
        {
            var tile = group.Items.OfType<TileLayoutItem>().FirstOrDefault();
            if (tile is not null)
                return Math.Max(0, tile.GroupColumn);
        }
        return 0;
    }
}
