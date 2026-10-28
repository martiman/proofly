using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Csv;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Tests;

public class CsvModuleTests
{
    [Fact]
    public void AddCsvAdapters_registers_all_eight_sources_and_the_manifest()
    {
        var sc = new ServiceCollection().AddCsvAdapters(o => o.Sources[SourceNames.BankingTransactions] = new CsvSourceOptions { Delimiter = ';' });
        using var sp = sc.BuildServiceProvider();
        var registry = new SourceAdapterRegistry(sp.GetServices<ISourceAdapter>());
        foreach (var s in SourceNames.DataSources.Append(SourceNames.DeliveryManifest))
        {
            var a = registry.Resolve(s, SourceFormat.Csv);
            Assert.Contains("1.0", a.SupportedSchemaVersions);
        }
        Assert.Equal(9, registry.Adapters.Count);
    }
}
