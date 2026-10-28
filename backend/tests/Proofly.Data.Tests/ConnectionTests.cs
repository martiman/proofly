using Npgsql;

namespace Proofly.Data.Tests;

public class ConnectionTests
{
    internal const string TestConnectionString = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";

    [Fact]
    public async Task Test_database_is_reachable()
    {
        await using var ds = NpgsqlDataSource.Create(TestConnectionString);
        await using var cmd = ds.CreateCommand("SELECT 1");
        Assert.Equal(1, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Migration_runner_applies_script_once()
    {
        await using var ds = NpgsqlDataSource.Create(TestConnectionString);
        var id = "t-" + Guid.NewGuid().ToString("N");
        var runner = new MigrationRunner(ds);
        Assert.Equal(1, await runner.RunAsync([(id, "SELECT 1")]));
        Assert.Equal(0, await runner.RunAsync([(id, "SELECT 1")]));
    }
}
