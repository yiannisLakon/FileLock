using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FileLock.Core;
using WinForms = System.Windows.Forms;

namespace FileLock.UI;

public partial class App : Application
{
    /// <summary>The install folder — where settings, <c>.backup</c>, and the ledger live.</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    public App()
    {
        // Opt into WPF's modern Fluent theme. Forced Light keeps the hand-tuned palette
        // readable regardless of the OS setting — switch to ThemeMode.System for OS-adaptive.
#pragma warning disable WPF0001 // ThemeMode is marked experimental in current WPF.
        ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001

        // Last line of defence: never let a raw stack trace reach the user.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        (string[] files, string? cliPassword) = ParseArgs(e.Args);

        if (files.Length == 0)
        {
            // Interactive launch → open the settings / control-panel window.
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            return;
        }

        // Shortcut drop → process headlessly, report via a tray balloon, then exit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            RunHeadless(files, cliPassword);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Something went wrong: " + ex.Message, "FileLock",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Splits the command line into file paths and an optional password. Recognises
    /// <c>--password &lt;pw&gt;</c> / <c>-p &lt;pw&gt;</c> and the <c>--password=&lt;pw&gt;</c> form.
    /// Other switches (anything starting with <c>-</c> or <c>/</c>) are ignored; everything else
    /// is treated as a file path. (Note: a password on the command line is visible to other
    /// processes — prefer the stored default for interactive use.)
    /// </summary>
    private static (string[] files, string? password) ParseArgs(string[] args)
    {
        var files = new List<string>();
        string? password = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--password", StringComparison.OrdinalIgnoreCase) || a.Equals("-p", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                    password = args[++i];
            }
            else if (a.StartsWith("--password=", StringComparison.OrdinalIgnoreCase))
            {
                password = a["--password=".Length..];
            }
            else if (a.StartsWith("-p=", StringComparison.OrdinalIgnoreCase))
            {
                password = a["-p=".Length..];
            }
            else if (a.StartsWith('-') || a.StartsWith('/'))
            {
                // Unknown switch — ignore. (Windows file paths never start with '-' or '/'.)
            }
            else
            {
                files.Add(a);
            }
        }

        return (files.ToArray(), string.IsNullOrEmpty(password) ? null : password);
    }

    private void RunHeadless(string[] files, string? cliPassword)
    {
        var settings = new SettingsStore(BaseDir);
        string? password = ResolvePassword(settings, cliPassword);
        if (password is null)
        {
            // No password available (e.g. the first-run prompt was cancelled) — do nothing.
            Shutdown();
            return;
        }

        var service = new LockToggleService(BaseDir);

        int locked = 0, unlocked = 0;
        var failures = new List<string>();
        ToggleResult? last = null;

        foreach (string file in files)
        {
            try
            {
                ToggleResult result = service.Process(file, password);
                if (result.Operation == LockOperation.Locked) locked++;
                else unlocked++;
                last = result;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {Friendly(ex)}");
            }
        }

        string message = Summarize(files.Length, locked, unlocked, failures, last);
        ShowBalloonAndExit(message, isError: failures.Count > 0);
    }

    /// <summary>
    /// Decides which password to use for a headless run:
    /// <list type="bullet">
    /// <item>A <c>--password</c> value is used for this run; if no default is stored yet, it is
    /// also saved as the default ("save if not set yet").</item>
    /// <item>Otherwise the stored default is used.</item>
    /// <item>On first run (no default, no <c>--password</c>) the user is prompted; the entered
    /// value is saved as the default and used. Returns <c>null</c> if the prompt is cancelled.</item>
    /// </list>
    /// </summary>
    private static string? ResolvePassword(SettingsStore settings, string? cliPassword)
    {
        if (!string.IsNullOrEmpty(cliPassword))
        {
            if (!settings.HasPassword)
                TrySave(settings, cliPassword);
            return cliPassword;
        }

        if (settings.HasPassword)
            return settings.GetPassword();

        // First run: no default and nothing passed → ask, save, then proceed.
        var dialog = new PasswordPromptWindow();
        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Password))
        {
            TrySave(settings, dialog.Password);
            return dialog.Password;
        }
        return null;
    }

    /// <summary>Persists the default password, ignoring write failures — the value still works
    /// for this run, and any unwritable-folder problem surfaces per-file when locking.</summary>
    private static void TrySave(SettingsStore settings, string password)
    {
        try { settings.SetPassword(password); }
        catch { /* best effort; locking will report a writability problem if there is one */ }
    }

    // ── Result reporting ───────────────────────────────────────────────────────────

    private static string Summarize(int total, int locked, int unlocked, List<string> failures, ToggleResult? last)
    {
        if (total == 1 && failures.Count == 0 && last is not null)
            return (last.Operation == LockOperation.Locked ? "🔒 Locked " : "🔓 Unlocked ") + last.OriginalName;

        var parts = new List<string>();
        if (locked > 0) parts.Add($"Locked {locked}");
        if (unlocked > 0) parts.Add($"Unlocked {unlocked}");
        if (failures.Count > 0) parts.Add($"{failures.Count} failed");

        string head = parts.Count > 0 ? string.Join(", ", parts) : "Nothing to do";
        if (failures.Count > 0)
            head += "\n" + string.Join("\n", failures.Take(5));
        return head;
    }

    private static string Friendly(Exception ex) => ex switch
    {
        WrongPasswordException => "wrong password, or the file was changed",
        BadFormatException => "not a FileLock file",
        FileTooLargeException => ex.Message,
        FileNotFoundException => "file not found",
        UnauthorizedAccessException => "permission denied (is the app folder writable?)",
        _ => ex.Message,
    };

    private void ShowBalloonAndExit(string message, bool isError)
    {
        WinForms.NotifyIcon icon;
        try
        {
            icon = new WinForms.NotifyIcon
            {
                Icon = TryLoadAppIcon() ?? SystemIcons.Application,
                Visible = true,
                BalloonTipTitle = "FileLock",
                BalloonTipText = message,
                BalloonTipIcon = isError ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info,
            };
            icon.ShowBalloonTip(5000);
        }
        catch
        {
            // If the tray balloon can't be shown, surface errors via a dialog instead.
            if (isError)
                MessageBox.Show(message, "FileLock", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        // Keep the message loop alive briefly so the balloon is visible, then clean up.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            icon.Visible = false;
            icon.Dispose();
            Shutdown();
        };
        timer.Start();
    }

    private static Icon? TryLoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            return exe is null ? null : Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
            return null;
        }
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
