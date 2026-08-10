using System.Runtime.InteropServices;
using WinTileLauncher.Models;

namespace WinTileLauncher.Services;

internal sealed class AppDiscoveryService
{
    private static readonly string[] Palette =
    [
        "#1267A9", "#3B6F9B", "#0078D4", "#16806E", "#B94E3B",
        "#BD6B22", "#386D92", "#147D89", "#26785F", "#155F91"
    ];

    public Task<List<LauncherItem>> DiscoverAsync() => Task.Run(Discover);

    private static List<LauncherItem> Discover()
    {
        var items = new List<LauncherItem>
        {
            Create("文件资源管理器", "explorer.exe", "\uE8B7", "#D39C00"),
            Create("Windows 设置", "ms-settings:", "\uE713", "#0078D4"),
            Create("终端", "wt.exe", "\uE756", "#323232"),
            Create("计算器", "calc.exe", "\uE8EF", "#0078D4"),
            Create("记事本", "notepad.exe", "\uE70F", "#1468A0"),
            Create("Microsoft Store", "ms-windows-store:", "\uE719", "#1473E6")
        };

        var startMenuFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        foreach (var folder in startMenuFolders.Where(Directory.Exists))
        {
            foreach (var file in SafeEnumerateFiles(folder))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".url", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Path.GetFileNameWithoutExtension(file).Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Contains("卸载", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
                    continue;

                items.Add(Create(name, file, "\uE8A5", ColorFor(file)));
            }
        }

        DiscoverPackagedApps(items);

        return items
            .GroupBy(item => item.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
            foreach (var directory in directories)
                pending.Push(directory);
        }
    }

    private static void DiscoverPackagedApps(List<LauncherItem> items)
    {
        object? shell = null;
        object? folder = null;
        object? folderItems = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null || (shell = Activator.CreateInstance(shellType)) is null)
                return;

            folder = shell.GetType().InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, ["shell:AppsFolder"]);
            if (folder is null)
                return;
            folderItems = folder.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod,
                null, folder, null);
            if (folderItems is not System.Collections.IEnumerable enumerable)
                return;

            foreach (var rawItem in enumerable)
            {
                if (rawItem is null)
                    continue;
                try
                {
                    var type = rawItem.GetType();
                    var name = type.InvokeMember("Name", System.Reflection.BindingFlags.GetProperty,
                        null, rawItem, null) as string;
                    var appId = type.InvokeMember("ExtendedProperty", System.Reflection.BindingFlags.InvokeMethod,
                        null, rawItem, ["System.AppUserModel.ID"]) as string;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId))
                        continue;

                    var parsingName = $"shell:AppsFolder\\{appId}";
                    items.Add(Create(name.Trim(), "explorer.exe", "\uE8A5", ColorFor(appId), parsingName));
                }
                finally
                {
                    if (Marshal.IsComObject(rawItem))
                        Marshal.FinalReleaseComObject(rawItem);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException or InvalidCastException)
        {
            // The Start Menu shortcut scan still provides a useful fallback.
        }
        finally
        {
            foreach (var value in new[] { folderItems, folder, shell })
            {
                if (value is not null && Marshal.IsComObject(value))
                    Marshal.FinalReleaseComObject(value);
            }
        }
    }

    private static LauncherItem Create(string name, string target, string glyph, string accent,
        string arguments = "") => new()
    {
        Id = StableId($"{target}\0{arguments}"),
        Name = name,
        Target = target,
        Arguments = arguments,
        Glyph = glyph,
        Accent = accent
    };

    private static string ColorFor(string value)
    {
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(value) & int.MaxValue;
        return Palette[hash % Palette.Length];
    }

    private static string StableId(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
