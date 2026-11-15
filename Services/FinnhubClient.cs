using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Folio.Services;

public class FinnhubOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://finnhub.io/api/v1/";
    /// <summary>How long a quote is served from cache. Keeps us well under the free 60 calls/min.</summary>
    public int QuoteCacheSeconds { get; set; } = 15;
    /// <summary>Stream trade prices over Finnhub's websocket (one connection, up to 50 symbols on the free tier).</summary>
    public bool Streaming { get; set; } = true;
    public string StreamUrl { get; set; } = "wss://ws.finnhub.io";
}

public record Quote(
    string Symbol,
    decimal Price,
    decimal Change,
    decimal ChangePercent,
    decimal PreviousClose,
    DateTimeOffset AsOf,
    bool Stale);

public record SymbolMatch(string Symbol, string Description, string Type);

public record CompanyProfile(string Symbol, string? Name, string? Industry, string? Logo);

public class FinnhubException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Thin Finnhub wrapper. Quotes are cached briefly; if Finnhub fails (rate limit, outage)
/// the last good quote is returned flagged <see cref="Quote.Stale"/>.
/// </summary>
public class FinnhubClient(HttpClient http, IMemoryCache cache, IOptions<FinnhubOptions> options, LivePrices live, ILogger<FinnhubClient> log)
{
    private static readonly ConcurrentDictionary<string, Quote> LastKnown = new(StringComparer.OrdinalIgnoreCase);
    private readonly FinnhubOptions _opt = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opt.ApiKey);

    /// <summary>
    /// Latest quote: the REST quote (cached briefly; previous close and fallback) with the newest streamed
    /// trade price on top when there is one.
    /// </summary>
    public async Task<Quote?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        var quote = await GetRestQuoteAsync(symbol.ToUpperInvariant(), ct);
        return quote is null ? null : live.Merge(quote);
    }

    private async Task<Quote?> GetRestQuoteAsync(string symbol, CancellationToken ct)
    {
        var key = $"quote:{symbol}";
        if (cache.TryGetValue(key, out Quote? cached)) return cached;

        try
        {
            var raw = await GetAsync<QuoteDto>($"quote?symbol={Uri.EscapeDataString(symbol)}", ct);
            // Finnhub answers unknown symbols with an all-zero payload rather than a 404.
            if (raw is null || (raw.Current == 0 && raw.Timestamp == 0)) return null;

            var quote = new Quote(
                symbol,
                raw.Current,
                raw.Change ?? 0,
                raw.ChangePercent ?? 0,
                raw.PreviousClose,
                DateTimeOffset.FromUnixTimeSeconds(raw.Timestamp),
                Stale: false);

            cache.Set(key, quote, TimeSpan.FromSeconds(_opt.QuoteCacheSeconds));
            LastKnown[symbol] = quote;
            return quote;
        }
        catch (FinnhubException ex) when (LastKnown.TryGetValue(symbol, out var last))
        {
            log.LogWarning("Quote for {Symbol} failed ({Message}); serving last known price", symbol, ex.Message);
            return last with { Stale = true };
        }
    }

    public async Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, CancellationToken ct = default)
    {
        var key = $"search:{query.ToUpperInvariant()}";
        if (cache.TryGetValue(key, out IReadOnlyList<SymbolMatch>? cached)) return cached!;

        var raw = await GetAsync<SearchDto>($"search?q={Uri.EscapeDataString(query)}&exchange=US", ct);
        IReadOnlyList<SymbolMatch> results = (raw?.Result ?? [])
            .Where(r => !string.IsNullOrEmpty(r.Symbol))
            .Take(10)
            .Select(r => new SymbolMatch(r.Symbol!, r.Description ?? "", r.Type ?? ""))
            .ToList();

        cache.Set(key, results, TimeSpan.FromMinutes(10));
        return results;
    }

    public async Task<CompanyProfile?> GetProfileAsync(string symbol, CancellationToken ct = default)
    {
        var raw = await GetAsync<ProfileDto>($"stock/profile2?symbol={Uri.EscapeDataString(symbol)}", ct);
        // ETFs and unknown tickers come back as {} on the free tier.
        if (raw is null || string.IsNullOrEmpty(raw.Name)) return null;
        return new CompanyProfile(symbol, raw.Name, NullIfEmpty(raw.Industry), SafeLogo(raw.Logo));
    }

    /// <summary>The page puts this straight into an img src, so only absolute https URLs are kept.</summary>
    public static string? SafeLogo(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.AbsoluteUri : null;

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new FinnhubException("Finnhub API key is not configured. Set Finnhub:ApiKey (see README).");

        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("X-Finnhub-Token", _opt.ApiKey);

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new FinnhubException($"Could not reach Finnhub: {ex.Message}");
        }

        using (res)
        {
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                throw new FinnhubException("Finnhub rate limit reached. Try again in a minute.", res.StatusCode);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new FinnhubException("Finnhub rejected the API key, or this endpoint isn't on your plan.", res.StatusCode);
            if (!res.IsSuccessStatusCode)
                throw new FinnhubException($"Finnhub returned {(int)res.StatusCode}.", res.StatusCode);

            return await res.Content.ReadFromJsonAsync<T>(ct);
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed class QuoteDto
    {
        [JsonPropertyName("c")] public decimal Current { get; set; }
        [JsonPropertyName("d")] public decimal? Change { get; set; }
        [JsonPropertyName("dp")] public decimal? ChangePercent { get; set; }
        [JsonPropertyName("pc")] public decimal PreviousClose { get; set; }
        [JsonPropertyName("t")] public long Timestamp { get; set; }
    }

    private sealed class SearchDto
    {
        [JsonPropertyName("result")] public List<SearchItem>? Result { get; set; }
    }

    private sealed class SearchItem
    {
        [JsonPropertyName("symbol")] public string? Symbol { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private sealed class ProfileDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("finnhubIndustry")] public string? Industry { get; set; }
        [JsonPropertyName("logo")] public string? Logo { get; set; }
    }
}
