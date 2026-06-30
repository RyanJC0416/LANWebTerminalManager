using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LANWebTerminalManager;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog(args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            MessageBox.Show(
                $"应用发生未处理错误：\n\n{args.Exception.Message}",
                "局域网网页终端管理器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }

    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null) return;
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LANWebTerminalManager");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{exception}\n\n");
        }
        catch
        {
            // ignore crash log failures
        }
    }
}
