using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Folio.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Folio.Tests;

/// <summary>Passphrase encryption of off-PC backup copies.</summary>
public class BackupEncryptionTests
{
    private const int FastRounds = 100_000; // the minimum Decrypt accepts; keeps tests quick
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("SQLite format 3\0 pretend this is a database");

    [Fact]
    public void Round_trips_with_the_right_passphrase()
    {
        var enc = BackupEncryption.Encrypt(Data, "correct horse battery", FastRounds);
        Assert.Equal(Data, BackupEncryption.Decrypt(enc, "correct horse battery"));
        Assert.DoesNotContain("SQLite format", Encoding.UTF8.GetString(enc)); // nothing readable left
    }

    [Fact]
    public void Each_encryption_is_different()
    {
        var a = BackupEncryption.Encrypt(Data, "correct horse battery", FastRounds);
        var b = BackupEncryption.Encrypt(Data, "correct horse battery", FastRounds);
        Assert.NotEqual(a, b); // random salt and nonce every time
    }

    [Fact]
    public void Wrong_passphrase_is_detected()
    {
        var enc = BackupEncryption.Encrypt(Data, "correct horse battery", FastRounds);
        var ex = Assert.Throws<CryptographicException>(() => BackupEncryption.Decrypt(enc, "wrong horse battery"));
        Assert.Contains("Wrong passphrase", ex.Message);
    }

    [Theory]
    [InlineData(9)]    // inside the authenticated header (rounds)
    [InlineData(20)]   // salt
    [InlineData(-1)]   // last byte of the ciphertext
    public void Any_tampering_is_detected(int position)
    {
        var enc = BackupEncryption.Encrypt(Data, "correct horse battery", FastRounds);
        var i = position < 0 ? enc.Length + position : position;
        enc[i] ^= 0x01;
        Assert.Throws<CryptographicException>(() => BackupEncryption.Decrypt(enc, "correct horse battery"));
    }

    [Fact]
    public void Non_folio_files_are_refused() =>
        Assert.Throws<CryptographicException>(() => BackupEncryption.Decrypt(Data, "correct horse battery"));

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void Short_passphrases_are_refused(string passphrase) =>
        Assert.Throws<TradeRejectedException>(() => BackupEncryption.ValidatePassphrase(passphrase));

