namespace Snacks.Services;

/// <summary>
///     Constant-time string comparison shared by the secret-bearing auth surfaces
///     (cluster shared secret, API keys).
/// </summary>
internal static class SecretCompare
{
    /// <summary>
    ///     Compares two strings in constant time using HMAC-SHA256 to prevent timing attacks.
    ///     Both inputs are hashed to a fixed-length 32-byte value before comparison,
    ///     eliminating length leakage.
    /// </summary>
    /// <param name="a">The expected secret.</param>
    /// <param name="b">The provided secret from the request.</param>
    /// <returns><see langword="true"/> if the strings are equal; otherwise <see langword="false"/>.</returns>
    public static bool ConstantTimeEquals(string a, string b)
    {
        // Hashing both values to a fixed length eliminates timing differences
        // that would otherwise reveal the length of the configured secret.
        var key   = System.Text.Encoding.UTF8.GetBytes("snacks-auth-compare");
        var hashA = System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(a));
        var hashB = System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(b));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }
}
