using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Schemas;

namespace Proofly.Connectors.Csv;

public static class CsvModule
{
    /// <summary>Registers one CSV <see cref="ISourceAdapter"/> for each of the 8 sources and the delivery manifest.</summary>
    public static IServiceCollection AddCsvAdapters(this IServiceCollection services, Action<CsvOptions>? configure = null)
    {
        var options = new CsvOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<ISourceAdapter>(_ => new CsvSourceAdapter<Proofly.Domain.Records.TransferInstruction>(SourceSchemas.TransferInstructions, options.For(SourceSchemas.TransferInstructions.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new CsvSourceAdapter<Proofly.Domain.Records.DirectDebitEvent>(SourceSchemas.DirectDebitEvents, options.For(SourceSchemas.DirectDebitEvents.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new CsvSourceAdapter<Proofly.Domain.Records.LedgerEntry>(SourceSchemas.InternalLedgerEntries, options.For(SourceSchemas.InternalLedgerEntries.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new CsvSourceAdapter<Proofly.Domain.Records.AccountBalance>(SourceSchemas.ClientAccountBalances, options.For(SourceSchemas.ClientAccountBalances.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new CsvSourceAdapter<Proofly.Domain.Records.BankTransaction>(SourceSchemas.BankingTransactions, options.For(SourceSchemas.BankingTransactions.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new CsvSourceAdapter<Proofly.Domain.Records.SafeguardingBalance>(SourceSchemas.SafeguardingBalances, options.For(SourceSchemas.SafeguardingBalances.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new CsvSourceAdapter<Proofly.Domain.Records.CardEvent>(SourceSchemas.CardTransactions, options.For(SourceSchemas.CardTransactions.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new CsvSourceAdapter<Proofly.Domain.Records.CardSettlement>(SourceSchemas.CardSettlements, options.For(SourceSchemas.CardSettlements.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new CsvSourceAdapter<Proofly.Domain.Records.DeliveryManifestEntry>(SourceSchemas.DeliveryManifest, options.For(SourceSchemas.DeliveryManifest.SourceName)));
        return services;
    }
}
