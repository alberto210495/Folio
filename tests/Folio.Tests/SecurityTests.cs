using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Folio.Tests;

/// <summary>Hosts the real app (all middleware) on a throwaway database, without a Finnhub key.</summary>
public sealed class FolioFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"folio-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing"); // not Development, so user secrets (your real key) aren't loaded
        builder.UseSetting("ConnectionStrings:Folio", $"Data Source={_db}");
        builder.UseSetting("Finnhub:ApiKey", "");
        builder.UseSetting("https_port", "443");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch (IOException) { /* best effort */ }
    }
}

public class SecurityTests : IClassFixture<FolioFactory>
{
    private readonly FolioFactory _app;

    public SecurityTests(FolioFactory app) => _app = app;

    private HttpClient Https() => _app.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    [Theory]
    [InlineData("/")]
    [InlineData("/api/status")]
    public async Task Responses_carry_security_headers(string path)
    {
        var res = await Https().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'self';", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("same-origin", res.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
        Assert.False(res.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Api_responses_are_not_cached()
    {
        var res = await Https().GetAsync("/api/portfolio");
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Plain_http_is_redirected_to_https()
    {
        var http = _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
        var res = await http.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, res.StatusCode);
        Assert.Equal("https", res.Headers.Location!.Scheme);
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("rebind.attacker.test:7245")]
    public async Task Other_host_names_are_refused(string host)
    {
        // A DNS-rebinding page reaches 127.0.0.1 but still sends its own host name.
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/portfolio");
        req.Headers.Host = host;
        var res = await Https().SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")] // e.g. another app on a different localhost port
    public async Task Api_calls_from_other_sites_are_refused(string fetchSite)
    {
        var get = new HttpRequestMessage(HttpMethod.Get, "/api/portfolio");
        get.Headers.Add("Sec-Fetch-Site", fetchSite);
        Assert.Equal(HttpStatusCode.Forbidden, (await Https().SendAsync(get)).StatusCode);

        var post = new HttpRequestMessage(HttpMethod.Post, "/api/portfolio/reset") { Content = JsonContent.Create(new { startingCash = 1 }) };
        post.Headers.Add("Sec-Fetch-Site", fetchSite);
        Assert.Equal(HttpStatusCode.Forbidden, (await Https().SendAsync(post)).StatusCode);
    }

    [Fact]
    public async Task Writes_with_a_foreign_origin_are_refused_even_without_fetch_metadata()
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/portfolios") { Content = JsonContent.Create(new { name = "Evil", startingCash = 1 }) };
        post.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await Https().SendAsync(post)).StatusCode);
    }

    [Fact]
    public async Task Folios_own_page_and_direct_tools_are_allowed()
    {
        var page = new HttpRequestMessage(HttpMethod.Get, "/api/portfolio");
        page.Headers.Add("Sec-Fetch-Site", "same-origin");
        Assert.Equal(HttpStatusCode.OK, (await Https().SendAsync(page)).StatusCode);

        var sameOriginWrite = new HttpRequestMessage(HttpMethod.Patch, "/api/portfolios/1") { Content = JsonContent.Create(new { name = "My portfolio" }) };
        sameOriginWrite.Headers.Add("Sec-Fetch-Site", "same-origin");
        sameOriginWrite.Headers.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.NoContent, (await Https().SendAsync(sameOriginWrite)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Https().GetAsync("/api/status")).StatusCode); // curl-style, no headers
    }

    [Theory]
    [InlineData("AAPL'; DROP TABLE Trades;--")]
    [InlineData("1 OR 1=1")]
    [InlineData("<script>alert(1)</script>")]
    public async Task Injection_in_symbols_is_rejected_as_an_invalid_ticker(string symbol)
    {
        var res = await Https().PostAsJsonAsync("/api/watchlist", new { symbol });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("valid ticker", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sql_and_html_in_names_are_stored_as_plain_text()
    {
        var client = Https();
        var name = "x'); DROP TABLE Accounts;-- <img src=x onerror=alert(1)>"[..40];
        var created = await client.PostAsJsonAsync("/api/portfolios", new { name, startingCash = 1000 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType!.MediaType); // never rendered as HTML

        var list = await client.GetStringAsync("/api/portfolios");
        Assert.Contains("DROP TABLE Accounts", list);               // stored verbatim…
        Assert.Contains("My portfolio", list);                      // …and the table is still there
    }

    [Fact]
    public async Task Non_numeric_and_unknown_portfolio_ids_are_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Https().GetAsync("/api/portfolio?portfolio=1%20OR%201=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Https().GetAsync("/api/portfolio?portfolio=999")).StatusCode);
    }

    [Fact]
    public async Task Errors_do_not_leak_internals()
    {
        var res = await Https().PostAsync("/api/trades", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.DoesNotContain("   at ", body); // no stack trace
        Assert.DoesNotContain("Folio.", body);
    }
}

public class CrossSiteGuardTests
{
    private static Microsoft.AspNetCore.Http.HttpRequest Req(string method, string? site, string? origin)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new("localhost:7245");
        if (site is not null) ctx.Request.Headers["Sec-Fetch-Site"] = site;
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        return ctx.Request;
    }

    [Theory]
    [InlineData("GET", "same-origin", null, true)]
    [InlineData("GET", "none", null, true)]           // typed in the address bar / opened a bookmark
    [InlineData("GET", null, null, true)]             // curl, scripts
    [InlineData("GET", "cross-site", null, false)]
    [InlineData("GET", "same-site", null, false)]
    [InlineData("POST", "same-origin", "https://localhost:7245", true)]
    [InlineData("POST", null, "https://localhost:7245", true)]
    [InlineData("POST", null, "http://localhost:5231", false)] // different origin (scheme/port)
    [InlineData("POST", null, "https://evil.example", false)]
    [InlineData("DELETE", "cross-site", "https://evil.example", false)]
    [InlineData("POST", null, null, true)]
    public void Decides_by_fetch_metadata_and_origin(string method, string? site, string? origin, bool allowed) =>
        Assert.Equal(allowed, Folio.Services.Security.IsAllowed(Req(method, site, origin)));
}
