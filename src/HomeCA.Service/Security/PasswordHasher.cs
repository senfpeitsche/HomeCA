using System.Security.Cryptography;

namespace HomeCA.Service.Security;

public static class PasswordHasher
{
    private const int Iterations = 600_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, 32);
        return $"pbkdf2-sha512${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha512" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
