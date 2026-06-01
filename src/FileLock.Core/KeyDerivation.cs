using System.Security.Cryptography;

namespace FileLock.Core;

/// <summary>
/// Password-to-key derivation. Uses PBKDF2-HMAC-SHA256 from the BCL only — no third-party
/// crypto, no custom KDF.
/// </summary>
public static class KeyDerivation
{
    /// <summary>
    /// Derives a key into <paramref name="destination"/> (its length sets the key size). The
    /// span-based one-shot API UTF-8-encodes the password internally, so no password byte
    /// array is allocated or left lingering on the heap. Keys are never cached; the caller
    /// owns <paramref name="destination"/> and should zero it when done.
    /// </summary>
    public static void DeriveKey(
        ReadOnlySpan<char> password,
        ReadOnlySpan<byte> salt,
        int iterations,
        Span<byte> destination)
    {
        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations));

        Rfc2898DeriveBytes.Pbkdf2(password, salt, destination, iterations, HashAlgorithmName.SHA256);
    }
}
