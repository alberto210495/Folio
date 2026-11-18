using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Folio.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Folio.Tests;

/// <summary>
/// The first-run setup screen's key: checked with Finnhub before it's saved, saved encrypted where
/// <c>dotnet user-secrets</c> keeps it, used straight away, and refused when it's malformed, wrong or unverifiable.
/// </summary>
public sealed class FinnhubKeySetupTests : IDisposable
{
    private const string GoodKey = "abc123GoodKey456def";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"folio-secrets-{Guid.NewGuid():N}");
    private readonly FakeFinnhub _finnhub = new() { AcceptedKey = GoodKey };
    private readonly IOptions<FinnhubOptions> _options = Options.Create(new FinnhubOptions { ApiKey = "" });

    public FinnhubKeySetupTests() => _finnhub.Prices["AAPL"] = 200m;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string SecretsPath => Path.Combine(_dir, "secrets.json");

    private FinnhubKeySetup Setup(bool fromEnvironment = false, bool persist = true) =>
        new(_options, new FinnhubKeyStorage(persist ? SecretsPath : null, fromEnvironment), NullLogger<FinnhubKeySetup>.Instance);

    // The client the endpoint gets: same shared options, so it sees a saved key straight away.
    private FinnhubClient Client() => new(
        new HttpClient(_finnhub) { BaseAddress = new Uri("https://finnhub.test/api/v1/") },
        new MemoryCache(new MemoryCacheOptions()), _options, new LivePrices(TimeProvider.System), NullLogger<FinnhubClient>.Instance);

    private JsonObject SecretsFile() => (JsonObject)JsonNode.Parse(File.ReadAllText(SecretsPath))!;

    [Fact]
    public async Task A_good_key_is_saved_and_used_straight_away()
    {
        var setup = Setup();
        var client = Client();
        Assert.False(setup.IsSet);
        Assert.False(client.IsConfigured);
        var waiting = setup.WhenSetAsync(default);
        var changed = setup.KeyChanged;
        Assert.False(waiting.IsCompleted);

        await setup.SaveAsync($"  {GoodKey}\n", client, default); // pasted with stray whitespace

        Assert.True(setup.IsSet);
        Assert.True(client.IsConfigured, "the same client now has the key, no restart");
        Assert.Equal(GoodKey, _options.Value.ApiKey);
        await waiting.WaitAsync(TimeSpan.FromSeconds(1)); // the live stream wakes up
        Assert.True(changed.IsCancellationRequested, "and reconnects with the new key");
        Assert.Equal(200m, (await client.GetQuoteAsync("AAPL"))!.Price);
    }

    [Fact]
    public async Task The_saved_key_is_encrypted_and_read_back_at_the_next_start()
    {
        await Setup().SaveAsync(GoodKey, Client(), default);

        var file = SecretsFile();
        if (!LocalProtection.IsAvailable)
        {
            // Elsewhere it's stored the way "dotnet user-secrets set" stores it.
            Assert.Equal(GoodKey, file[ApiKeyStore.PlainKey]!.GetValue<string>());
            return;
        }
        Assert.DoesNotContain(GoodKey, File.ReadAllText(SecretsPath));
        Assert.Null(file[ApiKeyStore.PlainKey]);
        Assert.Equal(GoodKey, LocalProtection.Unprotect(file[ApiKeyStore.ProtectedKey]!.GetValue<string>()));

        // What Program does at startup: decrypt the saved key into memory.
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        ApiKeyStore.ProtectAndLoad(config, SecretsPath);
        Assert.Equal(GoodKey, config[ApiKeyStore.PlainKey]);
    }

    [Fact]
    public void Saving_keeps_other_secrets_and_replaces_any_old_key()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SecretsPath, """{ "Finnhub:ApiKey": "oldPlainKey000", "Finnhub": { "ApiKey": "nested0000", "Streaming": false }, "Other": "kept" }""");

        ApiKeyStore.Save(SecretsPath, GoodKey);

        var file = SecretsFile();
        Assert.Equal("kept", file["Other"]!.GetValue<string>());
        Assert.False(file["Finnhub"]!["Streaming"]!.GetValue<bool>());
        Assert.Null(file["Finnhub"]!["ApiKey"]);
        Assert.DoesNotContain("oldPlainKey000", File.ReadAllText(SecretsPath));
        Assert.DoesNotContain("nested0000", File.ReadAllText(SecretsPath));
    }

    [Fact]
    public void A_damaged_secrets_file_is_left_alone()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SecretsPath, "{ not json");
        Assert.Throws<SetupRejectedException>(() => ApiKeyStore.Save(SecretsPath, GoodKey));
        Assert.Equal("{ not json", File.ReadAllText(SecretsPath));
    }

    [Fact]
    public async Task A_wrong_key_is_refused_and_nothing_is_saved()
    {
        var setup = Setup();
        var ex = await Assert.ThrowsAsync<SetupRejectedException>(() => setup.SaveAsync("wrongKey12345678", Client(), default));
        Assert.Contains("didn't accept", ex.Message);
        Assert.False(setup.IsSet);
        Assert.False(File.Exists(SecretsPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has spaces in it")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("dotnet user-secrets set Finnhub:ApiKey abc")]
    public async Task Something_that_isnt_a_key_is_refused_without_asking_Finnhub(string input)
    {
        var setup = Setup();
        var ex = await Assert.ThrowsAsync<SetupRejectedException>(() => setup.SaveAsync(input, Client(), default));
        Assert.Contains("doesn't look like a Finnhub key", ex.Message);
        Assert.Equal(0, _finnhub.Requests);
        Assert.False(File.Exists(SecretsPath));
    }

    [Theory]
    [InlineData(FakeFinnhub.Fault.Timeout)]
    [InlineData(FakeFinnhub.Fault.BadJson)]
    public async Task A_key_that_cant_be_checked_isnt_saved(FakeFinnhub.Fault fault)
    {
        _finnhub.QuoteFaults["AAPL"] = fault;
        var setup = Setup();
        var ex = await Assert.ThrowsAsync<SetupRejectedException>(() => setup.SaveAsync(GoodKey, Client(), default));
        Assert.Contains("Couldn't check the key", ex.Message);
        Assert.False(setup.IsSet);
        Assert.False(File.Exists(SecretsPath));
    }

    [Fact]
    public async Task A_rate_limited_key_counts_as_good()
    {
        _finnhub.Failing.Add("AAPL"); // 429 by default: Finnhub only says that to a key it accepted
        var setup = Setup();
        await setup.SaveAsync(GoodKey, Client(), default);
        Assert.True(setup.IsSet);
    }

    [Fact]
    public async Task An_environment_variable_key_isnt_overridden()
    {
        var setup = Setup(fromEnvironment: true);
        var ex = await Assert.ThrowsAsync<SetupRejectedException>(() => setup.SaveAsync(GoodKey, Client(), default));
        Assert.Contains(FinnhubKeyStorage.EnvironmentVariable, ex.Message);
        Assert.False(File.Exists(SecretsPath));
    }

    [Fact]
    public async Task Without_a_secrets_path_the_key_is_kept_in_memory_only()
    {
        var setup = Setup(persist: false);
        await setup.SaveAsync(GoodKey, Client(), default);
        Assert.True(setup.IsSet);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task Saving_again_replaces_the_key_and_reconnects_again()
    {
        var setup = Setup();
        await setup.SaveAsync(GoodKey, Client(), default);
        var changed = setup.KeyChanged;
        Assert.False(changed.IsCancellationRequested);

        _finnhub.AcceptedKey = "anotherGoodKey999";
        await setup.SaveAsync("anotherGoodKey999", Client(), default);

        Assert.Equal("anotherGoodKey999", _options.Value.ApiKey);
        Assert.True(changed.IsCancellationRequested);
    }
}

/// <summary>The setup endpoint in the real app: same protections as every write, and no key is ever saved by tests.</summary>
public class FinnhubKeyEndpointTests : IClassFixture<FolioFactory>
{
    private readonly FolioFactory _app;

    public FinnhubKeyEndpointTests(FolioFactory app) => _app = app;

    private HttpClient Https() => _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static HttpRequestMessage Post(object body, string site = "same-origin", string origin = "https://localhost")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/setup/finnhub-key") { Content = JsonContent.Create(body) };
        req.Headers.Add("Sec-Fetch-Site", site);
        req.Headers.Add("Origin", origin);
        return req;
    }

    [Fact]
    public async Task Status_says_there_is_no_key_yet()
    {
        var status = await Https().GetFromJsonAsync<JsonObject>("/api/status");
        Assert.False(status!["finnhubConfigured"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_malformed_key_gets_a_clear_400()
    {
        var res = await Https().SendAsync(Post(new { apiKey = "not a key" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("doesn't look like a Finnhub key", body!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("cross-site", "https://evil.example")]
    [InlineData("same-site", "https://other.localhost")]
    public async Task Other_websites_cant_set_your_key(string site, string origin)
    {
        var res = await Https().SendAsync(Post(new { apiKey = "attackerKey12345" }, site, origin));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
