namespace FileLock.Core;

/// <summary>
/// On-disk layout and constants for a FileLock (<c>.locked</c>) file.
///
/// Header (62 bytes, all multi-byte integers little-endian, all binary):
///   off  0  size  4  Magic           ASCII "FLK1"
///   off  4  size  2  Format version  0x0001
///   off  6  size  4  KDF iterations  PBKDF2 iteration count actually used
///   off 10  size 16  KDF salt        random per file
///   off 26  size 12  GCM nonce       random per file
///   off 38  size  8  Encryption date Unix time (UTC, seconds), fed into GCM as AAD
///   off 46  size 16  GCM auth tag    produced by AesGcm.Encrypt
///   off 62  ...      Ciphertext      encrypts [2-byte name len][name UTF-8][file bytes]
///
/// The AAD (authenticated-but-not-encrypted data) is header bytes 0..45 — everything
/// up to but not including the tag. This binds magic/version/iterations/salt/nonce/date
/// to the ciphertext so none of them can be silently altered.
/// </summary>
public static class FileFormat
{
    /// <summary>ASCII "FLK1" as a zero-allocation UTF-8 literal.</summary>
    public static ReadOnlySpan<byte> Magic => "FLK1"u8;

    public const ushort Version = 1;

    /// <summary>
    /// PBKDF2-HMAC-SHA256 iteration count. Stored in the header so a future format
    /// version can raise it without breaking files written today.
    /// </summary>
    public const int Pbkdf2Iterations = 600_000;

    public const int SaltSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int KeySize = 32; // AES-256

    // Header field offsets.
    public const int MagicOffset = 0;
    public const int MagicSize = 4;
    public const int VersionOffset = 4;
    public const int IterationsOffset = 6;
    public const int SaltOffset = 10;
    public const int NonceOffset = 26;
    public const int DateOffset = 38;
    public const int TagOffset = 46;

    /// <summary>Total header size in bytes, before the ciphertext begins.</summary>
    public const int HeaderSize = 62;

    /// <summary>Size of the associated-data region (header bytes 0..45).</summary>
    public const int AadSize = 46;

    /// <summary>
    /// Maximum plaintext size accepted in v1. AesGcm one-shot needs the whole buffer in
    /// memory, so we refuse anything larger rather than risk an out-of-memory failure.
    /// </summary>
    public const long MaxInputSize = 500L * 1024 * 1024; // 500 MB

    /// <summary>Extension appended to a locked file (e.g. report.pdf -> report.pdf.locked).</summary>
    public const string LockedExtension = ".locked";
}
