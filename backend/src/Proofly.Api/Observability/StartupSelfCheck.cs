using Npgsql;

namespace Proofly.Api.Observability;

/// <summary>
/// Pure configuration check that fails fast with every problem listed. Never repairs a value.
/// Production requires an explicit connection string and an existing evidence directory.
/// </summary>
public static class StartupSelfCheck
{
    public const string EvidenceKey = "Evidence:Directory";

    public static IReadOnlyList<string> Validate(IConfiguration configuration, bool isProduction, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Proofly")))
        {
            errors.Add("ConnectionStrings:Proofly is not configured.");
        }

        if (isProduction) { ProductionIsolation(configuration, errors); }

        EnvironmentLabel(configuration, isProduction, errors);

        var mode = configuration["Auth:Mode"];
        if (isProduction && string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Auth:Mode=Local is refused in the Production environment; configure Auth:Mode=Oidc.");
        }

        var evidence = configuration[EvidenceKey];
        if (string.IsNullOrWhiteSpace(evidence))
        {
            if (isProduction) { errors.Add($"{EvidenceKey} is not configured."); }
        }
        else if (!directoryExists(evidence))
        {
            errors.Add($"{EvidenceKey} '{evidence}' does not exist.");
        }

        var metrics = configuration["Observability:Metrics:Enabled"];
        if (metrics is not null && !bool.TryParse(metrics, out _))
        {
            errors.Add("Observability:Metrics:Enabled must be true or false.");
        }
        return errors;
    }

    public const string EnvironmentNameKey = "Environment:Name";
    public const string EnvironmentKindKey = "Environment:Kind";
    public static readonly IReadOnlyList<string> EnvironmentKinds = ["DEV", "TEST", "PILOT", "PRODUCTION"];

    /// <summary>The environment label shown in the UI. Required in Production (never guessed); optional elsewhere, but a given kind must be valid.</summary>
    private static void EnvironmentLabel(IConfiguration configuration, bool isProduction, List<string> errors)
    {
        var name = configuration[EnvironmentNameKey];
        var kind = configuration[EnvironmentKindKey];
        if (isProduction && string.IsNullOrWhiteSpace(name)) { errors.Add($"{EnvironmentNameKey} is not configured."); }
        if (isProduction && string.IsNullOrWhiteSpace(kind)) { errors.Add($"{EnvironmentKindKey} is not configured (one of {string.Join(", ", EnvironmentKinds)})."); }
        else if (!string.IsNullOrWhiteSpace(kind) && !EnvironmentKinds.Contains(kind, StringComparer.Ordinal))
        {
            errors.Add($"{EnvironmentKindKey} must be one of {string.Join(", ", EnvironmentKinds)}.");
        }
    }

    /// <summary>
    /// Development and production stores and credentials are isolated (docs/security/data-handling.md): Production refuses the
    /// development database names, a loopback database without any credential (the trust-auth development setup), leftover local
    /// signing key / token logging, and wildcard or development web origins. Messages never echo configuration values.
    /// </summary>
    private static void ProductionIsolation(IConfiguration configuration, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("Proofly")))
        {
            try
            {
                var b = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Proofly"));
                var db = b.Database ?? "";
                if (db.EndsWith("_dev", StringComparison.OrdinalIgnoreCase) || db.EndsWith("_test", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("ConnectionStrings:Proofly names a development or test database; Production must use its own isolated store.");
                }
                var loopback = (b.Host ?? "").Split(',').Select(h => h.Trim()).Any(h => h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || (System.Net.IPAddress.TryParse(h, out var ip) && System.Net.IPAddress.IsLoopback(ip)));
                var credential = !string.IsNullOrEmpty(b.Password) || !string.IsNullOrEmpty(b.Passfile) || !string.IsNullOrEmpty(b.SslCertificate);
                if (loopback && !credential)
                {
                    errors.Add("ConnectionStrings:Proofly is a loopback database without any credential (development trust authentication); Production requires a credential.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
            {
                errors.Add("ConnectionStrings:Proofly is not a valid Npgsql connection string.");
            }
        }

        if (!string.IsNullOrEmpty(configuration["Auth:Local:SigningKey"]) || bool.TryParse(configuration["Auth:Local:LogTokens"], out var log) && log)
        {
            errors.Add("Auth:Local settings (signing key, token logging) are development-only and must not be configured in Production.");
        }

        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (origins.Any(o => o.Trim() == "*" || Uri.TryCreate(o, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add("Cors:AllowedOrigins contains a wildcard or a development (localhost) origin; list the production web origin only.");
        }
    }

    public static void ThrowIfInvalid(IConfiguration configuration, bool isProduction)
    {
        var errors = Validate(configuration, isProduction);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
        }
    }
}
