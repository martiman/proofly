using System.Reflection;

namespace Proofly.Data;

/// <summary>Ordered SQL scripts embedded in this assembly (Migrations/Scripts/NNNN_name.sql).</summary>
public static class EmbeddedMigrations
{
    public static IReadOnlyList<(string Id, string Sql)> Load()
    {
        var asm = typeof(EmbeddedMigrations).Assembly;
        const string marker = ".Migrations.Scripts.";
        return asm.GetManifestResourceNames()
            .Where(n => n.Contains(marker, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                using var s = asm.GetManifestResourceStream(n)!;
                using var r = new StreamReader(s);
                return (Id: n[(n.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..^4], Sql: r.ReadToEnd());
            })
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
    }
}
