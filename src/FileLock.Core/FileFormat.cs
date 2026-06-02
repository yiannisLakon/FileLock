namespace FileLock.Core;

/// <summary>
/// On-disk layout and constants for a FileLock file.
///
/// The header is <b>variable-length</b> because it carries the locker's user name. Layout
/// (all multi-byte integers little-endian, all binary):
///   off  0       size  4  Magic            ASCII "FLK1"
///   off  4       size  2  Format version   0x0001
///   off  6       size  4  KDF iterations   PBKDF2 iteration count actually used
///   off 10       size 16  KDF salt         random per file
///   off 26       size 12  GCM nonce        random per file
///   off 38       size  8  Encryption date  Unix time (UTC, seconds)
///   off 46       size  2  User-name length N (UTF-8 byte count)
///   off 48       size  N  Locked-by        UTF-8 user name of whoever locked the file
///   off 48+N     size 16  GCM auth tag     produced by AesGcm.Encrypt
///   off 64+N     ...      Ciphertext       encrypts [2-byte name len][name UTF-8][file bytes]
///
/// The AAD (authenticated-but-not-encrypted data) is <b>everything before the tag</b>
/// (bytes <c>0 .. 48+N</c>). This binds magic/version/iterations/salt/nonce/date and the
/// locker user name to the ciphertext so none of them can be silently altered.
///
/// There is no legacy format: nothing was locked before this layout existed, so readers
/// support exactly one version and reject anything else.
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

    // Fixed header field offsets (everything up to and including the user-name length).
    public const int MagicOffset = 0;
    public const int MagicSize = 4;
    public const int VersionOffset = 4;
    public const int IterationsOffset = 6;
    public const int SaltOffset = 10;
    public const int NonceOffset = 26;
    public const int DateOffset = 38;
    public const int UsernameLenOffset = 46;

    /// <summary>Size of the fixed prefix (magic .. user-name length), before the user name begins.</summary>
    public const int FixedPrefixSize = 48;

    /// <summary>Offset at which the variable-length user name begins.</summary>
    public const int UsernameOffset = FixedPrefixSize; // 48

    /// <summary>Upper bound on the stored user name; longer names are truncated when locking.</summary>
    public const int MaxUsernameBytes = 256;

    /// <summary>
    /// Smallest possible header (an empty user name): fixed prefix + tag = 64 bytes. A file
    /// shorter than this cannot be a FileLock file.
    /// </summary>
    public const int MinHeaderSize = FixedPrefixSize + TagSize; // 64

    /// <summary>Offset of the GCM auth tag for a header carrying <paramref name="usernameByteCount"/> name bytes.</summary>
    public static int TagOffset(int usernameByteCount) => UsernameOffset + usernameByteCount;

    /// <summary>Total header size (up to where the ciphertext begins) for the given user-name length.</summary>
    public static int HeaderSize(int usernameByteCount) => UsernameOffset + usernameByteCount + TagSize;

    /// <summary>Size of the associated-data region (everything before the tag) for the given user-name length.</summary>
    public static int AadSize(int usernameByteCount) => UsernameOffset + usernameByteCount;

    /// <summary>
    /// Maximum plaintext size accepted in v1. AesGcm one-shot needs the whole buffer in
    /// memory, so we refuse anything larger rather than risk an out-of-memory failure.
    /// </summary>
    public const long MaxInputSize = 500L * 1024 * 1024; // 500 MB

    /// <summary>Conventional extension for a locked file. The toggle workflow no longer
    /// renames files, but the byte-level <see cref="FileCryptor.Lock"/> API still uses it.</summary>
    public const string LockedExtension = ".locked";
}
