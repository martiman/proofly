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

    public static void ThrowIfInvalid(IConfiguration configuration, bool isProduction)
    {
        var errors = Validate(configuration, isProduction);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
        }
    }
}
