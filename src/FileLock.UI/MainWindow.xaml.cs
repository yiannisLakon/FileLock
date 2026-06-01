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

    private readonly FileCryptor _cryptor = new();
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
    }

    // ── Drag visuals ───────────────────────────────────────────────────────────

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = (!_busy && TryGetSingleFile(e, out _)) ? DragDropEffects.Copy : DragDropEffects.None;
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

        if (_busy)
            return;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (paths.Length != 1)
        {
            SetStatus("Please drop one file at a time.", ErrorText);
            return;
        }

        string path = paths[0];
        if (Directory.Exists(path))
        {
            SetStatus("Folders aren't supported — drop a single file.", ErrorText);
            return;
        }
        if (!File.Exists(path))
        {
            SetStatus("That file could not be found.", ErrorText);
            return;
        }

        string password = PasswordInput.Password;
        if (string.IsNullOrEmpty(password))
        {
            SetStatus("Enter a password first.", ErrorText);
            PasswordInput.Focus();
            return;
        }

        bool unlocking = path.EndsWith(FileFormat.LockedExtension, StringComparison.OrdinalIgnoreCase);
        await RunOperationAsync(path, password, unlocking);
    }

    private async Task RunOperationAsync(string path, string password, bool unlocking)
    {
        SetBusy(true);
        SetStatus(unlocking ? "Unlocking…" : "Locking…", NormalText);

        try
        {
            string resultPath = await Task.Run(() =>
            {
                if (unlocking)
                {
                    string outDir = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
                    return _cryptor.Unlock(path, outDir, password);
                }
                else
                {
                    string outPath = FileCryptor.GetAvailablePath(path + FileFormat.LockedExtension);
                    _cryptor.Lock(path, outPath, password);
                    return outPath;
                }
            });

            string verb = unlocking ? "Unlocked" : "Locked";
            SetStatus($"✅ {verb}: {Path.GetFileName(resultPath)}", OkText);
            RevealInExplorer(resultPath);
        }
        catch (WrongPasswordException)
        {
            SetStatus("❌ Wrong password, or the file was changed.", ErrorText);
        }
        catch (BadFormatException)
        {
            SetStatus("This file isn't a FileLock file.", ErrorText);
        }
        catch (FileTooLargeException ex)
        {
            SetStatus(ex.Message, ErrorText);
        }
        catch (Exception ex)
        {
            SetStatus("Something went wrong: " + ex.Message, ErrorText);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PasswordInput.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private void SetStatus(string message, Brush color)
    {
        StatusText.Text = message;
        StatusText.Foreground = color;
    }

    private static bool TryGetSingleFile(DragEventArgs e, out string path)
    {
        path = string.Empty;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1)
            return false;
        path = paths[0];
        return true;
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
