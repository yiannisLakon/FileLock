namespace FileLock.Core;

/// <summary>Which way a <see cref="LockToggleService.Process"/> call went.</summary>
public enum LockOperation
{
    Locked,
    Unlocked,
}

/// <summary>The outcome of processing one file.</summary>
public sealed record ToggleResult(LockOperation Operation, string Path, string OriginalName);

/// <summary>
/// The toggle workflow: given a file, lock it or unlock it <em>in place</em> depending on
/// whether it already carries the FileLock header. Locking first copies the original into the
/// <see cref="BackupStore"/> and records it in the ledger; unlocking just replaces in place
/// (it is reversible and fail-closed, so no backup is taken).
/// </summary>
public sealed class LockToggleService
{
    private readonly FileCryptor _cryptor = new();
    private readonly BackupStore _backups;

    public LockToggleService(string baseDirectory)
    {
        _backups = new BackupStore(baseDirectory);
    }

    public BackupStore Backups => _backups;

    /// <summary>
    /// Locks <paramref name="path"/> if it is a plain file, or unlocks it if it is already a
    /// FileLock file. Replaces the file in place either way.
    /// </summary>
    /// <exception cref="FileNotFoundException">The path does not exist.</exception>
    /// <exception cref="FileLockException">A folder was passed, or a crypto/format error occurred.</exception>
    public ToggleResult Process(string path, string password, string? lockedBy = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(password);

        if (Directory.Exists(path))
            throw new FileLockException("Folders aren't supported — drop a single file.");
        if (!File.Exists(path))
            throw new FileNotFoundException("The file could not be found.", path);

        string name = Path.GetFileName(path);

        if (FileCryptor.IsLocked(path))
        {
            _cryptor.UnlockInPlace(path, password);
            return new ToggleResult(LockOperation.Unlocked, path, name);
        }

        // Lock: back up the original first, then replace in place, then record it.
        string who = string.IsNullOrEmpty(lockedBy) ? Environment.UserName : lockedBy;
        long size = new FileInfo(path).Length;
        string backupName = _backups.BackUp(path);
        try
        {
            _cryptor.LockInPlace(path, password, who);
        }
        catch
        {
            // Locking failed and left the source untouched — drop the orphan backup.
            _backups.TryDeleteBackup(backupName);
            throw;
        }

        _backups.Append(new LedgerEntry(
            Time: DateTimeOffset.UtcNow,
            Op: "lock",
            SourcePath: Path.GetFullPath(path),
            OriginalName: name,
            BackupName: backupName,
            LockedBy: who,
            Size: size));

        return new ToggleResult(LockOperation.Locked, path, name);
    }
}
