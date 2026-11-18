using System.Security.Cryptography;
using System.Text;

namespace SafePassage.Api.Services;

public sealed class ApplicationCredentialProtector(IConfiguration configuration)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(GetKey(), TagSize))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
    }

    public string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var payload = Convert.FromBase64String(value);
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("The stored application credential is invalid.");

        var plaintext = new byte[payload.Length - NonceSize - TagSize];
        using (var aes = new AesGcm(GetKey(), TagSize))
            aes.Decrypt(payload.AsSpan(0, NonceSize), payload.AsSpan(NonceSize + TagSize), payload.AsSpan(NonceSize, TagSize), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private byte[] GetKey()
    {
        var keyMaterial = configuration["ApplicationCredentials:EncryptionKey"]
            ?? configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(keyMaterial))
            throw new InvalidOperationException("Configure ApplicationCredentials:EncryptionKey to protect saved application passwords.");
        return SHA256.HashData(Encoding.UTF8.GetBytes("SafePassage.ApplicationCredentials.v1:" + keyMaterial));
    }
}
