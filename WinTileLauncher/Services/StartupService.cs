using Microsoft.Win32;

namespace WinTileLauncher.Services;

internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tile10";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return IsCommandForExecutable(
                key?.GetValue(ValueName) as string,
                Environment.ProcessPath);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsCommandForExecutable(string? command, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(executablePath))
            return false;

        var trimmed = command.Trim();
        string registeredPath;
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1)
                return false;
            if (closingQuote + 1 < trimmed.Length &&
                !char.IsWhiteSpace(trimmed[closingQuote + 1]))
                return false;
            registeredPath = trimmed[1..closingQuote];
        }
        else
        {
            var separator = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
            registeredPath = separator < 0 ? trimmed : trimmed[..separator];
        }

        try
        {
            return Path.GetFullPath(registeredPath)
                .Equals(Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定 Tile10 程序路径。");
            key.SetValue(ValueName, $"\"{executablePath}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
