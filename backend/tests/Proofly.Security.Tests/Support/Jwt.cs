using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Proofly.Security.Tests.Support;

/// <summary>Hand-made hostile tokens.</summary>
public static class Jwt
{
    private static string B64(string s) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(s));

    /// <summary>"alg":"none" token with no signature.</summary>
    public static string AlgNone(string subject, string issuer, string audience) =>
        B64("""{"alg":"none","typ":"JWT"}""") + "." + B64($$"""{"sub":"{{subject}}","iss":"{{issuer}}","aud":"{{audience}}","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""") + ".";

    public static string Hmac(string subject, string issuer, string audience, byte[] key, string alg = SecurityAlgorithms.HmacSha256,
        DateTime? notBefore = null, DateTime? expires = null, string? kid = null, bool omitExpiry = false)
    {
        var k = new SymmetricSecurityKey(key) { KeyId = kid };
        var now = DateTime.UtcNow;
        if (omitExpiry)
        {
            // The descriptor overload stamps a default exp; build the payload by hand to really have none.
            var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["sub"] = subject, ["iss"] = issuer, ["aud"] = audience, ["nbf"] = new DateTimeOffset(now.AddMinutes(-1)).ToUnixTimeSeconds() });
            return new JsonWebTokenHandler().CreateToken(json, new SigningCredentials(k, alg));
        }
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Claims = new Dictionary<string, object> { ["sub"] = subject },
            NotBefore = notBefore ?? now.AddMinutes(-1),
            Expires = omitExpiry ? null : expires ?? now.AddMinutes(30),
            SigningCredentials = new SigningCredentials(k, alg),
        });
    }
}
