using System.IO;
using System.Windows;
using System.Windows.Threading;
using PatchIt.Client.Services;

namespace PatchIt.Client;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private static void OnDispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"Si è verificato un errore imprevisto:\n\n{e.Exception.Message}",
            "PatchIT",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Log(ex);
    }

    private static void Log(Exception ex)
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, "crash.log");
            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch
        {
            // impossibile scrivere il log: si ignora
        }
    }
}
