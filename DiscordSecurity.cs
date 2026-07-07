using System;
using System.Text;
using NSec.Cryptography;

namespace WinkyBot.Functions;

public static class DiscordSecurity
{
    private static readonly string? PublicKeyHex = Environment.GetEnvironmentVariable("DISCORD_PUBLIC_KEY");

    public static bool VerifySignature(string signature, string timestamp, string body)
    {
        if (string.IsNullOrWhiteSpace(PublicKeyHex) || string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            var publicKeyBytes = Convert.FromHexString(PublicKeyHex);
            var signatureBytes = Convert.FromHexString(signature);
            var messageBytes = Encoding.UTF8.GetBytes(timestamp + body);

            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKeyBytes, KeyBlobFormat.RawPublicKey);

            return SignatureAlgorithm.Ed25519.Verify(publicKey, messageBytes, signatureBytes);
        }
        catch
        {
            return false;
        }
    }
}