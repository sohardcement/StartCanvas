using System.Diagnostics;
using WinTileLauncher.Models;

namespace WinTileLauncher.Services;

internal static class LaunchService
{
    public static bool TryLaunch(LauncherItem item, out string? error) =>
        TryLaunch(item.Target, item.Arguments, out error);

    public static bool TryLaunch(TileLayoutItem item, out string? error) =>
        TryLaunch(item.Target, item.Arguments, out error);

    private static bool TryLaunch(string target, string arguments, out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                Arguments = arguments,
                UseShellExecute = true
            });
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            error = ex.Message;
            return false;
        }
    }
}
