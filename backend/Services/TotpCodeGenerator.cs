using System.Security.Cryptography;

namespace SafePassage.Api.Services;

public static class TotpCodeGenerator
{
    public static string Generate(string secret, DateTimeOffset now)
    {
        var key = DecodeBase32(secret);
        var counter = now.ToUnixTimeSeconds() / 30;
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        var hash = HMACSHA1.HashData(key, counterBytes);
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool IsValidSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return true;
        try { _ = DecodeBase32(secret); return true; }
        catch (FormatException) { return false; }
    }

    private static byte[] DecodeBase32(string secret)
    {
        var normalized = secret.Trim().Replace(" ", "", StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        if (normalized.Length < 16 || normalized.Any(character => !("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(character))))
            throw new FormatException("The 2FA secret must be a valid Base32 secret with at least 16 characters.");

        var output = new List<byte>(normalized.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var character in normalized)
        {
            buffer = (buffer << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(character);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        return output.ToArray();
    }
}
