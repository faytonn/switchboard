using System.Globalization;
using System.Security.Cryptography;

namespace Switchboard.Core.Security;

/// <summary>
/// Password hashing for the owner's web login, with PBKDF2-SHA256 from .NET itself.
///
/// Unlike API tokens (256 random bits, so plain SHA-256 is enough), people pick passwords that
/// can be guessed. A slow hash makes every guess expensive: 600,000 iterations (OWASP's
/// 2023 advice for PBKDF2-SHA256) take ~0.3s per try. The random salt makes the same password
/// hash differently every time, so precomputed tables don't help.
///
/// Format: "pbkdf2-sha256:&lt;iterations&gt;:&lt;salt base64&gt;:&lt;hash base64&gt;". Colons, not "$",
/// because the hash lives in .env, which bash and Docker Compose both read.
/// </summary>
public static class PasswordHash
{
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
