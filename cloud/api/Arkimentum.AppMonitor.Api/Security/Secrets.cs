using System.Security.Cryptography;
using System.Text;

namespace Arkimentum.AppMonitor.Api.Security;

/// <summary>
/// Everything secret-shaped in this API is a random 256-bit value that is shown once and stored only as its SHA-256.
/// Comparisons are fixed-time (<see cref="CryptographicOperations.FixedTimeEquals"/>) so a caller cannot learn a key
/// byte by byte from response timing.
/// </summary>
public static class Secrets
{
    /// <summary>
    /// Prefix on every newly issued organization enrollment key. It carries no meaning to the API - the hash is
    /// taken over whatever is presented - but it makes the value recognisable to secret scanners and to whoever
    /// finds it in a GPO, a ticket or a script. Keys issued before the prefix existed keep working.
    /// </summary>
    public const string EnrollmentKeyPrefix = "ek_live_";

    /// <summary>Base64url of 32 random bytes - 43 characters, no padding, safe in URLs, headers and the registry.</summary>
    public static string NewKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>A new organization enrollment key: <see cref="EnrollmentKeyPrefix"/> followed by <see cref="NewKey"/>.</summary>
    public static string NewEnrollmentKey() => EnrollmentKeyPrefix + NewKey();

    /// <summary>
    /// SHA-256 of the key exactly as it was presented. Nothing is stripped or normalised, so a key issued before
    /// the prefix was introduced verifies against its stored hash unchanged.
    /// </summary>
    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public static bool Verify(string presentedKey, byte[]? storedHash)
    {
        if (storedHash is not { Length: 32 }) return false;
        var presented = Hash(presentedKey);
        return CryptographicOperations.FixedTimeEquals(presented, storedHash);
    }

    /// <summary>Short, stable fingerprint of a configuration document; the tail of a ConfigVersion.</summary>
    public static string ShortHash(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
