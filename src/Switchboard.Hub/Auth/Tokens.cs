using System.Security.Cryptography;
using System.Text;

namespace Switchboard.Hub.Auth;

public static class Tokens
{
    /// <summary>A new random token: 32 bytes from the OS's secure random generator, base64url.</summary>
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// SHA-256 is enough here (no salt or slow hash like for passwords): the token is 256 random
    /// bits, so there is nothing to guess even with the hash in hand.
    /// </summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>
    /// Compares in constant time. A normal string compare stops at the first different
    /// character, and the time that takes can leak how much of a guess was right.
    /// </summary>
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
