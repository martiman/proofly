using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Schemas;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Excel;

public static class ExcelModule
{
    /// <summary>Registers an Xlsx and an Xls <see cref="ISourceAdapter"/> for each of the 8 sources and the delivery manifest (18 adapters).</summary>
    public static IServiceCollection AddExcelAdapters(this IServiceCollection services, Action<ExcelOptions>? configure = null)
    {
        var options = new ExcelOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        foreach (var format in new[] { SourceFormat.Xlsx, SourceFormat.Xls })
        {
            Add<TransferInstruction>(SourceSchemas.TransferInstructions);
            Add<DirectDebitEvent>(SourceSchemas.DirectDebitEvents);
            Add<LedgerEntry>(SourceSchemas.InternalLedgerEntries);
            Add<AccountBalance>(SourceSchemas.ClientAccountBalances);
            Add<BankTransaction>(SourceSchemas.BankingTransactions);
            Add<SafeguardingBalance>(SourceSchemas.SafeguardingBalances);
            Add<CardEvent>(SourceSchemas.CardTransactions);
            Add<CardSettlement>(SourceSchemas.CardSettlements);
            Add<DeliveryManifestEntry>(SourceSchemas.DeliveryManifest);

            void Add<T>(ISourceSchema<T> schema) where T : class, ISourceRecord =>
                services.AddSingleton<ISourceAdapter>(_ => new ExcelSourceAdapter<T>(schema, format, options.For(schema.SourceName)));
        }
        return services;
    }
}
