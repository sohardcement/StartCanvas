using System.Text.Json;
using System.Text.Json.Serialization;
using WinTileLauncher.Models;

namespace WinTileLauncher.Services;

internal sealed class LayoutService
{
    private readonly string _layoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tile10", "layout.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LayoutData? Load()
    {
        try
        {
            if (!File.Exists(_layoutPath))
                return null;
            return JsonSerializer.Deserialize<LayoutData>(File.ReadAllText(_layoutPath), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(IEnumerable<TileLayoutItem> tiles, double canvasZoom = 1)
    {
        var directory = Path.GetDirectoryName(_layoutPath)!;
        Directory.CreateDirectory(directory);
        var data = new LayoutData
        {
            Version = LayoutRules.CurrentVersion,
            CanvasZoom = LayoutRules.NormalizeCanvasZoom(canvasZoom),
            Tiles = tiles.ToList()
        };
        var temporaryPath = _layoutPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporaryPath, _layoutPath, true);
    }
}
