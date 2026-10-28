using System.Security.Cryptography;

namespace Proofly.Data.Ingestion;

public sealed class EvidenceOptions
{
    /// <summary>Configuration key <c>Evidence:Directory</c>.</summary>
    public string Directory { get; set; } = "";
}

/// <summary>Location and identity of a stored original.</summary>
public sealed record EvidenceObject(string Sha256, long Length, string Path);

/// <summary>
/// Append-only, content-addressed store for the original delivery files: <c>{dir}/{sha[0..2]}/{sha}</c>. The
/// SHA-256 of the bytes is the identity, so storing the same bytes again is a no-op and an existing object is never
/// overwritten. Objects are marked read-only where the file system supports it.
/// </summary>
public sealed class EvidenceStore
{
    private readonly string _root;

    public EvidenceStore(EvidenceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Directory))
        {
            throw new InvalidOperationException("Evidence:Directory is not configured.");
        }
        _root = Path.GetFullPath(options.Directory);
        Directory.CreateDirectory(_root);
    }

    public static bool IsValidSha256(string s) => s.Length == 64 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public string PathFor(string sha256)
    {
        if (!IsValidSha256(sha256)) throw new ArgumentException("Not a lower-case hex SHA-256.", nameof(sha256));
        return Path.Combine(_root, sha256[..2], sha256);
    }

    public bool Exists(string sha256) => File.Exists(PathFor(sha256));

    /// <summary>Streams the content to a temporary file while hashing it, then moves it into place unless it already exists.</summary>
    public async Task<EvidenceObject> WriteAsync(Stream content, CancellationToken ct = default)
    {
        var tmpDir = Path.Combine(_root, ".tmp");
        Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));
        string sha;
        long length;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                int n;
                length = 0;
                while ((n = await content.ReadAsync(buf, ct)) > 0)
                {
                    hash.AppendData(buf, 0, n);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    length += n;
                }
            }
            sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var path = PathFor(sha);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                try { File.Move(tmp, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { /* concurrent writer stored identical bytes */ }
                try { File.SetAttributes(path, FileAttributes.ReadOnly); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
            }
            return new EvidenceObject(sha, length, path);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public Stream OpenRead(string sha256)
    {
        var path = PathFor(sha256);
        if (!File.Exists(path)) throw new FileNotFoundException("Evidence object not found.", path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    }

    /// <summary>Recomputes the hash of the stored bytes; false when missing or altered.</summary>
    public async Task<bool> VerifyAsync(string sha256, CancellationToken ct = default)
    {
        if (!Exists(sha256)) return false;
        await using var s = OpenRead(sha256);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
        return actual == sha256;
    }
}
