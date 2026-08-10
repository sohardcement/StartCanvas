using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinTileLauncher.Models;

namespace WinTileLauncher.Controls;

internal sealed class TilePositionPanel : Panel
{
    public const double CellSize = 80;
    public const int DefaultColumnCount = 4;
    public const int DefaultRowCount = 11;

    public static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached(
        "Column", typeof(int), typeof(TilePositionPanel),
        new FrameworkPropertyMetadata(0,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure |
            FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty RowProperty = DependencyProperty.RegisterAttached(
        "Row", typeof(int), typeof(TilePositionPanel),
        new FrameworkPropertyMetadata(0,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure |
            FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty ShowAlignmentGridProperty = DependencyProperty.Register(
        nameof(ShowAlignmentGrid), typeof(bool), typeof(TilePositionPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowAlignmentGrid
    {
        get => (bool)GetValue(ShowAlignmentGridProperty);
        set => SetValue(ShowAlignmentGridProperty, value);
    }

    public static void SetColumn(DependencyObject element, int value) => element.SetValue(ColumnProperty, value);

    public static int GetColumn(DependencyObject element) => (int)element.GetValue(ColumnProperty);

    public static void SetRow(DependencyObject element, int value) => element.SetValue(RowProperty, value);

    public static int GetRow(DependencyObject element) => (int)element.GetValue(RowProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        var tile = FirstTile();
        var columns = tile?.GroupWidthColumns ?? DefaultColumnCount;
        var rows = tile?.GroupHeightRows ?? DefaultRowCount;
        var requiredWidth = columns * CellSize;
        var requiredHeight = rows * CellSize;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var column = Math.Max(0, GetColumn(child));
            var row = Math.Max(0, GetRow(child));
            requiredWidth = Math.Max(requiredWidth, column * CellSize + child.DesiredSize.Width);
            requiredHeight = Math.Max(requiredHeight, row * CellSize + child.DesiredSize.Height);
        }

        return new Size(requiredWidth, requiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = FirstTile()?.GroupWidthColumns ?? DefaultColumnCount;
        foreach (UIElement child in InternalChildren)
        {
            var column = Math.Clamp(GetColumn(child), 0, Math.Max(0, columns - 1));
            var row = Math.Max(0, GetRow(child));
            child.Arrange(new Rect(
                column * CellSize,
                row * CellSize,
                child.DesiredSize.Width,
                child.DesiredSize.Height));
        }

        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!ShowAlignmentGrid || RenderSize.Width <= 0 || RenderSize.Height <= 0)
            return;

        var brush = new SolidColorBrush(Color.FromArgb(34, 208, 239, 255));
        var pen = new Pen(brush, 1) { DashStyle = new DashStyle([2, 5], 0) };
        brush.Freeze();
        pen.Freeze();
        for (var x = 0d; x <= RenderSize.Width; x += CellSize)
            drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, RenderSize.Height));
        for (var y = 0d; y <= RenderSize.Height; y += CellSize)
            drawingContext.DrawLine(pen, new Point(0, y), new Point(RenderSize.Width, y));
    }

    private TileLayoutItem? FirstTile()
    {
        foreach (UIElement child in InternalChildren)
        {
            if (child is ContentPresenter { Content: TileLayoutItem tile })
                return tile;
            if (child is FrameworkElement { DataContext: TileLayoutItem dataContext })
                return dataContext;
        }
        return null;
    }
}
