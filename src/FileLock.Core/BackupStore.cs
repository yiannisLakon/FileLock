using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileLock.Core;

/// <summary>
/// Owns the <c>.backup</c> folder and its <c>ledger.jsonl</c> manifest under a base directory
/// (the app's install folder). Before a file is locked, the original is copied here under a
/// collision-safe name and a <see cref="LedgerEntry"/> records where it came from.
/// </summary>
public sealed class BackupStore
{
    private readonly string _backupDir;
    private readonly string _ledgerPath;

    public BackupStore(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        _backupDir = Path.Combine(baseDirectory, ".backup");
        _ledgerPath = Path.Combine(_backupDir, "ledger.jsonl");
    }

    /// <summary>The <c>.backup</c> directory (created lazily on first write).</summary>
    public string BackupDirectory => _backupDir;

    /// <summary>The ledger file path (<c>.backup/ledger.jsonl</c>).</summary>
    public string LedgerPath => _ledgerPath;

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into the backup folder under a unique name formed
    /// by appending a random token to the original stem (e.g. <c>report.3f9a…b2.pdf</c>) and
    /// returns that backup file name. The true original name belongs in the ledger, not here.
    /// </summary>
    public string BackUp(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        Directory.CreateDirectory(_backupDir);

        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        string ext = Path.GetExtension(sourcePath);

        for (int attempt = 0; attempt < 1000; attempt++)
        {
            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)); // 64-bit, 16 hex chars
            string backupName = $"{stem}.{token}{ext}";
            string dest = Path.Combine(_backupDir, backupName);
            try
            {
                // overwrite: false → throws if the (random) name is somehow taken, so we retry.
                File.Copy(sourcePath, dest, overwrite: false);
                return backupName;
            }
            catch (IOException) when (File.Exists(dest))
            {
                // Name clash (astronomically unlikely) — pick a new token and try again.
            }
        }
        throw new IOException("Could not create a unique backup file name.");
    }

    /// <summary>Appends one compact JSON line to the ledger, retrying briefly if another
    /// process is mid-append (concurrent shortcut drops).</summary>
    public void Append(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(_backupDir);

        string line = JsonSerializer.Serialize(entry, LedgerJsonContext.Default.LedgerEntry);
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(_ledgerPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25); // brief back-off for a concurrent writer
            }
        }
    }

    /// <summary>Best-effort removal of a backup file (used to clean up if a lock fails).</summary>
    public void TryDeleteBackup(string backupName)
    {
        try
        {
            string p = Path.Combine(_backupDir, backupName);
            if (File.Exists(p))
                File.Delete(p);
        }
        catch
        {
            // Best effort; never mask the original error.
        }
    }
}
