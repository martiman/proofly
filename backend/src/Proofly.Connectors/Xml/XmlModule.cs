using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Schemas;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Xml;

public static class XmlModule
{
    /// <summary>Registers one XML <see cref="ISourceAdapter"/> for each of the 8 sources and the delivery manifest.</summary>
    public static IServiceCollection AddXmlAdapters(this IServiceCollection services, Action<XmlOptions>? configure = null)
    {
        var options = new XmlOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<ISourceAdapter>(_ => new XmlSourceAdapter<TransferInstruction>(SourceSchemas.TransferInstructions, options.For(SourceSchemas.TransferInstructions.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new XmlSourceAdapter<DirectDebitEvent>(SourceSchemas.DirectDebitEvents, options.For(SourceSchemas.DirectDebitEvents.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new XmlSourceAdapter<LedgerEntry>(SourceSchemas.InternalLedgerEntries, options.For(SourceSchemas.InternalLedgerEntries.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new XmlSourceAdapter<AccountBalance>(SourceSchemas.ClientAccountBalances, options.For(SourceSchemas.ClientAccountBalances.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new XmlSourceAdapter<BankTransaction>(SourceSchemas.BankingTransactions, options.For(SourceSchemas.BankingTransactions.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new XmlSourceAdapter<SafeguardingBalance>(SourceSchemas.SafeguardingBalances, options.For(SourceSchemas.SafeguardingBalances.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new XmlSourceAdapter<CardEvent>(SourceSchemas.CardTransactions, options.For(SourceSchemas.CardTransactions.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new XmlSourceAdapter<CardSettlement>(SourceSchemas.CardSettlements, options.For(SourceSchemas.CardSettlements.SourceName)));
        services.AddSingleton<ISourceAdapter>(_ => new XmlSourceAdapter<DeliveryManifestEntry>(SourceSchemas.DeliveryManifest, options.For(SourceSchemas.DeliveryManifest.SourceName)));
        return services;
    }
}
