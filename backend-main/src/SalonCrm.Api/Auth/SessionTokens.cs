using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace SalonCrm.Api.Auth;

public static class SessionTokens
{
    // 32 random bytes, base64url (no padding).
    public static string Generate() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    // SHA-256 of the cookie value, lowercase hex. Only this hash is stored in the DB.
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
