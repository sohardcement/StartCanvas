using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace WinTileLauncher.Models;

public enum TileSize
{
    Small,
    Medium,
    Wide,
    Large,
    Custom
}

public enum TileVisualStyle
{
    Classic,
    Glass,
    Minimal,
    IconOnly
}

public enum TileKind
{
    App,
    Folder,
    Clock
}

public sealed class LauncherItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isPinned;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
    public string Glyph { get; init; } = "\uE8A5";
    public string Accent { get; init; } = "#1674D1";

    public string SortLetter
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
                return "#";
            var first = Name.Trim()[0];
            return char.IsLetterOrDigit(first) ? char.ToUpperInvariant(first).ToString() : "#";
        }
    }

    [JsonIgnore]
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
                return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    [JsonIgnore]
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value)
                return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinActionAccessibleName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinActionToolTip)));
        }
    }

    [JsonIgnore]
    public string PinActionAccessibleName => IsPinned
        ? $"已固定到开始屏幕：{Name}"
        : $"固定到开始屏幕：{Name}";

    [JsonIgnore]
    public string PinActionToolTip => IsPinned ? "已固定到开始屏幕" : "固定到开始屏幕";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TileLayoutItem : INotifyPropertyChanged
{
    public const double MinimumTileExtent = 72;
    public const double MaximumTileWidth = 952;
    public const double MaximumTileHeight = 2552;

    private string _group = "我的应用";
    private TileSize _size = TileSize.Medium;
    private TileVisualStyle _visualStyle = TileVisualStyle.Glass;
    private double _tileWidth = 152;
    private double _tileHeight = 152;
    private int _gridColumn = -1;
    private int _gridRow = -1;
    private int _groupColumn = -1;
    private int _groupWidthColumns = 4;
    private int _groupHeightRows = 11;
    private string _name = string.Empty;
    private string _accent = "#1674D1";
    private string _livePrimaryText = string.Empty;
    private string _liveSecondaryText = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }
    public string Target { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string Group
    {
        get => _group;
        set => SetField(ref _group, value);
    }

    public TileSize Size
    {
        get => _size;
        set
        {
            if (!SetField(ref _size, value) || value == TileSize.Custom)
                return;
            var (width, height) = PresetDimensions(value);
            SetField(ref _tileWidth, width, nameof(TileWidth));
            SetField(ref _tileHeight, height, nameof(TileHeight));
        }
    }

    public double TileWidth
    {
        get => _tileWidth;
        set => SetField(ref _tileWidth, Math.Clamp(value, MinimumTileExtent, MaximumTileWidth));
    }

    public double TileHeight
    {
        get => _tileHeight;
        set => SetField(ref _tileHeight, Math.Clamp(value, MinimumTileExtent, MaximumTileHeight));
    }

    public int GridColumn
    {
        get => _gridColumn;
        set => SetField(ref _gridColumn, value);
    }

    public int GridRow
    {
        get => _gridRow;
        set => SetField(ref _gridRow, value);
    }

    public int GroupColumn
    {
        get => _groupColumn;
        set => SetField(ref _groupColumn, value);
    }

    public int GroupWidthColumns
    {
        get => _groupWidthColumns;
        set => SetField(ref _groupWidthColumns, Math.Clamp(value, 2, 12));
    }

    public int GroupHeightRows
    {
        get => _groupHeightRows;
        set => SetField(ref _groupHeightRows, Math.Clamp(value, 4, 32));
    }

    public TileVisualStyle VisualStyle
    {
        get => _visualStyle;
        set => SetField(ref _visualStyle, value);
    }
    public string Glyph { get; set; } = "\uE8A5";
    public string Accent
    {
        get => _accent;
        set => SetField(ref _accent, value);
    }
    public bool HasCustomAppearance { get; set; }
    public int Order { get; set; }
    public TileKind Kind { get; set; }
    public bool IsLiveEnabled { get; set; } = true;
    public ObservableCollection<TileLayoutItem> Children { get; set; } = [];

    [JsonIgnore]
    public ImageSource? Icon { get; set; }

    [JsonIgnore]
    public string LivePrimaryText
    {
        get => _livePrimaryText;
        set => SetField(ref _livePrimaryText, value);
    }

    [JsonIgnore]
    public string LiveSecondaryText
    {
        get => _liveSecondaryText;
        set => SetField(ref _liveSecondaryText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetCustomSize(double width, double height)
    {
        SetField(ref _size, TileSize.Custom, nameof(Size));
        TileWidth = width;
        TileHeight = height;
    }

    public static (double Width, double Height) PresetDimensions(TileSize size) => size switch
    {
        TileSize.Small => (72, 72),
        TileSize.Medium => (152, 152),
        TileSize.Wide => (312, 152),
        TileSize.Large => (312, 312),
        _ => (152, 152)
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class LayoutData
{
    public int Version { get; set; } = WinTileLauncher.Services.LayoutRules.CurrentVersion;
    public double CanvasZoom { get; set; } = 1;
    public List<TileLayoutItem> Tiles { get; set; } = [];
}
