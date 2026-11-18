using System.Text.Json;

namespace FolioTray;

/// <summary>What the tray needs from Folio's own settings files, which sit in the same folder.</summary>
internal sealed record TraySettings(string AppDirectory, string FolioExe, string Url, string DataDirectory)
{
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// The address to open: with Folio's local access key when there is one (it unlocks the browser once, then
    /// Folio redirects to the plain address). Read on every click, since Folio creates the key on its first start.
    /// </summary>
    public string OpenUrl()
    {
        var keyFile = Path.Combine(DataDirectory, "access.key");
        try
        {
            if (File.Exists(keyFile) && File.ReadAllText(keyFile).Trim() is { Length: >= 32 } key)
                return $"{Url.TrimEnd('/')}/unlock?key={Uri.EscapeDataString(key)}";
        }
        catch (IOException) { }
        return Url;
    }

    public static TraySettings Load(string appDir)
    {
        var url = "https://localhost:7245";
        var data = appDir;
        foreach (var file in new[] { "appsettings.json", "appsettings.Production.json" }) // later files win, as in Folio
        {
            var path = Path.Combine(appDir, file);
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var root = doc.RootElement;
                // Folio:Url since the installed copy moved to plain http://localhost; Kestrel's endpoint before that.
                if ((Get(root, "Folio", "Url") ?? Get(root, "Kestrel", "Endpoints", "Https", "Url")) is { Length: > 0 } u) url = u;
                if (Get(root, "Folio", "DataDirectory") is { Length: > 0 } d) data = Path.GetFullPath(Environment.ExpandEnvironmentVariables(d));
            }
            catch (JsonException) { /* fall back to defaults */ }
        }
        return new(appDir, Path.Combine(appDir, "Folio.exe"), url, data);
    }

    private static string? Get(JsonElement e, params string[] path)
    {
        foreach (var name in path)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            var match = e.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match.Value.ValueKind == JsonValueKind.Undefined) return null;
            e = match.Value;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }
}
