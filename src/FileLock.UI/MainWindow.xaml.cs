using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FileLock.Core;

namespace FileLock.UI;

public partial class MainWindow : Window
{
    private static readonly Brush IdleBorder = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush ActiveFill = new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF));
    private static readonly Brush IdleFill = new SolidColorBrush(Colors.White);
    private static readonly Brush ErrorText = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    private static readonly Brush OkText = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
    private static readonly Brush NormalText = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));

    private readonly SettingsStore _settings = new(App.BaseDir);
    private readonly LockToggleService _service = new(App.BaseDir);
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        StorageHint.Text = _service.Backups.BackupDirectory;
        StorageHint.ToolTip = _service.Backups.BackupDirectory;

        if (_settings.HasPassword)
            SetStatus("A password is set. Drop a file to lock or unlock it.", NormalText);
        else
            SetStatus("Set a password to get started.", NormalText);
    }

    // ── Password setup ───────────────────────────────────────────────────────────

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string pw = PasswordInput.Password;
        string confirm = ConfirmInput.Password;

        if (string.IsNullOrEmpty(pw))
        {
            SetStatus("Enter a password.", ErrorText);
            PasswordInput.Focus();
            return;
        }
        if (pw != confirm)
        {
            SetStatus("The two passwords don't match.", ErrorText);
            ConfirmInput.Focus();
            return;
        }

        try
        {
            _settings.SetPassword(pw);
            PasswordInput.Clear();
            ConfirmInput.Clear();
            SetStatus("✅ Password saved. Drop a file to lock or unlock it.", OkText);
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't save the password: " + ex.Message, ErrorText);
        }
    }

    // ── Drag visuals ───────────────────────────────────────────────────────────

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = (!_busy && e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None;
        if (e.Effects == DragDropEffects.Copy)
        {
            DropZone.BorderBrush = ActiveBorder;
            DropZone.Background = ActiveFill;
        }
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropZoneVisual();
        e.Handled = true;
    }

    private void ResetDropZoneVisual()
    {
        DropZone.BorderBrush = IdleBorder;
        DropZone.Background = IdleFill;
    }

    // ── Drop ────────────────────────────────────────────────────────────────────

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        ResetDropZoneVisual();

        if (_busy || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (paths.Length == 0)
            return;

        if (!_settings.HasPassword)
        {
            SetStatus("Set a password first, then drop the file again.", ErrorText);
            PasswordInput.Focus();
            return;
        }

        string password = _settings.GetPassword();
        await RunToggleAsync(paths, password);
    }

    private async Task RunToggleAsync(string[] paths, string password)
    {
        SetBusy(true);
        SetStatus(paths.Length == 1 ? "Working…" : $"Working on {paths.Length} files…", NormalText);

        try
        {
            (int locked, int unlocked, List<string> failures, ToggleResult? last) = await Task.Run(() =>
            {
                int l = 0, u = 0;
                var fails = new List<string>();
                ToggleResult? lastResult = null;
                foreach (string path in paths)
                {
                    try
                    {
                        ToggleResult r = _service.Process(path, password);
                        if (r.Operation == LockOperation.Locked) l++; else u++;
                        lastResult = r;
                    }
                    catch (Exception ex)
                    {
                        fails.Add($"{Path.GetFileName(path)}: {Friendly(ex)}");
                    }
                }
                return (l, u, fails, lastResult);
            });

            if (paths.Length == 1 && failures.Count == 0 && last is not null)
            {
                string verb = last.Operation == LockOperation.Locked ? "Locked" : "Unlocked";
                SetStatus($"✅ {verb}: {last.OriginalName}", OkText);
                RevealInExplorer(last.Path);
            }
            else if (failures.Count == 0)
            {
                SetStatus($"✅ Done — locked {locked}, unlocked {unlocked}.", OkText);
            }
            else
            {
                string head = $"Locked {locked}, unlocked {unlocked}, {failures.Count} failed:";
                SetStatus(head + "\n" + string.Join("\n", failures), ErrorText);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── Storage ──────────────────────────────────────────────────────────────────

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = _service.Backups.BackupDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't open the folder: " + ex.Message, ErrorText);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string Friendly(Exception ex) => ex switch
    {
        WrongPasswordException => "wrong password, or the file was changed",
        BadFormatException => "not a FileLock file",
        FileTooLargeException => ex.Message,
        FileNotFoundException => "file not found",
        UnauthorizedAccessException => "permission denied (is the app folder writable?)",
        _ => ex.Message,
    };

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PasswordInput.IsEnabled = !busy;
        ConfirmInput.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private void SetStatus(string message, Brush color)
    {
        StatusText.Text = message;
        StatusText.Foreground = color;
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Revealing the file is a nicety; never fail the operation over it.
        }
    }
}
