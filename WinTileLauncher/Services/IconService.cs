using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinTileLauncher.Services;

internal static class IconService
{
    private static readonly ConcurrentDictionary<string, Lazy<ImageSource?>> IconCache =
        new(StringComparer.OrdinalIgnoreCase);

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint ShgfiPidl = 0x000000008;
    private const uint FileAttributeNormal = 0x00000080;

    public static ImageSource? GetLauncherIcon(string target, string arguments = "")
    {
        if (arguments.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            return GetShellIcon(arguments);
        return GetIcon(target);
    }

    public static ImageSource? GetIcon(string path) => GetCached($"path\0{path}", () => LoadIcon(path));

    public static ImageSource? GetShellIcon(string parsingName) =>
        GetCached($"shell\0{parsingName}", () => LoadShellIcon(parsingName));

    private static ImageSource? GetCached(string key, Func<ImageSource?> factory)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        return IconCache.GetOrAdd(key, _ => new Lazy<ImageSource?>(factory,
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static ImageSource? LoadIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains(':') && !Path.IsPathRooted(path))
            return null;

        var resolvedPath = ResolveIconPath(path);
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return null;
        path = resolvedPath;

        var flags = ShgfiIcon | ShgfiLargeIcon;
        var attributes = 0u;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            flags |= ShgfiUseFileAttributes;
            attributes = FileAttributeNormal;
        }

        var result = SHGetFileInfo(path, attributes, out var info,
            (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(48, 48));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private static ImageSource? LoadShellIcon(string parsingName)
    {
        if (SHParseDisplayName(parsingName, IntPtr.Zero, out var itemIdList, 0, out _) != 0 ||
            itemIdList == IntPtr.Zero)
            return null;

        try
        {
            var result = SHGetFileInfoFromPidl(itemIdList, 0, out var info,
                (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon | ShgfiPidl);
            if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
                return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.IconHandle, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(48, 48));
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.IconHandle);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(itemIdList);
        }
    }

    private static string? ResolveIconPath(string path)
    {
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is not null && Activator.CreateInstance(shellType) is { } shell)
                {
                    try
                    {
                        dynamic shortcut = shell.GetType().InvokeMember("CreateShortcut",
                            System.Reflection.BindingFlags.InvokeMethod, null, shell, [path])!;
                        try
                        {
                            var iconLocation = (string?)shortcut.IconLocation;
                            if (!string.IsNullOrWhiteSpace(iconLocation))
                            {
                                var iconPath = iconLocation.Split(',')[0].Trim().Trim('"');
                                if (File.Exists(iconPath))
                                    return iconPath;
                            }

                            var targetPath = (string?)shortcut.TargetPath;
                            if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
                                return targetPath;
                        }
                        finally
                        {
                            if (Marshal.IsComObject(shortcut))
                                Marshal.FinalReleaseComObject(shortcut);
                        }
                    }
                    finally
                    {
                        if (Marshal.IsComObject(shell))
                            Marshal.FinalReleaseComObject(shell);
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
            {
                // Fall back to the shortcut's own shell icon below.
            }

            return path;
        }

        if (Path.IsPathRooted(path))
            return path;

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path),
            Path.Combine(Environment.SystemDirectory, path),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", path)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoFromPidl(
        IntPtr itemIdList,
        uint fileAttributes,
        out ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
