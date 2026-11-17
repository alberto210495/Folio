using System.Security.Cryptography;
using System.Text;
using Folio.Data;
using Microsoft.EntityFrameworkCore;

namespace Folio.Services;

/// <summary>
/// Serves company logos from Folio itself, so your browser never contacts Finnhub's image servers (which would
/// tell them which tickers you look at, and from what IP). Each logo is downloaded once and cached in
/// <c>logos/</c> in the data folder; a changed logo URL (after a profile refresh) is fetched again.
/// Downloads are restricted: https on Finnhub's domain only, no redirects, at most <see cref="MaxBytes"/>, and only
/// real PNG, JPEG, WebP or GIF files (checked by their bytes). SVG is refused: it can carry scripts.
/// </summary>
public class LogoService(FolioDb db, IHttpClientFactory http, IHostEnvironment env, Microsoft.Extensions.Options.IOptions<FolioOptions> options, ILogger<LogoService> log)
{
    public const string HttpClientName = "logos";
    public const int MaxBytes = 512 * 1024;

    public string Directory
    {
        get
        {
            var data = options.Value.DataDirectory;
            var root = string.IsNullOrWhiteSpace(data) ? env.ContentRootPath : Environment.ExpandEnvironmentVariables(data);
            return Path.Combine(Path.GetFullPath(root, env.ContentRootPath), "logos");
        }
    }

    /// <summary>Only https URLs on finnhub.io (or a subdomain) are ever fetched.</summary>
    public static bool IsAllowedSource(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && u.IsDefaultPort
        && (u.Host.Equals("finnhub.io", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".finnhub.io", StringComparison.OrdinalIgnoreCase));

    /// <summary>The image's content type from its first bytes, or null if it isn't an allowed raster image.</summary>
    public static string? SniffImage(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        return null;
    }

    /// <summary>The cached logo for a symbol (fetching it the first time), or null if there's none.</summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetAsync(string symbol, CancellationToken ct = default)
    {
        symbol = TradingService.NormalizeSymbol(symbol); // also makes it safe as a file name
        var url = await db.Companies.AsNoTracking().Where(c => c.Symbol == symbol).Select(c => c.Logo).FirstOrDefaultAsync(ct);
        if (!IsAllowedSource(url)) return null;

        // The file name includes a hash of the URL, so a new logo URL means a new download.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url!)))[..12].ToLowerInvariant();
        var path = Path.Combine(Directory, $"{symbol}-{hash}.img");
        if (File.Exists(path))
        {
            var cached = await File.ReadAllBytesAsync(path, ct);
            if (SniffImage(cached) is { } type) return (cached, type);
        }

        var bytes = await DownloadAsync(url!, ct);
        if (bytes is null) return null;
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var stale in System.IO.Directory.GetFiles(Directory, $"{symbol}-*.img")) File.Delete(stale);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return (bytes, SniffImage(bytes)!);
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = http.CreateClient(HttpClientName);
            using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxBytes) return null;

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxBytes) return null; // don't trust Content-Length alone
                buffer.Write(chunk, 0, read);
            }
            var bytes = buffer.ToArray();
            if (SniffImage(bytes) is null)
            {
                log.LogWarning("Ignored a logo from {Host} that isn't a PNG, JPEG, WebP or GIF image", new Uri(url).Host);
                return null;
            }
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            log.LogInformation("Couldn't download a logo: {Message}", ex.Message);
            return null;
        }
    }
}
