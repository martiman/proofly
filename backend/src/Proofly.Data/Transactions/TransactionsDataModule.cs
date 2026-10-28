using Microsoft.Extensions.DependencyInjection;

namespace Proofly.Data.Transactions;

public static class TransactionsDataModule
{
    public static IServiceCollection AddTransactionData(this IServiceCollection services) =>
        services.AddSingleton<ITransactionReadRepository, TransactionReadRepository>()
            .AddSingleton<ILineageRepository, LineageRepository>();
}
