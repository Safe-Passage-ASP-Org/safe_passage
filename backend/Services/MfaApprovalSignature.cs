using System.Security.Cryptography;
using System.Text;

namespace SafePassage.Api.Services;

public static class MfaApprovalSignature
{
    public static string CreateMessage(string sessionToken, string deviceId) =>
        $"safe-passage:mfa-approval:v1\n{sessionToken}\n{deviceId}";

    public static bool IsValidPublicKey(string? publicKey)
    {
        if (!TryImportPublicKey(publicKey, out var verifier))
            return false;

        verifier.Dispose();
        return true;
    }

    public static bool Verify(string? publicKey, string sessionToken, string deviceId, string? signature)
    {
        if (!TryImportPublicKey(publicKey, out var verifier))
            return false;

        using (verifier)
        {
            try
            {
                var signatureBytes = Convert.FromBase64String(signature ?? string.Empty);
                if (signatureBytes.Length != 64)
                    return false;

                var payload = Encoding.UTF8.GetBytes(CreateMessage(sessionToken, deviceId));
                return verifier.VerifyData(
                    payload,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (FormatException)
            {
                return false;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    private static bool TryImportPublicKey(string? encodedPublicKey, out ECDsa verifier)
    {
        verifier = null!;
        try
        {
            var publicKey = Convert.FromBase64String(encodedPublicKey ?? string.Empty);
            if (publicKey.Length != 65 || publicKey[0] != 0x04)
                return false;

            verifier = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = publicKey[1..33],
                    Y = publicKey[33..65]
                }
            });
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}