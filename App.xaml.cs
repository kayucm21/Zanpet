using System.IO;
using System.Windows;
using System.Windows.Threading;
using ZapretUI.Services;

namespace ZapretUI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!AdminElevation.IsRunningAsAdmin())
        {
            if (AdminElevation.TryRelaunchElevated())
            {
                Shutdown(0);
                return;
            }

            MessageBox.Show(
                "Zapret UI нужно запускать от имени администратора (WinDivert).\n\n" +
                "Подтвердите запрос UAC или запустите exe правой кнопкой → «Запуск от имени администратора».",
                "Требуются права администратора",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteFatal(args.ExceptionObject as Exception);

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteFatal(ex);
            MessageBox.Show(ex.ToString(), "Zapret UI — критическая ошибка запуска",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteFatal(e.Exception);
        MessageBox.Show(e.Exception.Message, "Zapret UI — ошибка",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteFatal(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "fatal.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
