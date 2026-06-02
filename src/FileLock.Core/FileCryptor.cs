using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileLock.Core;

/// <summary>
/// Locks (encrypts) and unlocks (decrypts) files with AES-256-GCM, using a key derived
/// from the user's password via PBKDF2. The class is UI-agnostic so it can be unit-tested
/// without WPF.
///
/// Two output styles are provided:
///   * <see cref="Lock"/> / <see cref="Unlock"/> write a <b>new</b> file (never overwriting).
///   * <see cref="LockInPlace"/> / <see cref="UnlockInPlace"/> <b>replace the source file</b>
///     in place. The toggle workflow uses these; callers are responsible for backing the
///     file up first (see <see cref="BackupStore"/>).
///
/// Fail-closed guarantees:
///   * Output is written to a temp file first and only atomically moved into place once it
///     is fully written, so a crash never leaves a partial/garbage output file.
///   * Any authentication failure on unlock throws and writes nothing — the source is left
///     exactly as it was.
///
/// Allocation strategy: the small fixed-size secrets (salt, nonce, tag, key) and the
/// variable header (≤ 320 bytes) live on the stack; the large plaintext/ciphertext buffers
/// are rented from <see cref="ArrayPool{T}"/> and every buffer that held plaintext is zeroed
/// before being returned to the pool.
/// </summary>
public sealed class FileCryptor
{
    /// <summary>
    /// Encrypts <paramref name="inputPath"/> to a new file <paramref name="outputPath"/>
    /// (never overwriting). The original file name is stored inside the encrypted payload so
    /// unlock can restore it; <paramref name="lockedBy"/> (defaulting to the current user) is
    /// recorded in the header.
    /// </summary>
    /// <exception cref="FileTooLargeException">Input exceeds the v1 size limit.</exception>
    /// <exception cref="IOException">Output already exists.</exception>
    public void Lock(string inputPath, string outputPath, string password, string? lockedBy = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        LockToFile(inputPath, outputPath, password, lockedBy, overwrite: false);
    }

    /// <summary>
    /// Encrypts <paramref name="path"/> and atomically replaces it with the locked bytes.
    /// The whole file is read into memory before anything is written, so reading and writing
    /// the same path is safe. The original name is still stored inside the payload.
    /// </summary>
    public void LockInPlace(string path, string password, string? lockedBy = null)
        => LockToFile(path, path, password, lockedBy, overwrite: true);

