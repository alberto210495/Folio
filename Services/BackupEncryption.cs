using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Folio.Services;

/// <summary>
/// Passphrase encryption for backup copies that leave this PC (e.g. OneDrive). AES-256-GCM, with the key derived
/// from your passphrase by PBKDF2-SHA256 (600,000 rounds, random salt per file). Tampering or a wrong passphrase is
/// detected rather than producing garbage.
/// File layout: "FOLIOENC" | version (1) | rounds (int32) | salt (16) | nonce (12) | tag (16) | ciphertext.
/// The header is authenticated too, so it can't be altered either.
/// </summary>
public static class BackupEncryption
{
    public const string Extension = ".enc";
    public const int MinPassphraseLength = 12;
    public const int MaxPassphraseLength = 256;
    public const int Rounds = 600_000;

    private static readonly byte[] Magic = "FOLIOENC"u8.ToArray();
    private const byte Version = 1;
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16;
    private static readonly int HeaderSize = Magic.Length + 1 + 4 + SaltSize;

    public static void ValidatePassphrase(string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < MinPassphraseLength)
            throw new TradeRejectedException($"Use a passphrase of at least {MinPassphraseLength} characters. A few unrelated words works well.");
        if (passphrase.Length > MaxPassphraseLength)
            throw new TradeRejectedException($"Passphrases can be up to {MaxPassphraseLength} characters.");
    }

    public static byte[] Encrypt(byte[] plain, string passphrase, int rounds = Rounds)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(Magic.Length + 1), rounds);
        salt.CopyTo(header, Magic.Length + 5);

        var key = DeriveKey(passphrase, salt, rounds);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag, associatedData: header);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        return [.. header, .. nonce, .. tag, .. cipher];
    }

    /// <summary>Decrypts, or throws <see cref="CryptographicException"/> for a wrong passphrase, tampering or a non-Folio file.</summary>
    public static byte[] Decrypt(byte[] data, string passphrase)
    {
        if (data.Length < HeaderSize + NonceSize + TagSize || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("This isn't an encrypted Folio backup.");
        if (data[Magic.Length] != Version)
            throw new CryptographicException($"Unsupported encrypted backup version {data[Magic.Length]}.");
        var rounds = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(Magic.Length + 1));
        if (rounds is < 100_000 or > 10_000_000) throw new CryptographicException("This encrypted backup's header is damaged.");

        var header = data.AsSpan(0, HeaderSize);
        var salt = header[(Magic.Length + 5)..].ToArray();
        var nonce = data.AsSpan(HeaderSize, NonceSize);
        var tag = data.AsSpan(HeaderSize + NonceSize, TagSize);
        var cipher = data.AsSpan(HeaderSize + NonceSize + TagSize);

        var key = DeriveKey(passphrase, salt, rounds);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, associatedData: header);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException("Wrong passphrase, or the file was changed or damaged.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return plain;
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)), salt, rounds, HashAlgorithmName.SHA256, 32);
}
