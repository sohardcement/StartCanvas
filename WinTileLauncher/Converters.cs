using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WinTileLauncher.Models;

namespace WinTileLauncher;

internal sealed class TileWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        TileSize.Small => 72d,
        TileSize.Medium => 152d,
        TileSize.Wide => 312d,
        TileSize.Large => 312d,
        _ => 152d
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

internal sealed class TileHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        TileSize.Small => 72d,
        TileSize.Medium => 152d,
        TileSize.Wide => 152d,
        TileSize.Large => 312d,
        _ => 152d
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

internal sealed class ClockFontSizeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var width = values.ElementAtOrDefault(0) is double tileWidth ? tileWidth : 312;
        var height = values.ElementAtOrDefault(1) is double tileHeight ? tileHeight : 312;
        var detail = parameter?.ToString()?.Equals("Detail", StringComparison.OrdinalIgnoreCase) == true;
        return detail
            ? Math.Clamp(Math.Min(width, height) * 0.045, 13, 30)
            : Math.Clamp(Math.Min(width * 0.15, height * 0.20), 44, 160);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

internal sealed class DrawerWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var availableWidth = value is double width && double.IsFinite(width) ? width : 1280;
        return Math.Clamp(availableWidth * 0.48, 440, 720);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

internal sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => TileAppearanceBrushConverter.CreateBrush(value?.ToString(), TileVisualStyle.Glass);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

internal sealed class TileAppearanceBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var accent = values.ElementAtOrDefault(0)?.ToString();
        var style = values.ElementAtOrDefault(1) is TileVisualStyle visualStyle
            ? visualStyle
            : TileVisualStyle.Glass;
        return CreateBrush(accent, style);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();

    internal static Brush CreateBrush(string? accent, TileVisualStyle style)
    {
        var color = Color.FromRgb(22, 116, 209);
        try
        {
            color = (Color)ColorConverter.ConvertFromString(accent ?? "#1674D1");
        }
        catch (FormatException)
        {
            // Fall back to Tile10 blue.
        }

        if (style == TileVisualStyle.Classic)
        {
            var solid = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            solid.Freeze();
            return solid;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        if (style == TileVisualStyle.Minimal)
        {
            brush.GradientStops.Add(new GradientStop(Shade(color, 0.12, 150), 0));
            brush.GradientStops.Add(new GradientStop(Shade(color, 0.46, 205), 1));
        }
        else if (style == TileVisualStyle.IconOnly)
        {
            brush.GradientStops.Add(new GradientStop(Shade(color, 0.38, 155), 0));
            brush.GradientStops.Add(new GradientStop(Shade(color, 0.68, 220), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Tint(color, 0.18, 224), 0));
            brush.GradientStops.Add(new GradientStop(Tint(color, 0.02, 210), 0.56));
            brush.GradientStops.Add(new GradientStop(Shade(color, 0.24, 226), 1));
        }
        brush.Freeze();
        return brush;
    }

    private static Color Tint(Color color, double amount, byte alpha) => Color.FromArgb(alpha,
        Blend(color.R, 255, amount), Blend(color.G, 255, amount), Blend(color.B, 255, amount));

    private static Color Shade(Color color, double amount, byte alpha) => Color.FromArgb(alpha,
        Blend(color.R, 0, amount), Blend(color.G, 0, amount), Blend(color.B, 0, amount));

    private static byte Blend(byte source, byte target, double amount) =>
        (byte)Math.Clamp(Math.Round(source + (target - source) * amount), byte.MinValue, byte.MaxValue);
}

internal sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

internal sealed class NullToInverseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
