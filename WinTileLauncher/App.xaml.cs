using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace WinTileLauncher;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private MainWindow? _mainWindow;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "Tile10.FullScreenLauncher.SingleInstance", out var isFirstInstance);
        _ownsMutex = isFirstInstance;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Tile10.FullScreenLauncher.Show");
        if (!isFirstInstance)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
            WriteCrashLog("Dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain", args.ExceptionObject);

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        if (e.Args.Contains("--windowed", StringComparer.OrdinalIgnoreCase))
            _mainWindow.EnableWindowedTestMode();
        _mainWindow.InitializeLauncher(
            e.Args.Contains("--keyboard-hook-test", StringComparer.OrdinalIgnoreCase));
        _ = Task.Run(ShowEventLoop);
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            _mainWindow.ShowInitial();
    }

    private void ShowEventLoop()
    {
        while (true)
        {
            try
            {
                _showEvent?.WaitOne();
                if (_showEvent is null)
                    return;
                Dispatcher.BeginInvoke(() => _mainWindow?.ShowInitial());
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.Dispose();
        _showEvent?.Dispose();
        _showEvent = null;
        if (_ownsMutex)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void WriteCrashLog(string source, object exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tile10");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Unable to write Tile10 crash log: {logException.Message}");
        }
    }
}
