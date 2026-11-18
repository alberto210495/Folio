using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace Folio.Services;

/// <summary>
/// Keeps the Finnhub key encrypted at rest. On Windows, a plain-text <c>Finnhub:ApiKey</c> in your user secrets is
/// replaced with <c>Finnhub:ApiKeyProtected</c> (Windows data protection, your account only) the first time Folio
/// starts, and decrypted into memory on every start. Setting a new key with <c>dotnet user-secrets set</c> works
/// as before: it's encrypted at the next start. An environment variable key is left alone.
/// </summary>
public static class ApiKeyStore
{
    public const string PlainKey = "Finnhub:ApiKey";
    public const string ProtectedKey = "Finnhub:ApiKeyProtected";

    public static string? SecretsPath()
    {
        var id = typeof(ApiKeyStore).Assembly.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
        return id is null ? null : PathHelper.GetSecretsPathFromSecretsId(id);
    }

    /// <summary>
    /// Encrypts a plain key found in <paramref name="secretsPath"/>, then makes sure <paramref name="config"/> has the
    /// key in memory. Returns a note for the log (never containing the key), or null when there was nothing to do.
    /// </summary>
    public static string? ProtectAndLoad(IConfiguration config, string? secretsPath)
    {
        string? note = null;
        var current = config[PlainKey];
        if (LocalProtection.IsAvailable && secretsPath is not null && File.Exists(secretsPath))
            note = MigrateFile(secretsPath);

        // In Development the user-secrets file reloads on change, so rewriting it would drop the key from memory.
        // Setting it through the configuration root stores it in every provider, which keeps it for this run.
        if (!string.IsNullOrWhiteSpace(current)) config[PlainKey] = current;

        if (string.IsNullOrWhiteSpace(config[PlainKey]))
        {
            var stored = config[ProtectedKey] ?? ReadProtected(secretsPath);
            if (!string.IsNullOrWhiteSpace(stored))
            {
                var key = LocalProtection.Unprotect(stored);
                if (key is null) return "The saved Finnhub key couldn't be decrypted (it was encrypted by another Windows account or PC). Set it again with: dotnet user-secrets set Finnhub:ApiKey <key>";
                config[PlainKey] = key;
            }
        }
        return note;
    }

    /// <summary>Replaces a plain-text key in the secrets file with its encrypted form. Returns a log note if it did.</summary>
    public static string? MigrateFile(string path)
    {
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return null; } // not ours to repair

        // "dotnet user-secrets set" writes flat "Finnhub:ApiKey"; a hand-edited file may nest it.
        var plain = root[PlainKey]?.GetValue<string>();
        var nested = root["Finnhub"] as JsonObject;
        plain ??= nested?["ApiKey"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(plain)) return null;

        root[ProtectedKey] = LocalProtection.Protect(plain);
        root.Remove(PlainKey);
        nested?.Remove("ApiKey");
        if (nested is { Count: 0 }) root.Remove("Finnhub");

        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
        return "Encrypted the Finnhub key in your user secrets with Windows data protection.";
    }

    /// <summary>
    /// Saves <paramref name="key"/> in the user-secrets file at <paramref name="path"/>, replacing any key there: encrypted
    /// (<see cref="ProtectedKey"/>) on Windows, plain (as <c>dotnet user-secrets set</c> writes it) elsewhere. Other
    /// settings in the file are kept.
    /// </summary>
    public static void Save(string path, string key)
    {
        var root = new JsonObject();
        if (File.Exists(path))
        {
            try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
            catch (JsonException)
            {
                throw new SetupRejectedException($"Your user-secrets file ({path}) isn't valid JSON, so Folio won't overwrite it. Fix or delete it, then try again.");
            }
        }

        root.Remove(PlainKey);
        root.Remove(ProtectedKey);
        if (root["Finnhub"] is JsonObject nested)
        {
            nested.Remove("ApiKey");
            if (nested.Count == 0) root.Remove("Finnhub");
        }
        if (LocalProtection.IsAvailable) root[ProtectedKey] = LocalProtection.Protect(key);
        else root[PlainKey] = key;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static string? ReadProtected(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try { return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?[ProtectedKey]?.GetValue<string>(); }
        catch (JsonException) { return null; }
    }
}
