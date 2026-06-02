using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileLock.UI;

/// <summary>
/// Loads/saves <c>settings.json</c> in the install folder. The default password is stored
/// DPAPI-encrypted (Windows Data Protection, <see cref="DataProtectionScope.CurrentUser"/>),
/// so the on-disk blob can only be decrypted by the same Windows user on this machine — a
/// copied <c>settings.json</c> is useless elsewhere. The install folder is treated as a
/// trusted location; the password's job is to protect files <em>in transit</em>.
/// </summary>
public sealed class SettingsStore
{
    // Static app-specific entropy mixed into DPAPI. Not a secret — defense in depth only.
    private static readonly byte[] Entropy = "FileLock.settings.v1"u8.ToArray();

    private readonly string _path;
    private SettingsData _data;

    public SettingsStore(string baseDirectory)
    {
        _path = Path.Combine(baseDirectory, "settings.json");
        _data = Load(_path);
    }

    public string SettingsPath => _path;

    public bool HasPassword => !string.IsNullOrEmpty(_data.ProtectedPassword);

    /// <summary>Decrypts and returns the stored default password.</summary>
    public string GetPassword()
    {
        if (string.IsNullOrEmpty(_data.ProtectedPassword))
            throw new InvalidOperationException("No password is configured.");

        byte[] cipher = Convert.FromBase64String(_data.ProtectedPassword);
        byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Encrypts and persists a new default password.</summary>
    public void SetPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] plain = Encoding.UTF8.GetBytes(password);
        try
        {
            byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            _data = _data with { ProtectedPassword = Convert.ToBase64String(cipher) };
            Save();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void Save()
    {
        string json = JsonSerializer.Serialize(_data, SettingsJsonContext.Default.SettingsData);
        File.WriteAllText(_path, json);
    }

    private static SettingsData Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsData) ?? new SettingsData();
            }
        }
        catch
        {
            // Corrupt/unreadable settings → behave as if unconfigured rather than crash.
        }
        return new SettingsData();
    }
}

internal sealed record SettingsData
{
    public string? ProtectedPassword { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SettingsData))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
