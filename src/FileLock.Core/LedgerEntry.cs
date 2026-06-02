using System.Text.Json.Serialization;

namespace FileLock.Core;

/// <summary>
/// One line in the backup ledger (<c>.backup/ledger.jsonl</c>) — written whenever a file is
/// locked. The on-disk backup file is renamed with a random token (collisions in the flat
/// <c>.backup</c> folder are likely), so this record is what maps that token back to the real
/// <see cref="OriginalName"/> and <see cref="SourcePath"/>.
/// </summary>
public sealed record LedgerEntry(
    DateTimeOffset Time,
    string Op,
    string SourcePath,
    string OriginalName,
    string BackupName,
    string? LockedBy,
    long Size);

/// <summary>Source-generated JSON metadata for <see cref="LedgerEntry"/> (no reflection;
/// trim/AOT-safe). System.Text.Json ships in the shared framework, so no NuGet is needed.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LedgerEntry))]
internal sealed partial class LedgerJsonContext : JsonSerializerContext;
