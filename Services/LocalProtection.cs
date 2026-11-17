using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Folio.Services;

/// <summary>
/// Encrypts small secrets (the Finnhub key, the backup passphrase) with Windows data protection (DPAPI,
/// current user): only your Windows account on this PC can decrypt them. Not available on other systems.
/// </summary>
public static class LocalProtection
{
    // Extra entropy so another app using DPAPI for the same user can't trivially unwrap Folio's secrets.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Folio.LocalProtection.v1");

    public static bool IsAvailable => OperatingSystem.IsWindows();

    public static string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows data protection is only available on Windows.");
        return Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>The secret, or null if it can't be decrypted (another user/PC, or damaged).</summary>
    public static string? Unprotect(string protectedBase64)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(UnprotectBytes(Convert.FromBase64String(protectedBase64))); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectBytes(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectBytes(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
