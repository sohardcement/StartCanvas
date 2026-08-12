using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinTileLauncher.Models;

namespace WinTileLauncher.Services;

internal sealed record LauncherTheme(
    string Id,
    string DisplayName,
    string Description,
    string BackgroundStart,
    string BackgroundMiddle,
    string BackgroundEnd,
    string Accent,
    string AccentHover,
    string AccentPressed,
    string Glass,
    string GlassStrong,
    string GlassHover,
    string Border,
    string BorderStrong,
    string Focus,
    string Decoration,
    string Glow);

internal readonly record struct BackgroundResolution(string Mode, string? Value, bool IsFallback);

internal sealed class AppearanceService
{
    public const string ThemeBackgroundMode = "theme";
    public const string ImageBackgroundMode = "image";
    public const string SolidBackgroundMode = "solid";

    private static readonly string[] SupportedImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];
    private const int MaximumDecodedImageDimension = 3840;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _appearancePath;
    private readonly string _backgroundDirectory;
    private readonly object _cacheGate = new();
    private string? _cachedImagePath;
    private DateTime _cachedImageWriteTimeUtc;
    private ImageSource? _cachedImage;

    internal static IReadOnlyList<LauncherTheme> Themes { get; } =
    [
        new("ocean", "深海", "清澈、沉静", "#0A1A2E", "#0D2A46", "#123D5B",
            "#2778B8", "#2A79B4", "#1C659F", "#8C10263D", "#E90A1C2E", "#B42A4964",
            "#58DDF3FF", "#8FE9F8FF", "#EAFBFF", "#22C4EEFF", "#0F8FD8FF"),
        new("cyan", "青岚", "轻盈、通透", "#071A20", "#0B3138", "#104C55",
            "#0E7894", "#147D96", "#09647C", "#8C0A2B31", "#E9072026", "#B4255058",
            "#58C6F2F4", "#8FE0FAFC", "#DDFBFF", "#22A8EEF2", "#0F5FE4E9"),
        new("forest", "松林", "自然、专注", "#0B1915", "#133329", "#1D4B3A",
            "#21806D", "#26816F", "#176A5A", "#8C102A23", "#E90A211B", "#B42C5145",
            "#58C7ECDD", "#8FE0F8EE", "#E8FFF6", "#229BE4C7", "#0F55D2A9"),
        new("violet", "暮紫", "柔和、灵感", "#151126", "#2A2148", "#46356A",
            "#7651A8", "#8A62BE", "#60418D", "#8C241B3A", "#E916102B", "#B44B3A68",
            "#58E0CBFF", "#8FF0E7FF", "#F8F0FF", "#22C7A8F4", "#0F9D67E4"),
        new("sunset", "暖暮", "温暖、舒展", "#21160D", "#3B2615", "#55351C",
            "#93620F", "#98691B", "#79500B", "#8C302114", "#E923170D", "#B45C4128",
            "#58FFE1B4", "#8FFFF0D2", "#FFF6E8", "#22F0B96D", "#0FDF7F2A"),
        new("graphite", "石墨", "克制、沉稳", "#101419", "#1A232B", "#2A3843",
            "#496A82", "#52748A", "#39566B", "#8C1A2229", "#E912181E", "#B43B4B56",
            "#58D3E3EE", "#8FEAF3F8", "#F5FAFD", "#2299BBD2", "#0F78A8C7")
    ];

    public AppearanceService(string? appearancePath = null, string? backgroundDirectory = null)
    {
        _appearancePath = appearancePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tile10", "appearance.json");
        _backgroundDirectory = backgroundDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tile10", "Backgrounds");
    }

    public AppearanceSettings Load()
    {
        try
        {
            if (!File.Exists(_appearancePath))
                return Normalize(null);

            return Normalize(JsonSerializer.Deserialize<AppearanceSettings>(
                File.ReadAllText(_appearancePath), JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Normalize(null);
        }
    }

    public void Save(AppearanceSettings settings)
    {
        var directory = Path.GetDirectoryName(_appearancePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _appearancePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Normalize(settings), JsonOptions));
        File.Move(temporaryPath, _appearancePath, true);
    }

    public string ImportBackgroundImage(string sourcePath)
    {
        if (!IsSupportedImagePath(sourcePath))
            throw new NotSupportedException("请选择 PNG、JPG 或 BMP 图片。");

        var sourceFullPath = Path.GetFullPath(sourcePath);
        var directory = _backgroundDirectory;
        Directory.CreateDirectory(directory);

        var extension = Path.GetExtension(sourceFullPath).ToLowerInvariant();
        var destinationPath = Path.Combine(directory, $"background-{Guid.NewGuid():N}{extension}");
        if (string.Equals(sourceFullPath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryLoadImage(destinationPath, out _))
                throw new InvalidDataException("无法读取所选图片。");
            return destinationPath;
        }

        var temporaryPath = Path.Combine(directory, $"background-importing-{Guid.NewGuid():N}{extension}");
        try
        {
            File.Copy(sourceFullPath, temporaryPath, true);
            if (!TryLoadImage(temporaryPath, out var importedImage) || importedImage is null)
                throw new InvalidDataException("无法读取所选图片，文件可能已损坏。");
            File.Move(temporaryPath, destinationPath, true);
            lock (_cacheGate)
            {
                _cachedImagePath = destinationPath;
                _cachedImageWriteTimeUtc = File.GetLastWriteTimeUtc(destinationPath);
                _cachedImage = importedImage;
            }
            return destinationPath;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed cleanup must not hide the more useful import error.
            }
        }
    }

    public void DeleteManagedBackground(string? path)
    {
        if (!IsManagedBackgroundPath(path))
            return;
        try
        {
            File.Delete(path!);
            lock (_cacheGate)
            {
                if (string.Equals(_cachedImagePath, Path.GetFullPath(path!), StringComparison.OrdinalIgnoreCase))
                {
                    _cachedImagePath = null;
                    _cachedImage = null;
                    _cachedImageWriteTimeUtc = default;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale cached background should never break personalization.
        }
    }

    internal static LauncherTheme ResolveTheme(string? themeId) =>
        Themes.FirstOrDefault(theme => theme.Id.Equals(themeId?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    internal static string ResolveThemeId(string? themeId) => ResolveTheme(themeId).Id;

    internal static AppearanceSettings Normalize(AppearanceSettings? settings)
    {
        settings ??= new AppearanceSettings();
        var mode = settings.BackgroundMode?.Trim().ToLowerInvariant() switch
        {
            ImageBackgroundMode => ImageBackgroundMode,
            SolidBackgroundMode => SolidBackgroundMode,
            _ => ThemeBackgroundMode
        };

        return new AppearanceSettings
        {
            Version = 1,
            ThemeId = ResolveThemeId(settings.ThemeId),
            BackgroundMode = mode,
            BackgroundColor = TryNormalizeHexColor(settings.BackgroundColor, out var normalizedColor)
                ? normalizedColor
                : AppearanceDefaults.BackgroundColor,
            BackgroundImagePath = settings.BackgroundImagePath?.Trim() ?? string.Empty,
            BackgroundOverlayOpacity = double.IsFinite(settings.BackgroundOverlayOpacity)
                ? Math.Clamp(settings.BackgroundOverlayOpacity, 0.55, 0.8)
                : AppearanceDefaults.BackgroundOverlayOpacity
        };
    }

    internal static bool TryNormalizeHexColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        var candidate = value?.Trim();
        if (candidate is null || candidate.Length != 7 || candidate[0] != '#')
            return false;
        if (!candidate.AsSpan(1).ToString().All(Uri.IsHexDigit))
            return false;
        normalized = candidate.ToUpperInvariant();
        return true;
    }

    internal static BackgroundResolution ResolveBackground(AppearanceSettings settings,
        Func<string, bool>? imageExists = null)
    {
        var normalized = Normalize(settings);
        imageExists ??= File.Exists;
        if (normalized.BackgroundMode == ImageBackgroundMode &&
            IsSupportedImagePath(normalized.BackgroundImagePath) &&
            SafeFileExists(normalized.BackgroundImagePath, imageExists))
        {
            return new BackgroundResolution(ImageBackgroundMode, normalized.BackgroundImagePath, false);
        }

        if (normalized.BackgroundMode == SolidBackgroundMode)
            return new BackgroundResolution(SolidBackgroundMode, normalized.BackgroundColor, false);

        return new BackgroundResolution(ThemeBackgroundMode, normalized.ThemeId,
            normalized.BackgroundMode == ImageBackgroundMode);
    }

    internal static bool IsSupportedImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            if (!Path.IsPathFullyQualified(path))
                return false;
            var uri = new Uri(path, UriKind.Absolute);
            return uri.IsFile && !uri.IsUnc &&
                   SupportedImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or UriFormatException)
        {
            return false;
        }
    }

    internal bool IsManagedBackgroundPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var managedDirectory = Path.GetFullPath(_backgroundDirectory);
            var fullPath = Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(fullPath), managedDirectory,
                       StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileName(fullPath).StartsWith("background-", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public bool TryLoadBackgroundImage(string? path, out ImageSource? image)
    {
        image = null;
        if (!IsSupportedImagePath(path))
            return false;
        try
        {
            var fullPath = Path.GetFullPath(path!);
            var writeTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            lock (_cacheGate)
            {
                if (_cachedImage is not null &&
                    string.Equals(_cachedImagePath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                    _cachedImageWriteTimeUtc == writeTimeUtc)
                {
                    image = _cachedImage;
                    return true;
                }
            }

            if (!TryLoadImage(fullPath, out image) || image is null)
                return false;
            lock (_cacheGate)
            {
                _cachedImagePath = fullPath;
                _cachedImageWriteTimeUtc = writeTimeUtc;
                _cachedImage = image;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool TryLoadImage(string? path, out ImageSource? image)
    {
        image = null;
        if (!IsSupportedImagePath(path))
            return false;
        try
        {
            using var stream = File.Open(path!, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (frame.PixelWidth > MaximumDecodedImageDimension ||
                frame.PixelHeight > MaximumDecodedImageDimension)
            {
                if (frame.PixelWidth >= frame.PixelHeight)
                    bitmap.DecodePixelWidth = MaximumDecodedImageDimension;
                else
                    bitmap.DecodePixelHeight = MaximumDecodedImageDimension;
            }
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or
                                   ArgumentException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    internal static LinearGradientBrush CreateThemeBackground(LauncherTheme theme)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 1),
            EndPoint = new System.Windows.Point(1, 0)
        };
        brush.GradientStops.Add(new GradientStop(ParseColor(theme.BackgroundStart), 0));
        brush.GradientStops.Add(new GradientStop(ParseColor(theme.BackgroundMiddle), 0.52));
        brush.GradientStops.Add(new GradientStop(ParseColor(theme.BackgroundEnd), 1));
        brush.Freeze();
        return brush;
    }

    internal static Color ParseColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value)!;

    private static bool SafeFileExists(string path, Func<string, bool> imageExists)
    {
        try
        {
            return imageExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
