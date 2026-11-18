namespace Folio.Services;

/// <summary>
/// Browser-facing hardening for a local, single-user app (see README "Security").
/// HTTPS, host filtering and loopback-only binding are configured in Program.cs and appsettings.json.
/// </summary>
public static class Security
{
    /// <summary>
    /// Content Security Policy. Scripts, styles, images, data and connections only from Folio itself (logos are
    /// served by Folio too, so nothing loads from elsewhere). Inline <c>style="…"</c> attributes are allowed (the charts set widths and
    /// colours that way), but inline scripts, eval, plugins, framing and foreign form targets are not.
    /// </summary>
    public const string BasePolicy =
        "default-src 'none'; " +
        "script-src 'self'; " +
        "style-src 'self'; style-src-attr 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "connect-src 'self'; " +
        "font-src 'self'; " +
        "manifest-src 'self'; " +
        "base-uri 'none'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'";

    /// <summary>
    /// The policy for a site served over HTTPS or plain HTTP. Over HTTPS it also asks the browser to upgrade any
    /// http:// request; over http://localhost (the installed copy) that would send every request to a port with
    /// no HTTPS and break the page, so it's left out.
    /// </summary>
    public static string ContentSecurityPolicy(bool https) => https ? BasePolicy + "; upgrade-insecure-requests" : BasePolicy;

    /// <summary>Security headers on every response, and no caching of API data.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, bool https = true) => app.Use(async (ctx, next) =>
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h.ContentSecurityPolicy = ContentSecurityPolicy(https);
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            // API data isn't cached; logos are (the endpoint sets its own cache header).
            if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/logo"))
                h.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        await next();
    });

    /// <summary>
    /// Refuses API requests that another website makes through your browser (cross-site request forgery,
    /// or a page reading your portfolio). Browsers label every request with <c>Sec-Fetch-Site</c> and, for
    /// writes, <c>Origin</c>; only Folio's own pages ("same-origin") and direct visits ("none") get through.
    /// Tools such as curl send neither header and are allowed: they can't be driven by a web page.
    /// </summary>
    public static IApplicationBuilder UseCrossSiteGuard(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api") && !IsAllowed(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "Cross-site requests aren't allowed." });
            return;
        }
        await next();
    });

    public static bool IsAllowed(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "none")) return false;

        // Older browsers without Sec-Fetch-*: a write must come from this exact origin.
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            var self = $"{request.Scheme}://{request.Host}";
            if (!string.Equals(origin, self, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
