using System.Security.Cryptography;
using FileLock.Core;
using Xunit;

namespace FileLock.Core.Tests;

/// <summary>
/// Exercises the crypto core end-to-end against a throwaway temp directory. Each test gets
/// its own subfolder, cleaned up on dispose.
/// </summary>
public sealed class FileCryptorTests : IDisposable
{
    private readonly string _dir;
    private readonly FileCryptor _cryptor = new();

    public FileCryptorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "filelock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteInput(string name, byte[] contents)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static byte[] RandomBytes(int n)
    {
        byte[] b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    [Fact]
    public void RoundTrip_RestoresIdenticalBytesAndName()
    {
        byte[] original = RandomBytes(64 * 1024 + 7); // odd size, not a block multiple
        string input = WriteInput("report.pdf", original);
        string locked = input + FileFormat.LockedExtension;

        _cryptor.Lock(input, locked, "correct horse battery staple");

        Assert.True(File.Exists(locked));
        // Locked file must not contain the plaintext verbatim.
        Assert.False(ContainsSubsequence(File.ReadAllBytes(locked), original));

        // Unlock into a fresh dir so the restored name isn't de-duplicated.
        string outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(outDir);
        string restored = _cryptor.Unlock(locked, outDir, "correct horse battery staple");

        Assert.Equal("report.pdf", Path.GetFileName(restored));
        Assert.Equal(original, File.ReadAllBytes(restored));
    }

    [Fact]
    public void Unlock_WrongPassword_Throws()
    {
        string input = WriteInput("secret.txt", RandomBytes(1024));
        string locked = input + FileFormat.LockedExtension;
        _cryptor.Lock(input, locked, "the-right-password");

        var ex = Assert.Throws<WrongPasswordException>(
            () => _cryptor.Unlock(locked, _dir, "the-WRONG-password"));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Unlock_TamperedCiphertext_Throws()
    {
        string input = WriteInput("data.bin", RandomBytes(2048));
        string locked = input + FileFormat.LockedExtension;
        _cryptor.Lock(input, locked, "pw");

        // Flip one byte well inside the ciphertext.
        byte[] bytes = File.ReadAllBytes(locked);
        int i = FileFormat.HeaderSize + 100;
        bytes[i] ^= 0xFF;
        File.WriteAllBytes(locked, bytes);

        Assert.Throws<WrongPasswordException>(() => _cryptor.Unlock(locked, _dir, "pw"));
    }

    [Fact]
    public void Unlock_TamperedHeaderDate_Throws()
    {
        string input = WriteInput("data.bin", RandomBytes(2048));
        string locked = input + FileFormat.LockedExtension;
        _cryptor.Lock(input, locked, "pw");

        // The date is part of the AAD, so mutating it must fail authentication.
        byte[] bytes = File.ReadAllBytes(locked);
        bytes[FileFormat.DateOffset] ^= 0x01;
        File.WriteAllBytes(locked, bytes);

        Assert.Throws<WrongPasswordException>(() => _cryptor.Unlock(locked, _dir, "pw"));
    }

    [Fact]
    public void Unlock_NonFileLockFile_ThrowsBadFormat()
    {
        // Plenty of bytes, but the magic is wrong.
        string bogus = WriteInput("notlocked.locked", RandomBytes(FileFormat.HeaderSize + 50));
        Assert.Throws<BadFormatException>(() => _cryptor.Unlock(bogus, _dir, "pw"));
    }

    [Fact]
    public void Unlock_TooShortFile_ThrowsBadFormat()
    {
        string tiny = WriteInput("tiny.locked", RandomBytes(10));
        Assert.Throws<BadFormatException>(() => _cryptor.Unlock(tiny, _dir, "pw"));
    }

    [Fact]
    public void RoundTrip_EmptyFile_Works()
    {
        string input = WriteInput("empty.dat", Array.Empty<byte>());
        string locked = input + FileFormat.LockedExtension;
        _cryptor.Lock(input, locked, "pw");

        string outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(outDir);
        string restored = _cryptor.Unlock(locked, outDir, "pw");

        Assert.Equal("empty.dat", Path.GetFileName(restored));
        Assert.Empty(File.ReadAllBytes(restored));
    }

    [Fact]
    public void Lock_SameInputTwice_ProducesDifferentOutput()
    {
        byte[] original = RandomBytes(4096);
        string input = WriteInput("dup.bin", original);
        string lockedA = Path.Combine(_dir, "a.locked");
        string lockedB = Path.Combine(_dir, "b.locked");

        _cryptor.Lock(input, lockedA, "same-password");
        _cryptor.Lock(input, lockedB, "same-password");

        byte[] a = File.ReadAllBytes(lockedA);
        byte[] b = File.ReadAllBytes(lockedB);
        Assert.Equal(a.Length, b.Length);
        // Random salt + nonce => different ciphertext and different salt region.
        Assert.NotEqual(a, b);

        // Both still unlock to the same original.
        string outA = Path.Combine(_dir, "outA");
        string outB = Path.Combine(_dir, "outB");
        Directory.CreateDirectory(outA);
        Directory.CreateDirectory(outB);
        Assert.Equal(original, File.ReadAllBytes(_cryptor.Unlock(lockedA, outA, "same-password")));
        Assert.Equal(original, File.ReadAllBytes(_cryptor.Unlock(lockedB, outB, "same-password")));
    }

    [Fact]
    public void Unlock_NameCollision_AppendsSuffix()
    {
        byte[] original = RandomBytes(512);
        string input = WriteInput("clash.txt", original);
        string locked = input + FileFormat.LockedExtension;
        _cryptor.Lock(input, locked, "pw");

        string outDir = Path.Combine(_dir, "collide");
        Directory.CreateDirectory(outDir);
        // Pre-place a file with the same name as the one we'll restore.
        File.WriteAllText(Path.Combine(outDir, "clash.txt"), "existing");

        string restored = _cryptor.Unlock(locked, outDir, "pw");

        Assert.Equal("clash (1).txt", Path.GetFileName(restored));
        Assert.Equal(original, File.ReadAllBytes(restored));
        // Original colliding file is untouched.
        Assert.Equal("existing", File.ReadAllText(Path.Combine(outDir, "clash.txt")));
    }

    [Fact]
    public void Lock_DoesNotModifyOrDeleteSource()
    {
        byte[] original = RandomBytes(1000);
        string input = WriteInput("keepme.dat", original);
        string locked = input + FileFormat.LockedExtension;

        _cryptor.Lock(input, locked, "pw");

        Assert.True(File.Exists(input));
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    /// <summary>True if <paramref name="needle"/> appears as a contiguous run in <paramref name="haystack"/>.</summary>
    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
            return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
                j++;
            if (j == needle.Length)
                return true;
        }
        return false;
    }
}
