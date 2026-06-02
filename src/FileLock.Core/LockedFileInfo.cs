namespace FileLock.Core;

/// <summary>
/// Plaintext metadata read from a locked file's header <em>without</em> a password
/// (see <see cref="FileCryptor.ReadHeaderInfo"/>). The values are authenticated as AAD,
/// so they are tamper-evident — but that authentication is only <em>verified</em> when the
/// file is actually unlocked. Treat a peeked value as informational until then.
/// </summary>
/// <param name="Version">On-disk format version.</param>
/// <param name="LockedAtUtc">When the file was locked.</param>
/// <param name="LockedBy">User name of whoever locked the file (may be empty).</param>
public sealed record LockedFileInfo(int Version, DateTimeOffset LockedAtUtc, string LockedBy);
