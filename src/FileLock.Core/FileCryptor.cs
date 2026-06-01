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
/// Fail-closed guarantees:
///   * Output is written to a temp file first and only atomically moved into place once it
///     is fully written, so a crash never leaves a partial/garbage output file.
///   * The source file is never modified or deleted.
///   * Any authentication failure on unlock throws and writes nothing.
///
/// Allocation strategy: the small fixed-size secrets (salt, nonce, tag, key, header) live on
/// the stack; the large plaintext/ciphertext buffers are rented from <see cref="ArrayPool{T}"/>
/// and every buffer that held plaintext is zeroed before being returned to the pool.
/// </summary>
public sealed class FileCryptor
{
    /// <summary>
    /// Encrypts <paramref name="inputPath"/> to <paramref name="outputPath"/>. The original
    /// file name is stored inside the encrypted payload so unlock can restore it.
    /// </summary>
    /// <exception cref="FileTooLargeException">Input exceeds the v1 size limit.</exception>
    /// <exception cref="IOException">Output already exists (caller picks a free path).</exception>
    public void Lock(string inputPath, string outputPath, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
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

            // Build the 62-byte header on the stack. Bytes 0..45 are the AAD; 46..61 hold the tag.
            Span<byte> header = stackalloc byte[FileFormat.HeaderSize];
            Span<byte> salt = header.Slice(FileFormat.SaltOffset, FileFormat.SaltSize);
            Span<byte> nonce = header.Slice(FileFormat.NonceOffset, FileFormat.NonceSize);
            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);

            FileFormat.Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(FileFormat.VersionOffset, 2), FileFormat.Version);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(FileFormat.IterationsOffset, 4), FileFormat.Pbkdf2Iterations);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(FileFormat.DateOffset, 8), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            ReadOnlySpan<byte> aad = header[..FileFormat.AadSize];
            Span<byte> tag = header.Slice(FileFormat.TagOffset, FileFormat.TagSize);

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

            // header now carries the tag at offset 46 → write header + ciphertext.
            WriteAtomically(outputPath, header, ciphertext.AsSpan(0, payloadLength));
        }
        finally
        {
            // payload held the plaintext — always zero it before returning to the pool.
            CryptographicOperations.ZeroMemory(payload.AsSpan(0, payloadLength));
            ArrayPool<byte>.Shared.Return(payload);
            ArrayPool<byte>.Shared.Return(ciphertext);
        }
    }

    /// <summary>
    /// Decrypts <paramref name="inputPath"/> into <paramref name="outputDir"/>, restoring the
    /// original file name (de-duplicated with " (1)", " (2)", ... on collision).
    /// </summary>
    /// <returns>The full path of the restored file.</returns>
    /// <exception cref="BadFormatException">Not a FileLock file or header is corrupt.</exception>
    /// <exception cref="WrongPasswordException">Wrong password or the file was modified.</exception>
    public string Unlock(string inputPath, string outputDir, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);
        ArgumentNullException.ThrowIfNull(password);

        var info = new FileInfo(inputPath);
        if (!info.Exists)
            throw new FileNotFoundException("The file to unlock was not found.", inputPath);
        long total = info.Length;
        if (total < FileFormat.HeaderSize)
            throw new BadFormatException();
        if (total > FileFormat.MaxInputSize + FileFormat.HeaderSize)
            throw new FileTooLargeException(total, FileFormat.MaxInputSize);

        int cipherLength = checked((int)(total - FileFormat.HeaderSize));

        Span<byte> header = stackalloc byte[FileFormat.HeaderSize];
        byte[] ciphertext = ArrayPool<byte>.Shared.Rent(Math.Max(1, cipherLength));
        byte[] plaintext = ArrayPool<byte>.Shared.Rent(Math.Max(1, cipherLength));
        try
        {
            using (SafeFileHandle handle = File.OpenHandle(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
            {
                ReadExactly(handle, header, fileOffset: 0);
                ReadExactly(handle, ciphertext.AsSpan(0, cipherLength), fileOffset: FileFormat.HeaderSize);
            }

            if (!header[..FileFormat.MagicSize].SequenceEqual(FileFormat.Magic))
                throw new BadFormatException();

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(FileFormat.VersionOffset, 2));
            if (version != FileFormat.Version)
                throw new BadFormatException("This file was made by a newer version of FileLock.");

            int iterations = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(FileFormat.IterationsOffset, 4));
            if (iterations <= 0)
                throw new BadFormatException();

            ReadOnlySpan<byte> salt = header.Slice(FileFormat.SaltOffset, FileFormat.SaltSize);
            ReadOnlySpan<byte> nonce = header.Slice(FileFormat.NonceOffset, FileFormat.NonceSize);
            ReadOnlySpan<byte> tag = header.Slice(FileFormat.TagOffset, FileFormat.TagSize);
            ReadOnlySpan<byte> aad = header[..FileFormat.AadSize];

            Span<byte> key = stackalloc byte[FileFormat.KeySize];
            try
            {
                KeyDerivation.DeriveKey(password, salt, iterations, key);
                using var aes = new AesGcm(key, FileFormat.TagSize);
                aes.Decrypt(nonce, ciphertext.AsSpan(0, cipherLength), tag, plaintext.AsSpan(0, cipherLength), aad);
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

            string outputPath = GetAvailablePath(Path.Combine(outputDir, safeName));
            int dataOffset = 2 + nameLen;
            WriteAtomically(outputPath, ReadOnlySpan<byte>.Empty, plaintext.AsSpan(dataOffset, cipherLength - dataOffset));
            return outputPath;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, Math.Max(1, cipherLength)));
            ArrayPool<byte>.Shared.Return(ciphertext);
            ArrayPool<byte>.Shared.Return(plaintext);
        }
    }

    /// <summary>
    /// Returns <paramref name="desiredPath"/> if free, otherwise the same name with
    /// " (1)", " (2)", ... inserted before the extension until an unused path is found.
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

    /// <summary>
    /// Writes <paramref name="header"/> followed by <paramref name="body"/> to a temp file,
    /// flushes it to disk, then atomically moves it onto <paramref name="outputPath"/>.
    /// Never overwrites an existing output file; the temp file is removed on any failure.
    /// </summary>
    private static void WriteAtomically(string outputPath, ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
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
            File.Move(tempPath, outputPath, overwrite: false);
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