    /// <summary>
    /// Decrypts <paramref name="inputPath"/> into <paramref name="outputDir"/>, restoring the
    /// original file name (de-duplicated with " (1)", " (2)", … on collision). Never overwrites.
    /// </summary>
    /// <returns>The full path of the restored file.</returns>
    /// <exception cref="BadFormatException">Not a FileLock file or header is corrupt.</exception>
    /// <exception cref="WrongPasswordException">Wrong password or the file was modified.</exception>
    public string Unlock(string inputPath, string outputDir, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDir);
        return UnlockCore(inputPath, password,
            chooseOutputPath: safeName => GetAvailablePath(Path.Combine(outputDir, safeName)),
            overwrite: false);
    }

    /// <summary>
    /// Decrypts <paramref name="path"/> and atomically replaces it with the original bytes.
    /// The file keeps its current name (the name stored inside is ignored for placement).
    /// On any authentication failure the source is left untouched.
    /// </summary>
    public void UnlockInPlace(string path, string password)
        => UnlockCore(path, password, chooseOutputPath: _ => path, overwrite: true);

    /// <summary>
    /// True if <paramref name="path"/> begins with the FileLock magic, i.e. it is a locked
    /// file. Cheap — reads only the first few bytes. Used to decide lock-vs-unlock.
    /// </summary>
    public static bool IsLocked(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < FileFormat.MagicSize)
            return false;

        Span<byte> magic = stackalloc byte[FileFormat.MagicSize];
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ReadExactly(handle, magic, fileOffset: 0);
        return magic.SequenceEqual(FileFormat.Magic);
    }

    /// <summary>
    /// Reads a locked file's plaintext header (no password required) and returns its version,
    /// lock date, and the user name of whoever locked it.
    /// </summary>
    /// <exception cref="BadFormatException">Not a FileLock file or header is corrupt.</exception>
    public static LockedFileInfo ReadHeaderInfo(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The file was not found.", path);
        if (info.Length < FileFormat.FixedPrefixSize)
            throw new BadFormatException();

        Span<byte> prefix = stackalloc byte[FileFormat.FixedPrefixSize];
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        ReadExactly(handle, prefix, fileOffset: 0);

        if (!prefix[..FileFormat.MagicSize].SequenceEqual(FileFormat.Magic))
            throw new BadFormatException();

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(FileFormat.VersionOffset, 2));
        long unixDate = BinaryPrimitives.ReadInt64LittleEndian(prefix.Slice(FileFormat.DateOffset, 8));
        int userLen = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(FileFormat.UsernameLenOffset, 2));
        if (userLen > FileFormat.MaxUsernameBytes)
            throw new BadFormatException();

        string lockedBy = string.Empty;
        if (userLen > 0)
        {
            if (info.Length < FileFormat.UsernameOffset + userLen)
                throw new BadFormatException();
            Span<byte> nameSpan = stackalloc byte[userLen];
            ReadExactly(handle, nameSpan, fileOffset: FileFormat.UsernameOffset);
            lockedBy = Encoding.UTF8.GetString(nameSpan);
        }

        return new LockedFileInfo(version, DateTimeOffset.FromUnixTimeSeconds(unixDate), lockedBy);
    }

    // ── Lock / Unlock cores ──────────────────────────────────────────────────────

    private void LockToFile(string inputPath, string outputPath, string password, string? lockedBy, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentNullException.ThrowIfNull(password);

        var info = new FileInfo(inputPath);
        if (!info.Exists)
            throw new FileNotFoundException("The file to lock was not found.", inputPath);
        long fileLength = info.Length;
        if (fileLength > FileFormat.MaxInputSize)
            throw new FileTooLargeException(fileLength, FileFormat.MaxInputSize);

        string originalName = Path.GetFileName(inputPath);
        int nameByteCount = Encoding.UTF8.GetByteCount(originalName);
        if (nameByteCount > ushort.MaxValue)
            throw new FileLockException("The file name is too long to lock.");

        byte[] userBytes = EncodeUsername(lockedBy ?? Environment.UserName);
        int userLen = userBytes.Length;

        // Plaintext payload = [2-byte name len][name UTF-8][file bytes].
        int payloadLength = checked(2 + nameByteCount + (int)fileLength);
        byte[] payload = ArrayPool<byte>.Shared.Rent(payloadLength);
        byte[] ciphertext = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)nameByteCount);
            Encoding.UTF8.GetBytes(originalName, payload.AsSpan(2, nameByteCount));
            // Read the file contents straight into the payload — no separate copy.
            ReadExactlyFromFile(inputPath, payload.AsSpan(2 + nameByteCount, (int)fileLength));

            // Build the variable-length header on the stack (≤ 320 bytes). The tag region at
            // the end is filled by AesGcm.Encrypt; bytes [0 .. tagOffset) are the AAD.
            Span<byte> header = stackalloc byte[FileFormat.HeaderSize(userLen)];
            Span<byte> salt = header.Slice(FileFormat.SaltOffset, FileFormat.SaltSize);
            Span<byte> nonce = header.Slice(FileFormat.NonceOffset, FileFormat.NonceSize);
            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);

            FileFormat.Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(FileFormat.VersionOffset, 2), FileFormat.Version);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(FileFormat.IterationsOffset, 4), FileFormat.Pbkdf2Iterations);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(FileFormat.DateOffset, 8), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(FileFormat.UsernameLenOffset, 2), (ushort)userLen);
            userBytes.CopyTo(header.Slice(FileFormat.UsernameOffset, userLen));

            ReadOnlySpan<byte> aad = header[..FileFormat.AadSize(userLen)];
            Span<byte> tag = header.Slice(FileFormat.TagOffset(userLen), FileFormat.TagSize);

            Span<byte> key = stackalloc byte[FileFormat.KeySize];
            try
            {
                KeyDerivation.DeriveKey(password, salt, FileFormat.Pbkdf2Iterations, key);
                using var aes = new AesGcm(key, FileFormat.TagSize);
                aes.Encrypt(nonce, payload.AsSpan(0, payloadLength), ciphertext.AsSpan(0, payloadLength), tag, aad);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            // header now carries the tag → write header + ciphertext.
            PersistAtomically(outputPath, header, ciphertext.AsSpan(0, payloadLength), overwrite);
        }
        finally
        {
            // payload held the plaintext — always zero it before returning to the pool.
            CryptographicOperations.ZeroMemory(payload.AsSpan(0, payloadLength));
            ArrayPool<byte>.Shared.Return(payload);
            ArrayPool<byte>.Shared.Return(ciphertext);
        }
    }

    private string UnlockCore(string inputPath, string password, Func<string, string> chooseOutputPath, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentNullException.ThrowIfNull(password);

        var info = new FileInfo(inputPath);
        if (!info.Exists)
            throw new FileNotFoundException("The file to unlock was not found.", inputPath);
        long total = info.Length;
        if (total < FileFormat.MinHeaderSize)
            throw new BadFormatException();

        // Pass 1: read the fixed prefix to learn the (variable) user-name length.
        Span<byte> prefix = stackalloc byte[FileFormat.FixedPrefixSize];
        using (SafeFileHandle prefixHandle = File.OpenHandle(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
            ReadExactly(prefixHandle, prefix, fileOffset: 0);

        if (!prefix[..FileFormat.MagicSize].SequenceEqual(FileFormat.Magic))
            throw new BadFormatException();
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(FileFormat.VersionOffset, 2));
        if (version != FileFormat.Version)
            throw new BadFormatException("This file was made by a newer version of FileLock.");
        int iterations = BinaryPrimitives.ReadInt32LittleEndian(prefix.Slice(FileFormat.IterationsOffset, 4));
        if (iterations <= 0)
            throw new BadFormatException();
        int userLen = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(FileFormat.UsernameLenOffset, 2));
        if (userLen > FileFormat.MaxUsernameBytes) // we never write more; reject anything larger
            throw new BadFormatException();

        int headerSize = FileFormat.HeaderSize(userLen);
        if (total < headerSize)
            throw new BadFormatException();
        long cipherLong = total - headerSize;
        if (cipherLong > FileFormat.MaxInputSize)
            throw new FileTooLargeException(total, FileFormat.MaxInputSize);
        int cipherLength = (int)cipherLong;

        // Pass 2: read the full header + ciphertext into memory, then CLOSE the handle before
        // we (possibly) overwrite the source — Windows won't replace a file with an open handle.
        Span<byte> header = stackalloc byte[headerSize];
        byte[] cipher = ArrayPool<byte>.Shared.Rent(Math.Max(1, cipherLength));
        byte[] plaintext = ArrayPool<byte>.Shared.Rent(Math.Max(1, cipherLength));
        try
        {
            using (SafeFileHandle handle = File.OpenHandle(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
            {
                ReadExactly(handle, header, fileOffset: 0);
                ReadExactly(handle, cipher.AsSpan(0, cipherLength), fileOffset: headerSize);
            }

            ReadOnlySpan<byte> salt = header.Slice(FileFormat.SaltOffset, FileFormat.SaltSize);
            ReadOnlySpan<byte> nonce = header.Slice(FileFormat.NonceOffset, FileFormat.NonceSize);
            ReadOnlySpan<byte> aad = header[..FileFormat.AadSize(userLen)];
            ReadOnlySpan<byte> tag = header.Slice(FileFormat.TagOffset(userLen), FileFormat.TagSize);

            Span<byte> key = stackalloc byte[FileFormat.KeySize];
            try
            {
                KeyDerivation.DeriveKey(password, salt, iterations, key);
                using var aes = new AesGcm(key, FileFormat.TagSize);
                aes.Decrypt(nonce, cipher.AsSpan(0, cipherLength), tag, plaintext.AsSpan(0, cipherLength), aad);
            }
            catch (AuthenticationTagMismatchException)
            {
                // Wrong password and tampering are indistinguishable here — report both.
                throw new WrongPasswordException();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            // Parse payload = [2-byte name len][name UTF-8][file bytes].
            if (cipherLength < 2)
                throw new BadFormatException();
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(plaintext.AsSpan(0, 2));
            if (2 + nameLen > cipherLength)
                throw new BadFormatException();

            string storedName = Encoding.UTF8.GetString(plaintext.AsSpan(2, nameLen));
            // Never trust the stored name for traversal — keep only the file-name part.
            string safeName = Path.GetFileName(storedName);
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "recovered.bin";

            string outputPath = chooseOutputPath(safeName);
            int dataOffset = 2 + nameLen;
            PersistAtomically(outputPath, ReadOnlySpan<byte>.Empty, plaintext.AsSpan(dataOffset, cipherLength - dataOffset), overwrite);
            return outputPath;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, Math.Max(1, cipherLength)));
            ArrayPool<byte>.Shared.Return(cipher);
            ArrayPool<byte>.Shared.Return(plaintext);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <paramref name="desiredPath"/> if free, otherwise the same name with
    /// " (1)", " (2)", … inserted before the extension until an unused path is found.
    /// </summary>
    public static string GetAvailablePath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
            return desiredPath;

        string dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        string nameNoExt = Path.GetFileNameWithoutExtension(desiredPath);
        string ext = Path.GetExtension(desiredPath);

        for (int i = 1; i < int.MaxValue; i++)
        {
            string candidate = Path.Combine(dir, $"{nameNoExt} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
        throw new IOException("Could not find an available output file name.");
    }

    /// <summary>UTF-8-encodes <paramref name="user"/>, truncating to a whole-character prefix
    /// that fits in <see cref="FileFormat.MaxUsernameBytes"/>.</summary>
    private static byte[] EncodeUsername(string? user)
    {
        if (string.IsNullOrEmpty(user))
            return [];
        if (Encoding.UTF8.GetByteCount(user) <= FileFormat.MaxUsernameBytes)
            return Encoding.UTF8.GetBytes(user);

        // Rare path (very long name): drop trailing characters until it fits.
        for (int len = user.Length - 1; len > 0; len--)
        {
            if (Encoding.UTF8.GetByteCount(user.AsSpan(0, len)) <= FileFormat.MaxUsernameBytes)
                return Encoding.UTF8.GetBytes(user[..len]);
        }
        return [];
    }

    /// <summary>
    /// Writes <paramref name="header"/> followed by <paramref name="body"/> to a temp file in
    /// the destination directory, flushes it to disk, then atomically moves it onto
    /// <paramref name="outputPath"/>. The temp file shares the destination's volume, so the
    /// move is atomic. The temp file is removed on any failure.
    /// </summary>
    private static void PersistAtomically(string outputPath, ReadOnlySpan<byte> header, ReadOnlySpan<byte> body, bool overwrite)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $".filelock-{Guid.NewGuid():N}.tmp");

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!header.IsEmpty)
                    fs.Write(header);
                if (!body.IsEmpty)
                    fs.Write(body);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempPath, outputPath, overwrite);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Reads exactly <c>destination.Length</c> bytes of <paramref name="path"/> into the span.</summary>
    private static void ReadExactlyFromFile(string path, Span<byte> destination)
    {
        if (destination.IsEmpty)
            return;
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        ReadExactly(handle, destination, fileOffset: 0);
    }

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="handle"/>, looping over short reads.</summary>
    private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long fileOffset)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = RandomAccess.Read(handle, buffer[filled..], fileOffset + filled);
            if (read == 0)
                throw new BadFormatException(); // file ended sooner than its length claimed
            filled += read;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; never mask the original error.
        }
    }
}
