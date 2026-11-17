using System.Security.Cryptography;
using System.Text;

namespace Folio.Services;

/// <summary>
/// A local access key, so only you can use Folio, not other Windows accounts or programs on this PC that can
/// reach localhost. The key is random (256 bits) and kept in a file only your account can read. The desktop
/// shortcut and tray open <c>/unlock?key=…</c> once; that sets a long-lived, HttpOnly, SameSite=Strict cookie, after
/// which normal URLs and bookmarks work. Scripts can send the key in the <c>X-Folio-Key</c> header instead.
/// </summary>
public sealed class AccessKey
{
    // "__Host-" makes the browser refuse the cookie unless it's Secure, Path=/ and has no Domain.
    public const string CookieName = "__Host-folio-access";
    public const string HeaderName = "X-Folio-Key";
    public const string UnlockPath = "/unlock";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(400); // the most browsers allow

    private readonly byte[] _token;
    private readonly byte[] _cookie;

    private AccessKey(string token)
    {
        Token = token;
        _token = Encoding.UTF8.GetBytes(token);
        // The cookie holds a value derived from the key, not the key itself.
        _cookie = Encoding.UTF8.GetBytes(Convert.ToHexString(HMACSHA256.HashData(_token, "folio-session-v1"u8)));
    }

    public string Token { get; }

    public string UnlockUrl(string siteUrl) => $"{siteUrl.TrimEnd('/')}{UnlockPath}?key={Uri.EscapeDataString(Token)}";

    /// <summary>Reads the key file, creating it with a new random key the first time.</summary>
    public static AccessKey LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return new AccessKey(existing);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(path, token);
        return new AccessKey(token);
    }

    /// <summary>Reads an existing key without creating one (null if there's no key file yet).</summary>
    public static AccessKey? TryLoad(string path) =>
        File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 32 } t ? new AccessKey(t) : null;

    public bool IsKey(string? candidate) => candidate is not null && FixedEquals(_token, candidate);

    public bool IsCookie(string? candidate) => candidate is not null && FixedEquals(_cookie, candidate);

    private static bool FixedEquals(byte[] expected, string candidate) =>
        CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(candidate));

    public void SetCookie(HttpResponse response) => response.Cookies.Append(CookieName, Encoding.UTF8.GetString(_cookie), new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = CookieLifetime,
        IsEssential = true,
    });
}

public static class AccessKeyMiddleware
{
    /// <summary>Lets through only requests with the access cookie or header; handles <c>/unlock</c>.</summary>
    public static IApplicationBuilder UseAccessKey(this IApplicationBuilder app, AccessKey key) => app.Use(async (ctx, next) =>
    {
        var req = ctx.Request;
        if (req.Path.Equals(AccessKey.UnlockPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!key.IsKey(req.Query["key"].ToString()))
            {
                await Locked(ctx, StatusCodes.Status403Forbidden, "That link has the wrong key.");
                return;
            }
            key.SetCookie(ctx.Response);
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Redirect("/", permanent: false); // drop the key from the address bar
            return;
        }

        if (key.IsCookie(req.Cookies[AccessKey.CookieName]) || key.IsKey(req.Headers[AccessKey.HeaderName].ToString()))
        {
            await next();
            return;
        }

        if (req.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Folio is locked. Open it from the Folio shortcut on your desktop or the tray icon." });
            return;
        }
        await Locked(ctx, StatusCodes.Status401Unauthorized, null);
    });

    // Self-contained (no scripts, no external files) so it works under the strict Content Security Policy.
    private static Task Locked(HttpContext ctx, int status, string? problem)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        var extra = problem is null ? "" : $"<p style=\"color:#d39a9a\">{System.Net.WebUtility.HtmlEncode(problem)}</p>";
        return ctx.Response.WriteAsync($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Folio is locked</title></head>
            <body style="margin:0;min-height:100vh;display:grid;place-items:center;background:#070809;color:#e6eff4;font:15px/1.6 system-ui,sans-serif">
            <main style="max-width:420px;padding:24px;text-align:center">
            <div style="width:18px;height:18px;border-radius:5px;background:#dcebf3;margin:0 auto 18px"></div>
            <h1 style="font-size:12px;font-weight:500;letter-spacing:.09em;text-transform:uppercase;color:#a3adb4;margin:0 0 10px">Folio is locked</h1>
            <p style="margin:0 0 10px">Open Folio from the <b>Folio</b> shortcut on your desktop, or <b>Open Folio</b> on the tray icon by the clock.</p>
            {{extra}}
            <p style="color:#6c757c;font-size:13px;margin:16px 0 0">Only your Windows account has the key, so other accounts and programs on this PC can't use Folio.</p>
            </main></body></html>
            """);
    }
}
