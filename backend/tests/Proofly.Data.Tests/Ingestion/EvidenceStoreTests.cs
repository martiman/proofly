using System.Security.Cryptography;
using System.Text;
using Proofly.Data.Ingestion;

namespace Proofly.Data.Tests.Ingestion;

public sealed class EvidenceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proofly-ev-" + Guid.NewGuid().ToString("N"));
    private EvidenceStore Store() => new(new EvidenceOptions { Directory = _dir });
    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    public void Dispose() { if (Directory.Exists(_dir)) { foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(_dir, true); } }

    [Fact]
    public async Task Stores_content_addressed_by_sha256_and_reads_back()
    {
        var o = await Store().WriteAsync(Bytes("hello"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData("hello"u8)).ToLowerInvariant(), o.Sha256);
        Assert.Equal(5, o.Length);
        Assert.EndsWith(Path.Combine(o.Sha256[..2], o.Sha256), o.Path);
        using var r = new StreamReader(Store().OpenRead(o.Sha256));
        Assert.Equal("hello", await r.ReadToEndAsync());
        Assert.True(await Store().VerifyAsync(o.Sha256));
    }

    [Fact]
    public async Task Same_bytes_never_overwrite_and_leave_no_temp_files()
    {
        var s = Store();
        var a = await s.WriteAsync(Bytes("same"));
        var written = File.GetLastWriteTimeUtc(a.Path);
        await Task.Delay(20);
        var b = await s.WriteAsync(Bytes("same"));
        Assert.Equal(a.Path, b.Path);
        Assert.Equal(written, File.GetLastWriteTimeUtc(a.Path));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, ".tmp")));
    }

    [Fact]
    public async Task Verify_detects_tampering_and_absence()
    {
        var s = Store();
        var o = await s.WriteAsync(Bytes("original"));
        File.SetAttributes(o.Path, FileAttributes.Normal);
        await File.WriteAllTextAsync(o.Path, "changed");
        Assert.False(await s.VerifyAsync(o.Sha256));
        Assert.False(await s.VerifyAsync(new string('a', 64)));
        Assert.Throws<FileNotFoundException>(() => s.OpenRead(new string('a', 64)));
    }

    [Fact]
    public void Rejects_bad_configuration_and_bad_hashes()
    {
        Assert.Throws<InvalidOperationException>(() => new EvidenceStore(new EvidenceOptions()));
        Assert.Throws<ArgumentException>(() => Store().PathFor("../../etc/passwd"));
    }
}
