using System.Security.Cryptography;
using System.Text.Json;
using FileLock.Core;
using Xunit;

namespace FileLock.Core.Tests;

/// <summary>
/// Exercises the toggle workflow (detect → lock/unlock in place) together with the backup
/// folder and ledger. Each test gets its own throwaway base dir + work dir.
/// </summary>
public sealed class LockToggleServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _baseDir;  // app install folder (.backup + ledger live here)
    private readonly string _workDir;  // where the files being toggled live
    private readonly LockToggleService _service;

    public LockToggleServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "filelock-toggle-tests", Guid.NewGuid().ToString("N"));
        _baseDir = Path.Combine(_root, "app");
        _workDir = Path.Combine(_root, "work");
        Directory.CreateDirectory(_baseDir);
        Directory.CreateDirectory(_workDir);
        _service = new LockToggleService(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string name, byte[] contents, string? dir = null)
    {
        string path = Path.Combine(dir ?? _workDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static byte[] RandomBytes(int n)
    {
        byte[] b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private string[] BackupFiles() =>
        Directory.Exists(BackupDir)
            ? Directory.GetFiles(BackupDir).Where(f => !f.EndsWith("ledger.jsonl", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];

    private string[] LedgerLines() =>
        File.Exists(LedgerPath) ? File.ReadAllLines(LedgerPath).Where(l => l.Length > 0).ToArray() : [];

    private string BackupDir => Path.Combine(_baseDir, ".backup");
    private string LedgerPath => Path.Combine(BackupDir, "ledger.jsonl");

    [Fact]
    public void Process_PlainFile_LocksInPlace_BacksUp_AndRecordsLedger()
    {
        byte[] original = RandomBytes(1234);
        string file = Write("report.pdf", original);

        ToggleResult result = _service.Process(file, "pw");

        Assert.Equal(LockOperation.Locked, result.Operation);
        Assert.Equal("report.pdf", result.OriginalName);
        Assert.True(FileCryptor.IsLocked(file)); // replaced in place

        // Exactly one backup, holding the original plaintext.
        string[] backups = BackupFiles();
        Assert.Single(backups);
        Assert.Equal(original, File.ReadAllBytes(backups[0]));

        // Exactly one ledger line with the right metadata.
        string[] lines = LedgerLines();
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.Equal("lock", root.GetProperty("Op").GetString());
        Assert.Equal("report.pdf", root.GetProperty("OriginalName").GetString());
        Assert.Equal(Path.GetFullPath(file), root.GetProperty("SourcePath").GetString());
        Assert.Equal(Path.GetFileName(backups[0]), root.GetProperty("BackupName").GetString());
    }

    [Fact]
    public void Process_Backup_IsNamedWithTimestampPrefixAndOriginalName()
    {
        string file = Write("report.pdf", RandomBytes(256));

        _service.Process(file, "pw");

        string backupName = Path.GetFileName(BackupFiles().Single());
        // "[yyyy-MM-dd HH-mm-ss] " prefix, then the verbatim original file name.
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}\] report\.pdf$", backupName);
    }

    [Fact]
    public void Process_LockedFile_UnlocksInPlace_WithNoNewBackup()
    {
        byte[] original = RandomBytes(2048);
        string file = Write("notes.txt", original);

        _service.Process(file, "pw");                       // lock
        ToggleResult result = _service.Process(file, "pw"); // unlock

        Assert.Equal(LockOperation.Unlocked, result.Operation);
        Assert.False(FileCryptor.IsLocked(file));
        Assert.Equal(original, File.ReadAllBytes(file));

        // Unlock takes no backup → still just the single lock-time backup + ledger line.
        Assert.Single(BackupFiles());
        Assert.Single(LedgerLines());
    }

    [Fact]
    public void Process_WrongPasswordOnUnlock_LeavesFileLocked_AndAddsNoBackup()
    {
        byte[] original = RandomBytes(4096);
        string file = Write("secret.bin", original);

        _service.Process(file, "right"); // lock with one password
        byte[] lockedBytes = File.ReadAllBytes(file);

        // Detected as locked → tries to unlock → wrong password.
        Assert.Throws<WrongPasswordException>(() => _service.Process(file, "wrong"));

        Assert.Equal(lockedBytes, File.ReadAllBytes(file)); // untouched, still locked
        Assert.True(FileCryptor.IsLocked(file));
        Assert.Single(BackupFiles());  // unlock attempt added nothing
        Assert.Single(LedgerLines());
    }

    [Fact]
    public void Process_BackupNameCollision_PreservesOriginalNamesAndContents()
    {
        byte[] dataA = RandomBytes(300);
        byte[] dataB = RandomBytes(300);
        string fileA = Write("clash.txt", dataA, Path.Combine(_workDir, "a"));
        string fileB = Write("clash.txt", dataB, Path.Combine(_workDir, "b"));

        _service.Process(fileA, "pw");
        _service.Process(fileB, "pw");

        // Two distinct backup files despite the shared original name.
        string[] backups = BackupFiles();
        Assert.Equal(2, backups.Length);
        Assert.Equal(2, backups.Select(Path.GetFileName).Distinct().Count());

        // Both backup contents survive (matched against the two originals).
        byte[][] backupContents = backups.Select(File.ReadAllBytes).ToArray();
        Assert.Contains(backupContents, b => b.SequenceEqual(dataA));
        Assert.Contains(backupContents, b => b.SequenceEqual(dataB));

        // Two ledger lines, both preserving the real original name.
        string[] lines = LedgerLines();
        Assert.Equal(2, lines.Length);
        foreach (string line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("clash.txt", doc.RootElement.GetProperty("OriginalName").GetString());
        }
    }

    [Fact]
    public void Process_Folder_Throws()
    {
        string dir = Path.Combine(_workDir, "afolder");
        Directory.CreateDirectory(dir);
        Assert.Throws<FileLockException>(() => _service.Process(dir, "pw"));
    }
}
