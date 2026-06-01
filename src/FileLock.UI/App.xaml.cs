using System.Windows;
using System.Windows.Threading;

namespace FileLock.UI;

public partial class App : Application
{
    public App()
    {
        // Opt into WPF's modern Fluent theme (Windows 11 styling: rounded controls, updated
        // typography). Forced Light keeps the hand-tuned palette readable regardless of the
        // OS dark/light setting — switch to ThemeMode.System for OS-adaptive theming.
#pragma warning disable WPF0001 // ThemeMode is marked experimental in current WPF.
        ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001

        // Last line of defence: never let a raw stack trace reach the user.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Something went wrong: " + e.Exception.Message,
            "FileLock",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
