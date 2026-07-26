using System.Security.Cryptography;
using System.Text;

namespace GameHub.Core.Services;

/// <summary>Creates the only durable representation of an OIDC provider session identifier.</summary>
internal static class OidcProviderSessionKey
{
    /// <summary>
    /// Hashes the exact issuer and opaque <c>sid</c> with an unambiguous length prefix. The issuer
    /// binding is required because the OIDC session identifier is unique only within one issuer.
    /// </summary>
    public static string Hash(string issuer, string providerSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerSessionId);

        var issuerBytes = Encoding.UTF8.GetBytes(issuer);
        var sessionBytes = Encoding.UTF8.GetBytes(providerSessionId);
        var input = new byte[sizeof(int) + issuerBytes.Length + sessionBytes.Length];
        BitConverter.TryWriteBytes(input.AsSpan(0, sizeof(int)), issuerBytes.Length);
        issuerBytes.CopyTo(input.AsSpan(sizeof(int)));
        sessionBytes.CopyTo(input.AsSpan(sizeof(int) + issuerBytes.Length));
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
