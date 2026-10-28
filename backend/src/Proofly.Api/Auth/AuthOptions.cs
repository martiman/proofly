using Microsoft.Extensions.Options;

namespace Proofly.Api.Auth;

public static class AuthModes
{
    public const string Oidc = "Oidc";
    public const string Local = "Local";
}

/// <summary>Configuration section <c>Auth</c>. No secrets here: OIDC uses public metadata; the local key is evaluation-only.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Oidc (default) or Local (development/evaluation only; refused in Production).</summary>
    public string Mode { get; set; } = AuthModes.Oidc;
    public OidcOptions Oidc { get; set; } = new();
    public LocalOptions Local { get; set; } = new();
}

public sealed class OidcOptions
{
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    /// <summary>Expected issuer; defaults to the issuer in the discovery document.</summary>
    public string? Issuer { get; set; }
    public string? MetadataAddress { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
    public int ClockSkewSeconds { get; set; } = 30;
    /// <summary>Create a role-less local user on first login. Off by default; needs an <c>IAuthAuditHook</c>.</summary>
    public bool ProvisionOnFirstLogin { get; set; }
}

public sealed class LocalOptions
{
    /// <summary>At least 32 characters. When empty a random per-process key is used (tokens die with the process).</summary>
    public string? SigningKey { get; set; }
    public bool LogTokens { get; set; }
}

internal sealed class AuthOptionsValidator(Microsoft.Extensions.Hosting.IHostEnvironment env) : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions o)
    {
        if (o.Mode is not (AuthModes.Oidc or AuthModes.Local))
        {
            return ValidateOptionsResult.Fail($"Auth:Mode must be '{AuthModes.Oidc}' or '{AuthModes.Local}'.");
        }
        if (o.Mode == AuthModes.Local && env.IsProduction())
        {
            return ValidateOptionsResult.Fail("Auth:Mode=Local is for development and evaluation and is refused in the Production environment.");
        }
        if (o.Mode == AuthModes.Local && !string.IsNullOrEmpty(o.Local.SigningKey) && o.Local.SigningKey.Length < 32)
        {
            return ValidateOptionsResult.Fail("Auth:Local:SigningKey must be at least 32 characters.");
        }
        return ValidateOptionsResult.Success;
    }
}