    [Fact]
    public void Decrypt_file_writes_a_normal_backup()
    {
        var dir = Directory.CreateTempSubdirectory("folio-dec-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "b.db.enc"), BackupEncryption.Encrypt(Data, "correct horse battery", FastRounds));
            BackupService.DecryptFile(Path.Combine(dir, "b.db.enc"), Path.Combine(dir, "b.db"), "correct horse battery");
            Assert.Equal(Data, File.ReadAllBytes(Path.Combine(dir, "b.db")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>Encrypted off-PC copies (Windows only: the passphrase is stored with Windows data protection).</summary>
public sealed class EncryptedCopyTests : IDisposable
{
    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Folio";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private readonly string _root = Directory.CreateTempSubdirectory("folio-enc-").FullName;
    private readonly TestApp _app = new();
    private string Cloud => Path.Combine(_root, "OneDrive");

    public void Dispose()
    {
        _app.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private BackupService Backups(TestApp.Scope s) => new(s.Db, Options.Create(new FolioOptions { CopyBackupsTo = Cloud }),
        new Env(_root), _app.Time, NullLogger<BackupService>.Instance);

    [Fact]
    public async Task Setting_a_passphrase_replaces_plain_copies_with_encrypted_ones()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var s = _app.NewScope();
        var backups = Backups(s);
        await backups.CreateAsync(); // plain copy, as before encryption
        Assert.Single(Directory.GetFiles(Cloud, "*.db"));

        var view = await backups.SetPassphraseAsync("correct horse battery staple");

        Assert.True(view.Encrypted);
        Assert.Empty(Directory.GetFiles(Cloud, "*.db"));
        var enc = Assert.Single(Directory.GetFiles(Cloud, "*.db.enc"));
        var plain = BackupEncryption.Decrypt(File.ReadAllBytes(enc), "correct horse battery staple");
        Assert.StartsWith("SQLite format 3", Encoding.ASCII.GetString(plain, 0, 15));
        Assert.False(File.Exists(Path.Combine(Cloud, "offpc-passphrase.dpapi"))); // the passphrase never goes to the cloud folder
    }

    [Fact]
    public async Task New_backups_are_encrypted_and_pruned_in_the_copy_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var s = _app.NewScope();
        var backups = new BackupService(s.Db, Options.Create(new FolioOptions { CopyBackupsTo = Cloud, BackupKeep = 2 }),
            new Env(_root), _app.Time, NullLogger<BackupService>.Instance);
        await backups.SetPassphraseAsync("correct horse battery staple");
        for (var i = 0; i < 3; i++) { await backups.CreateAsync(); _app.Time.Now = _app.Time.Now.AddMinutes(1); }

        Assert.Equal(2, Directory.GetFiles(Cloud, "*.db.enc").Length);
        Assert.Empty(Directory.GetFiles(Cloud, "*.db"));
    }

    [Fact]
    public async Task An_unreadable_passphrase_never_falls_back_to_a_plain_copy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var s = _app.NewScope();
        var backups = Backups(s);
        Directory.CreateDirectory(backups.Directory);
        File.WriteAllText(Path.Combine(backups.Directory, "offpc-passphrase.dpapi"), "bm90IGEgZHBhcGkgYmxvYg=="); // e.g. copied from another PC

        await backups.CreateAsync();

        Assert.False(Directory.Exists(Cloud) && Directory.GetFiles(Cloud, "folio-*").Length > 0);
        Assert.Contains("passphrase", backups.List().CopyError);
    }
}

/// <summary>The Finnhub key is kept encrypted in user secrets (Windows only).</summary>
public sealed class ApiKeyStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"folio-secrets-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_file); } catch (IOException) { } }

    [Theory]
    [InlineData("""{ "Finnhub:ApiKey": "abc123secretkey", "Other": "keep" }""")]
    [InlineData("""{ "Finnhub": { "ApiKey": "abc123secretkey" }, "Other": "keep" }""")]
    public void Plain_key_is_replaced_with_an_encrypted_one(string json)
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(_file, json);

        Assert.NotNull(ApiKeyStore.MigrateFile(_file));

        var text = File.ReadAllText(_file);
        Assert.DoesNotContain("abc123secretkey", text);
        Assert.Contains(ApiKeyStore.ProtectedKey, text);
        Assert.Contains("keep", text); // other secrets untouched
        Assert.Null(ApiKeyStore.MigrateFile(_file)); // nothing left to do
    }

    [Fact]
    public void Encrypted_key_is_loaded_into_memory()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(_file, """{ "Finnhub:ApiKey": "abc123secretkey" }""");
        ApiKeyStore.MigrateFile(_file);

        var config = new ConfigurationBuilder().AddJsonFile(_file).Build(); // as on the next start
        Assert.Null(config[ApiKeyStore.PlainKey]);
        ApiKeyStore.ProtectAndLoad(config, _file);
        Assert.Equal("abc123secretkey", config[ApiKeyStore.PlainKey]);
    }

    [Fact]
    public void A_key_from_another_account_is_reported_not_crashed_on()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(_file, $$"""{ "{{ApiKeyStore.ProtectedKey}}": "bm90IGEgZHBhcGkgYmxvYg==" }""");
        var config = new ConfigurationBuilder().AddJsonFile(_file).Build();
        var note = ApiKeyStore.ProtectAndLoad(config, _file);
        Assert.Contains("couldn't be decrypted", note);
        Assert.Null(config[ApiKeyStore.PlainKey]);
    }
}

/// <summary>Hosts the app with the access key required.</summary>
public sealed class LockedFolioFactory : FolioFactory
{
    public string KeyFile { get; } = Path.Combine(Path.GetTempPath(), $"folio-key-{Guid.NewGuid():N}.key");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Folio:RequireAccessKey", "true");
        builder.UseSetting("Folio:AccessKeyFile", KeyFile);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(KeyFile); } catch (IOException) { }
    }
}

public class AccessKeyTests : IClassFixture<LockedFolioFactory>
{
    private readonly LockedFolioFactory _app;
    public AccessKeyTests(LockedFolioFactory app) => _app = app;

