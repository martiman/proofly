using System.Diagnostics;

namespace Proofly.Recovery.Tests.Support;

/// <summary>Locates pg_dump / pg_restore (PG_BIN, the Homebrew postgresql@14 location, then PATH) and runs them.</summary>
public static class PgTools
{
    private static readonly string[] Candidates =
    [
        Environment.GetEnvironmentVariable("PG_BIN") ?? "",
        "/opt/homebrew/opt/postgresql@14/bin",
        "/usr/local/opt/postgresql@14/bin",
        "/usr/lib/postgresql/14/bin",
        "/usr/bin",
    ];

    public static string? Find(string tool)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe" } : new[] { tool };
        foreach (var dir in Candidates.Where(d => d.Length > 0))
            foreach (var n in names)
                if (File.Exists(Path.Combine(dir, n))) return Path.Combine(dir, n);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
                if (File.Exists(Path.Combine(dir, n))) return Path.Combine(dir, n);
        return null;
    }

    public static bool Available => Find("pg_dump") is not null && Find("pg_restore") is not null;

    public static async Task<string> RunAsync(string tool, params string[] args)
    {
        var path = Find(tool) ?? throw new InvalidOperationException($"{tool} not found.");
        var psi = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var err = await stderr;
        if (p.ExitCode != 0) throw new InvalidOperationException($"{tool} exited with {p.ExitCode}: {err}");
        return await stdout + err;
    }

    public static Task DumpAsync(string database, string file) =>
        RunAsync("pg_dump", "-Fc", "-h", "localhost", "-p", "55432", "-U", "proofly", "-d", database, "-f", file);

    /// <summary>Same flags as deploy/customer/README.md.</summary>
    public static Task RestoreAsync(string database, string file) =>
        RunAsync("pg_restore", "--clean", "--if-exists", "-h", "localhost", "-p", "55432", "-U", "proofly", "-d", database, file);
}

/// <summary>[Fact] that is skipped, with a reason, when pg_dump / pg_restore are not installed.</summary>
public sealed class RequiresPgToolsFactAttribute : FactAttribute
{
    public RequiresPgToolsFactAttribute()
    {
        if (!PgTools.Available) Skip = "pg_dump/pg_restore not found (set PG_BIN to the PostgreSQL client bin directory).";
    }
}
