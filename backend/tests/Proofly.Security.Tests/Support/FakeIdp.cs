using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Proofly.Security.Tests.Support;

/// <summary>A minimal OIDC provider on loopback: discovery document, JWKS, and token minting with arbitrary claims/keys.</summary>
public sealed class FakeIdp : IDisposable
{
    public const string Audience = "proofly-api-sec";
    private readonly HttpListener _http = new();
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly CancellationTokenSource _cts = new();
    public string Authority { get; }
    public RsaSecurityKey Key { get; }

    public FakeIdp()
    {
        var port = FreePort();
        Authority = $"http://127.0.0.1:{port}";
        _http.Prefixes.Add(Authority + "/");
        _http.Start();
        Key = new RsaSecurityKey(_rsa) { KeyId = "k1" };
        _ = Task.Run(Serve);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task Serve()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { return; }
            string body;
            if (ctx.Request.Url!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
            {
                body = JsonSerializer.Serialize(new { issuer = Authority, jwks_uri = Authority + "/jwks", authorization_endpoint = Authority + "/auth", token_endpoint = Authority + "/token",
                    response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" } });
            }
            else
            {
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "k1" });
                body = JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public string Mint(string subject, Func<SecurityTokenDescriptor, SecurityTokenDescriptor>? tweak = null, SecurityKey? key = null, string alg = SecurityAlgorithms.RsaSha256)
    {
        var now = DateTime.UtcNow;
        var d = new SecurityTokenDescriptor
        {
            Issuer = Authority, Audience = Audience, Claims = new Dictionary<string, object> { ["sub"] = subject },
            NotBefore = now.AddMinutes(-1), IssuedAt = now.AddMinutes(-1), Expires = now.AddMinutes(30),
            SigningCredentials = new SigningCredentials(key ?? Key, alg),
        };
        if (tweak is not null) { d = tweak(d); }
        return new JsonWebTokenHandler().CreateToken(d);
    }

    public void Dispose() { _cts.Cancel(); _http.Close(); _rsa.Dispose(); }
}