    private HttpClient Client() => _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });

    private string Key()
    {
        _ = Client(); // starts the host, which creates the key
        return File.ReadAllText(_app.KeyFile).Trim();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/api/portfolio")]
    [InlineData("/api/status")]
    public async Task Everything_is_locked_without_the_key(string path)
    {
        var res = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.DoesNotContain("Total value", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Locked_page_explains_how_to_get_in()
    {
        var res = await Client().GetAsync("/");
        Assert.Contains("Folio is locked", await res.Content.ReadAsStringAsync());
        Assert.Equal("text/html", res.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Wrong_key_is_refused()
    {
        var res = await Client().GetAsync("/unlock?key=" + new string('x', 43));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.False(res.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Right_key_sets_a_secure_cookie_and_redirects_without_the_key()
    {
        var res = await Client().GetAsync("/unlock?key=" + Uri.EscapeDataString(Key()));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location!.OriginalString);

        var cookie = res.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(AccessKey.CookieName + "=", cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key(), cookie); // the cookie isn't the key itself

        var value = cookie.Split(';')[0];
        var page = new HttpRequestMessage(HttpMethod.Get, "/api/portfolio");
        page.Headers.Add("Cookie", value);
        Assert.Equal(HttpStatusCode.OK, (await Client().SendAsync(page)).StatusCode);
    }

    [Fact]
    public async Task A_forged_cookie_is_refused()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/portfolio");
        req.Headers.Add("Cookie", $"{AccessKey.CookieName}={new string('A', 64)}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Scripts_can_use_the_key_header()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        req.Headers.Add(AccessKey.HeaderName, Key());
        Assert.Equal(HttpStatusCode.OK, (await Client().SendAsync(req)).StatusCode);
    }

    [Fact]
    public void The_key_is_long_and_random()
    {
        var key = Key();
        Assert.True(key.Length >= 43, "256-bit key");
        Assert.Matches("^[A-Za-z0-9_-]+$", key);
    }
}

/// <summary>Logos are fetched by Folio from Finnhub only, and only if they're real images.</summary>
public sealed class LogoServiceTests : IDisposable
{
    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Folio";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeLogos : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        public List<Uri> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly string _root = Directory.CreateTempSubdirectory("folio-logos-").FullName;
    private readonly TestApp _app = new();
    private readonly FakeLogos _http = new();

    public void Dispose()
    {
        _app.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<LogoService> Service(string? logoUrl)
    {
        using (var s = _app.NewScope())
        {
            s.Db.Companies.Add(new Folio.Data.Company { Symbol = "AAPL", Name = "Apple", Logo = logoUrl });
            await s.Db.SaveChangesAsync();
        }
        var scope = _app.NewScope();
        return new LogoService(scope.Db, new Factory(_http), new Env(_root), Options.Create(new FolioOptions()), NullLogger<LogoService>.Instance);
    }

    [Theory]
    [InlineData("https://static2.finnhub.io/logo/AAPL.png", true)]
    [InlineData("https://finnhub.io/logo.png", true)]
    [InlineData("http://static2.finnhub.io/logo.png", false)]         // not https
    [InlineData("https://evilfinnhub.io/logo.png", false)]            // look-alike domain
    [InlineData("https://finnhub.io.evil.example/logo.png", false)]
    [InlineData("https://static2.finnhub.io:8443/logo.png", false)]   // odd port
    [InlineData("https://169.254.169.254/latest/meta-data", false)]   // internal address
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData(null, false)]
    public void Only_finnhub_https_sources_are_allowed(string? url, bool allowed) =>
        Assert.Equal(allowed, LogoService.IsAllowedSource(url));

    [Fact]
    public void Only_real_raster_images_are_recognised()
    {
        Assert.Equal("image/png", LogoService.SniffImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal("image/jpeg", LogoService.SniffImage([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/gif", LogoService.SniffImage("GIF89a"u8));
        Assert.Null(LogoService.SniffImage("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8));
        Assert.Null(LogoService.SniffImage("<html>"u8));
    }

    [Fact]
    public async Task Downloads_once_then_serves_from_cache()
    {
        var logos = await Service("https://static2.finnhub.io/logo/AAPL.png");
        Assert.Equal("image/png", (await logos.GetAsync("AAPL"))?.ContentType);
        Assert.Equal("image/png", (await logos.GetAsync("aapl"))?.ContentType);
        Assert.Single(_http.Requested);
        Assert.Single(Directory.GetFiles(logos.Directory));
    }

    [Fact]
    public async Task Svg_or_html_responses_are_refused_and_not_cached()
    {
        _http.Body = "<svg onload=\"alert(1)\"></svg>"u8.ToArray();
        var logos = await Service("https://static2.finnhub.io/logo/AAPL.svg");
        Assert.Null(await logos.GetAsync("AAPL"));
        Assert.False(Directory.Exists(logos.Directory) && Directory.GetFiles(logos.Directory).Length > 0);
    }

    [Fact]
    public async Task Oversized_logos_are_refused()
    {
        _http.Body = [.. new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, .. new byte[LogoService.MaxBytes]];
        var logos = await Service("https://static2.finnhub.io/logo/AAPL.png");
        Assert.Null(await logos.GetAsync("AAPL"));
    }

    [Fact]
    public async Task Disallowed_sources_are_never_requested()
    {
        var logos = await Service("https://evil.example/track.png");
        Assert.Null(await logos.GetAsync("AAPL"));
        Assert.Empty(_http.Requested);
    }

    [Fact]
    public async Task Path_tricks_in_the_symbol_are_rejected()
    {
        var logos = await Service("https://static2.finnhub.io/logo/AAPL.png");
        await Assert.ThrowsAsync<TradeRejectedException>(() => logos.GetAsync("..\\..\\Windows"));
    }
}

/// <summary>Hosts the app as the installed copy runs: plain http://localhost, access key required.</summary>
public sealed class PlainHttpFolioFactory : FolioFactory
{
    public string KeyFile { get; } = Path.Combine(Path.GetTempPath(), $"folio-key-{Guid.NewGuid():N}.key");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Folio:Url", "http://localhost:7245");
        builder.UseSetting("Folio:RequireAccessKey", "true");
        builder.UseSetting("Folio:AccessKeyFile", KeyFile);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(KeyFile); } catch (IOException) { }
    }
}

/// <summary>
/// The installed copy on plain http://localhost: no redirect to HTTPS, no "upgrade-insecure-requests" (it would break
/// the page), and an access cookie browsers accept over HTTP, with every other protection kept.
/// </summary>
public class PlainHttpTests : IClassFixture<PlainHttpFolioFactory>
{
    private readonly PlainHttpFolioFactory _app;
    public PlainHttpTests(PlainHttpFolioFactory app) => _app = app;

    private HttpClient Http() => _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:7245"), AllowAutoRedirect = false, HandleCookies = false });

    private string Key()
    {
        _ = Http(); // starts the host, which creates the key
        return File.ReadAllText(_app.KeyFile).Trim();
    }

    private HttpRequestMessage WithKey(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(AccessKey.HeaderName, Key());
        return req;
    }

    [Fact]
    public async Task Pages_are_served_over_http_without_a_redirect()
    {
        var res = await Http().SendAsync(WithKey(HttpMethod.Get, "/api/status"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task The_policy_doesnt_upgrade_requests_to_https_but_keeps_everything_else()
    {
        var res = await Http().SendAsync(WithKey(HttpMethod.Get, "/"));
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.DoesNotContain("upgrade-insecure-requests", csp);
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'self';", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Still_locked_without_the_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http().GetAsync("/api/portfolio")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http().GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Unlocking_sets_a_cookie_browsers_accept_over_http()
    {
        var res = await Http().GetAsync("/unlock?key=" + Uri.EscapeDataString(Key()));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var cookie = res.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(AccessKey.PlainCookieName + "=", cookie);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase); // a browser may drop a Secure cookie over http
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key(), cookie);

        var page = new HttpRequestMessage(HttpMethod.Get, "/api/portfolio");
        page.Headers.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await Http().SendAsync(page)).StatusCode);
    }

    [Fact]
    public async Task A_forged_cookie_is_refused()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/portfolio");
        req.Headers.Add("Cookie", $"{AccessKey.PlainCookieName}={new string('A', 64)}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http().SendAsync(req)).StatusCode);
    }

    [Theory]
    [InlineData("same-origin", "http://localhost:7245", HttpStatusCode.NoContent)]
    [InlineData("cross-site", "https://evil.example", HttpStatusCode.Forbidden)]
    [InlineData(null, "https://localhost:7245", HttpStatusCode.Forbidden)] // the https origin is a different site now
    public async Task Writes_are_only_accepted_from_folio_itself(string? site, string origin, HttpStatusCode expected)
    {
        var req = WithKey(HttpMethod.Patch, "/api/portfolios/1");
        req.Content = JsonContent.Create(new { name = "My portfolio" });
        if (site is not null) req.Headers.Add("Sec-Fetch-Site", site);
        req.Headers.Add("Origin", origin);
        Assert.Equal(expected, (await Http().SendAsync(req)).StatusCode);
    }
}

public class SiteUrlTests
{
    [Theory]
    [InlineData("http://0.0.0.0:7245")]
    [InlineData("https://example.com")]
    [InlineData("ftp://localhost:7245")]
    [InlineData("http://localhost:7245/folio")]
    [InlineData("not a url")]
    public void Folio_only_listens_on_localhost(string url)
    {
        using var factory = new UrlFactory(url);
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Folio:Url must be", ex.ToString());
    }

    private sealed class UrlFactory(string url) : FolioFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Folio:Url", url);
        }
    }
}
