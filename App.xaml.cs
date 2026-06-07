using System.IO;
using System.Text;
using System.Windows;
using OwTranslateLite.Core;
using Application = System.Windows.Application;

namespace OwTranslateLite;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            LogException(e.Exception);
            System.Windows.MessageBox.Show(
                $"程序遇到错误，已写入日志：\n{ConfigStore.CrashLogPath}\n\n{e.Exception.Message}",
                "OW Translator Lite",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException(ex);
            }
            else
            {
                LogText(e.ExceptionObject?.ToString() ?? "Unknown fatal exception");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogException(e.Exception);
            e.SetObserved();
        };
    }

    private static void LogException(Exception exception)
    {
        LogText(exception.ToString());
    }

    private static void LogText(string text)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.AppDirectory);
            File.AppendAllText(
                ConfigStore.CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{text}\n\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // Last-resort crash logging must never trigger another crash.
        }
    }
}
