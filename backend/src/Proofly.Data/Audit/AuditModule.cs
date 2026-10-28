using Microsoft.Extensions.DependencyInjection;

namespace Proofly.Data.Audit;

public static class AuditModule
{
    /// <summary>Registers writer, verifier and reader. Requires the NpgsqlDataSource from AddData.</summary>
    public static IServiceCollection AddAudit(this IServiceCollection services)
    {
        services.AddSingleton<IAuditWriter, AuditWriter>();
        services.AddSingleton<IAuditVerifier, AuditVerifier>();
        services.AddSingleton<IAuditReader, AuditReader>();
        return services;
    }
}
