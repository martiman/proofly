using Npgsql;
using Proofly.Data.Auth;
using Proofly.Domain.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Data.Tests.Auth;

public class AuthDataTests : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private readonly SchemaFixture _f;
    private UserDirectory Dir => new(_f.DataSource);
    public AuthDataTests(SchemaFixture f) => _f = f;

    public async Task InitializeAsync() => await new MigrationRunner(_f.DataSource).RunAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Hook(bool fail = false) : IAuthAuditHook
    {
        public int Calls;
        public Task RecordAsync(NpgsqlConnection c, NpgsqlTransaction t, AuthAuditEvent e, CancellationToken ct)
        {
            Calls++;
            if (fail) { throw new InvalidOperationException("audit failed"); }
            Assert.Equal("ACCESS_CHANGED", e.EventType);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Seeded_role_permissions_equal_the_code_bundles()
    {
        await using var cmd = _f.DataSource.CreateCommand("SELECT role_id, permission_id FROM role_permissions");
        await using var r = await cmd.ExecuteReaderAsync();
        var db = new Dictionary<string, HashSet<string>>();
        while (await r.ReadAsync()) { (db.TryGetValue(r.GetString(0), out var s) ? s : db[r.GetString(0)] = []).Add(r.GetString(1)); }

        Assert.Equal(RolePermissions.Roles.Count(), db.Count);
        foreach (var role in RolePermissions.Roles)
        {
            Assert.Equal(RolePermissions.For(role).Select(WireNames.ToPermission).Order().ToArray(), db[WireNames.ToUpperSnake(role)].Order().ToArray());
        }
        Assert.Equal(Enum.GetValues<Permission>().Length, await _f.Scalar<long>("SELECT count(*) FROM permissions"));
        Assert.Equal(RolePermissions.Roles.Count(), await _f.Scalar<long>("SELECT count(*) FROM roles"));
    }

    [Fact]
    public async Task Role_seed_script_is_idempotent()
    {
        var sql = EmbeddedMigrations.Load().Single(m => m.Id.StartsWith("0007_", StringComparison.Ordinal)).Sql;
        var before = await _f.Scalar<long>("SELECT count(*) FROM role_permissions");
        await _f.Exec(sql);
        Assert.Equal(before, await _f.Scalar<long>("SELECT count(*) FROM role_permissions"));
    }

    [Fact]
    public async Task Roles_and_scope_are_read_from_the_database()
    {
        await Dir.EnsureUserAsync(new UserRecord("u-a", "Ann", "ann@x.test", "sub-a", true,
            [new RoleAssignment("ANALYST", "LE-1"), new RoleAssignment("VIEWER", "LE-2")]));
        var u = await Dir.FindBySubjectAsync("sub-a");
        Assert.NotNull(u);
        Assert.Equal("u-a", u.Id);
        Assert.Equal([new RoleAssignment("ANALYST", "LE-1"), new RoleAssignment("VIEWER", "LE-2")], u.Assignments);
        Assert.Equal("Ann", (await Dir.FindByIdAsync("u-a"))!.Name);
        Assert.Null(await Dir.FindBySubjectAsync("nobody"));
    }

    [Fact]
    public async Task User_without_roles_has_no_assignments_and_inactive_is_visible()
    {
        await Dir.EnsureUserAsync(new UserRecord("u-b", "Bob", "bob@x.test", "sub-b", false, []));
        var u = await Dir.FindBySubjectAsync("sub-b");
        Assert.False(u!.Active);
        Assert.Empty(u.Assignments);
    }

    [Fact]
    public async Task Provisioning_creates_a_roleless_user_and_audits_in_the_same_transaction()
    {
        var hook = new Hook();
        var u = await Dir.ProvisionAsync("sub-p", "Pat", "pat@x.test", hook);
        Assert.NotNull(u);
        Assert.Equal(1, hook.Calls);
        Assert.Empty((await Dir.FindBySubjectAsync("sub-p"))!.Assignments);
    }

    [Fact]
    public async Task Failing_audit_rolls_the_provisioned_user_back()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Dir.ProvisionAsync("sub-q", "Quinn", "quinn@x.test", new Hook(fail: true)));
        Assert.Null(await Dir.FindBySubjectAsync("sub-q"));
    }

    [Fact]
    public async Task Provisioning_does_not_take_over_an_existing_email()
    {
        await Dir.EnsureUserAsync(new UserRecord("u-c", "Cy", "cy@x.test", "sub-c", true, []));
        Assert.Null(await Dir.ProvisionAsync("sub-c2", "Cy2", "cy@x.test", new Hook()));
    }
}
