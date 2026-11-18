using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Folio.Services;

public record FinnhubKeyRequest(string? ApiKey);

/// <summary>The key entered on the setup screen was refused; the message says why. Returned as 400.</summary>
public class SetupRejectedException(string message) : Exception(message);

/// <summary>
/// Where a key saved from the page goes: the user-secrets file (<see cref="SecretsPath"/>, null to keep it in memory
/// only, as in tests), unless the key comes from the <c>Finnhub__ApiKey</c> environment variable, which would win
/// over anything saved at the next start.
/// </summary>
public sealed record FinnhubKeyStorage(string? SecretsPath, bool FromEnvironment)
{
    public const string EnvironmentVariable = "Finnhub__ApiKey";

    public static bool EnvironmentHasKey() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));
}

/// <summary>
/// The Finnhub key entered on the page's setup screen. It's checked with Finnhub first, so a typo or a revoked key
/// is refused rather than saved. Then it's saved where <c>dotnet user-secrets</c> keeps it (encrypted with Windows
/// data protection on Windows) and put to use straight away: quotes, trading and the live stream start without a
/// restart.
/// </summary>
public sealed partial class FinnhubKeySetup(IOptions<FinnhubOptions> options, FinnhubKeyStorage storage, ILogger<FinnhubKeySetup> log)
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _firstKey = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource _changed = new();

    // Finnhub keys are about 20 letters and digits; this allows some room without accepting arbitrary text.
    [GeneratedRegex("^[A-Za-z0-9_-]{8,128}$")]
    private static partial Regex KeyPattern();

    public bool IsSet => !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    /// <summary>Completes once there's a key: straight away if there is one already.</summary>
    public Task WhenSetAsync(CancellationToken ct)
    {
        lock (_gate) return IsSet ? Task.CompletedTask : _firstKey.Task.WaitAsync(ct);
    }

    /// <summary>Cancelled when a new key is saved, so the live stream reconnects with it.</summary>
    public CancellationToken KeyChanged
    {
        get { lock (_gate) return _changed.Token; }
    }

    /// <summary>Checks <paramref name="key"/> with Finnhub, saves it and starts using it.</summary>
    public async Task SaveAsync(string? key, FinnhubClient finnhub, CancellationToken ct)
    {
        key = key?.Trim() ?? "";
        if (!KeyPattern().IsMatch(key))
            throw new SetupRejectedException("That doesn't look like a Finnhub key. Copy it from your Finnhub dashboard: letters and digits, no spaces.");
        if (storage.FromEnvironment)
            throw new SetupRejectedException($"Your Finnhub key is set by the {FinnhubKeyStorage.EnvironmentVariable} environment variable, which takes priority. Change it there, then restart Folio.");

        var (check, problem) = await finnhub.CheckKeyAsync(key, ct);
        if (check == FinnhubClient.KeyCheck.Rejected)
            throw new SetupRejectedException("Finnhub didn't accept that key. Check you copied all of it from your Finnhub dashboard.");
        if (check == FinnhubClient.KeyCheck.Unreachable)
            throw new SetupRejectedException($"Couldn't check the key with Finnhub ({problem}) Check your internet connection and try again.");

        if (storage.SecretsPath is { } path) ApiKeyStore.Save(path, key);

        CancellationTokenSource previous;
        lock (_gate)
        {
            // IOptions hands every service this same instance, so they all see the new key from here on.
            options.Value.ApiKey = key;
            _firstKey.TrySetResult();
            previous = _changed;
            _changed = new CancellationTokenSource();
        }
        previous.Cancel();
        previous.Dispose();

        log.LogInformation(storage.SecretsPath is null
            ? "Finnhub key set from the setup screen (kept in memory only)"
            : LocalProtection.IsAvailable
                ? "Finnhub key saved from the setup screen, encrypted with Windows data protection"
                : "Finnhub key saved from the setup screen to your user secrets");
    }
}
