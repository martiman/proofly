using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Proofly.Api.Auth;

/// <summary>A token whose signature, issuer, audience and lifetime were verified.</summary>
public sealed record ValidatedToken(string Subject, string? Name, string? Email, DateTimeOffset ExpiresAt);

public interface ITokenValidator
{
    /// <summary>Null when the token is invalid or expired. Throws only for infrastructure failures (e.g. IdP unreachable).</summary>
    Task<ValidatedToken?> ValidateAsync(string token, CancellationToken ct);
}

internal static class TokenClaims
{
    public static ValidatedToken? From(TokenValidationResult r)
    {
        if (!r.IsValid || r.SecurityToken is not JsonWebToken jwt) { return null; }
        var sub = jwt.Subject;
        if (string.IsNullOrWhiteSpace(sub)) { return null; }
        string? Claim(string t) => jwt.TryGetPayloadValue<string>(t, out var v) ? v : null;
        return new ValidatedToken(sub, Claim("name") ?? Claim("preferred_username"), Claim("email"), new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
    }
}

/// <summary>Validates tokens from the customer's OIDC provider using its discovery document and signing keys.</summary>
public sealed class OidcTokenValidator : ITokenValidator
{
    private readonly OidcOptions _o;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _config;
    private readonly JsonWebTokenHandler _handler = new();

    public OidcTokenValidator(IOptions<AuthOptions> options)
    {
        _o = options.Value.Oidc;
        var metadata = _o.MetadataAddress ?? (string.IsNullOrWhiteSpace(_o.Authority) ? null : _o.Authority.TrimEnd('/') + "/.well-known/openid-configuration");
        if (metadata is not null)
        {
            _config = new ConfigurationManager<OpenIdConnectConfiguration>(metadata, new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = _o.RequireHttpsMetadata });
        }
    }

    /// <summary>False when Auth:Oidc is not configured: every token is then rejected (deny by default).</summary>
    public bool IsConfigured => _config is not null && !string.IsNullOrWhiteSpace(_o.Audience);

    public async Task<ValidatedToken?> ValidateAsync(string token, CancellationToken ct)
    {
        if (!IsConfigured) { return null; }
        var result = await TryAsync(token, ct);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            _config!.RequestRefresh(); // key rotation: refetch once
            result = await TryAsync(token, ct);
        }
        return TokenClaims.From(result);
    }

    private async Task<TokenValidationResult> TryAsync(string token, CancellationToken ct)
    {
        var cfg = await _config!.GetConfigurationAsync(ct);
        var p = new TokenValidationParameters
        {
            ValidIssuer = string.IsNullOrWhiteSpace(_o.Issuer) ? cfg.Issuer : _o.Issuer,
            ValidAudience = _o.Audience,
            IssuerSigningKeys = cfg.SigningKeys,
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
            RequireExpirationTime = true, RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(_o.ClockSkewSeconds),
        };
        return await _handler.ValidateTokenAsync(token, p);
    }
}

/// <summary>Issues and validates HS256 tokens for development, evaluation and tests. Never used in Production.</summary>
public sealed class LocalTokenService : ITokenValidator
{
    public const string Issuer = "proofly-local";
    public const string Audience = "proofly-api";

    private readonly SymmetricSecurityKey _key;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly TimeProvider _time;

    public LocalTokenService(IOptions<AuthOptions> options, TimeProvider time)
    {
        _time = time;
        var k = options.Value.Local.SigningKey;
        _key = new SymmetricSecurityKey(string.IsNullOrEmpty(k) ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(k));
    }

    public string Issue(string subject, TimeSpan? lifetime = null, string? name = null, string? email = null, DateTimeOffset? issuedAt = null)
    {
        var iat = issuedAt ?? _time.GetUtcNow();
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (name is not null) { claims["name"] = name; }
        if (email is not null) { claims["email"] = email; }
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience,
            Claims = claims,
            IssuedAt = iat.UtcDateTime,
            NotBefore = iat.UtcDateTime,
            Expires = iat.Add(lifetime ?? TimeSpan.FromHours(8)).UtcDateTime,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });
    }

    public async Task<ValidatedToken?> ValidateAsync(string token, CancellationToken ct)
    {
        var r = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = _key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
            RequireExpirationTime = true, RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(5),
            LifetimeValidator = (nbf, exp, _, _) => exp is not null && exp > _time.GetUtcNow().UtcDateTime && (nbf is null || nbf <= _time.GetUtcNow().UtcDateTime.AddSeconds(5)),
        });
        return TokenClaims.From(r);
    }
}
