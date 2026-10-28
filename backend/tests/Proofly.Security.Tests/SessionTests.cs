using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Proofly.Api.Auth;
using Proofly.Domain.Enums;
using Proofly.Security.Tests.Support;

namespace Proofly.Security.Tests;

/// <summary>Issue #52 (3): session expiry and token validation, in Local mode (HS256) and Oidc mode (RS256 via a fake IdP).</summary>
public sealed class SessionLocalTests : IClassFixture<SecDb>, IAsyncLifetime
{
    private readonly SecDb _db;
    private SecFactory _f = null!;
    private static readonly byte[] Key = Encoding.UTF8.GetBytes(SecFactory.SigningKey);
    private static string Sub => LocalUsers.Subject(Role.Viewer);

    public SessionLocalTests(SecDb db) => _db = db;
    public async Task InitializeAsync() { _f = new SecFactory(_db); _ = _f.Server; await Task.CompletedTask; }
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private async Task<HttpStatusCode> Me(string? token) => (await _f.As(token).GetAsync("/api/v1/me")).StatusCode;

    private static string Valid(string iss = LocalTokenService.Issuer, string aud = LocalTokenService.Audience, string alg = SecurityAlgorithms.HmacSha256,
        DateTime? nbf = null, DateTime? exp = null, bool omitExp = false, byte[]? key = null) => Jwt.Hmac(Sub, iss, aud, key ?? Key, alg, nbf, exp, omitExpiry: omitExp);

    [Fact]
    public async Task Control_a_correctly_signed_token_is_accepted() => Assert.Equal(HttpStatusCode.OK, await Me(Valid()));

    [Fact]
    public async Task Expired_token_is_401_with_a_challenge()
    {
        var res = await _f.As(Valid(nbf: DateTime.UtcNow.AddHours(-2), exp: DateTime.UtcNow.AddHours(-1))).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Token_that_just_expired_beyond_the_skew_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Valid(nbf: DateTime.UtcNow.AddMinutes(-10), exp: DateTime.UtcNow.AddSeconds(-30))));

    [Fact]
    public async Task Not_yet_valid_token_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Valid(nbf: DateTime.UtcNow.AddHours(1), exp: DateTime.UtcNow.AddHours(2))));

    [Fact]
    public async Task Token_without_expiry_is_401()
    {
        var token = Valid(omitExp: true);
        var payload = Encoding.UTF8.GetString(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));
        Assert.DoesNotContain("\"exp\"", payload); // the test really built a token without exp
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(token));
    }

    [Fact]
    public async Task Wrong_audience_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, await Me(Valid(aud: "another-api")));

    [Fact]
    public async Task Wrong_issuer_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, await Me(Valid(iss: "https://evil.example")));

    [Fact]
    public async Task Algorithm_none_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Jwt.AlgNone(Sub, LocalTokenService.Issuer, LocalTokenService.Audience)));

    [Theory, InlineData(SecurityAlgorithms.HmacSha384), InlineData(SecurityAlgorithms.HmacSha512)]
    public async Task Other_hmac_algorithms_are_401_even_with_the_right_key(string alg) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Valid(alg: alg)));

    [Fact]
    public async Task Asymmetric_token_signed_with_an_unknown_rsa_key_is_401()
    {
        using var rsa = RSA.Create(2048);
        var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalTokenService.Issuer, Audience = LocalTokenService.Audience, Claims = new Dictionary<string, object> { ["sub"] = Sub },
            Expires = DateTime.UtcNow.AddMinutes(30), SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(token));
    }

    [Fact]
    public async Task Token_signed_with_a_different_secret_or_tampered_payload_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Valid(key: Encoding.UTF8.GetBytes("a-different-secret-of-32-chars-or-more!!"))));
        var parts = Valid().Split('.');
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"sub":"{{LocalUsers.Subject(Role.Approver)}}","iss":"proofly-local","aud":"proofly-api","exp":9999999999}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(HttpStatusCode.Unauthorized, await Me($"{parts[0]}.{forged}.{parts[2]}"));
    }

    [Theory, InlineData(""), InlineData("garbage"), InlineData("a.b.c"), InlineData("Bearer")]
    public async Task Malformed_bearer_values_are_401(string token) => Assert.Equal(HttpStatusCode.Unauthorized, await Me(token.Length == 0 ? null : token));

    [Fact]
    public async Task Non_bearer_schemes_are_401()
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String("local:viewer"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
        var q = _f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await q.GetAsync("/api/v1/me?access_token=" + Valid())).StatusCode);
    }

    [Fact]
    public async Task Unknown_subject_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Jwt.Hmac("local:nobody", LocalTokenService.Issuer, LocalTokenService.Audience, Key)));
}

public sealed class SessionRevalidationTests : IClassFixture<SecDb>
{
    private readonly SecDb _db;
    public SessionRevalidationTests(SecDb db) => _db = db;

    [Fact]
    public async Task Tokens_are_revalidated_on_every_request_so_expiry_and_revocation_apply_immediately()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var f = new SecFactory(_db, clock: clock);
        _ = f.Server;
        var token = f.Tokens.Issue(LocalUsers.Subject(Role.Viewer), TimeSpan.FromMinutes(10));
        var client = f.As(token);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        // The very same token, last accepted a moment ago, stops working once it expires: nothing is cached across requests.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);

        // A token issued in the future is not yet valid; it becomes valid when the clock reaches it.
        var future = f.Tokens.Issue(LocalUsers.Subject(Role.Viewer), TimeSpan.FromMinutes(10), issuedAt: clock.GetUtcNow().AddMinutes(30));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.As(future).GetAsync("/api/v1/me")).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.OK, (await f.As(future).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_user_or_revoking_a_role_takes_effect_on_the_next_request()
    {
        await using var f = new SecFactory(_db);
        _ = f.Server;
        var users = new Proofly.Data.Auth.UserDirectory(_db.Db);
        await users.EnsureUserAsync(new("u-sess", "Sess", "sess@sec.test", "sub-sess", true, [new("ANALYST", "*")]));
        var client = f.As(f.Tokens.Issue("sub-sess"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/runs")).StatusCode);

        await _db.Exec("DELETE FROM user_roles WHERE user_id='u-sess' AND role_id='ANALYST'");
        await _db.Exec("INSERT INTO user_roles (user_id, role_id, legal_entity_id) VALUES ('u-sess','VIEWER','*')");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/runs/x/sign-off/submit", null)).StatusCode);

        await _db.Exec("UPDATE users SET active=false WHERE id='u-sess'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/runs")).StatusCode);
        await _db.Exec("UPDATE users SET active=true WHERE id='u-sess'");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/runs")).StatusCode);
    }
}

public sealed class SessionOidcTests : IClassFixture<SecDb>, IAsyncLifetime
{
    private readonly SecDb _db;
    private FakeIdp _idp = null!;
    private SecFactory _f = null!;
    private const string Sub = "oidc-sec-user";

    public SessionOidcTests(SecDb db) => _db = db;

    public async Task InitializeAsync()
    {
        _idp = new FakeIdp();
        _f = new SecFactory(_db, "Oidc", settings: new Dictionary<string, string?>
        {
            ["Auth:Oidc:Authority"] = _idp.Authority, ["Auth:Oidc:Audience"] = FakeIdp.Audience, ["Auth:Oidc:RequireHttpsMetadata"] = "false",
        });
        _ = _f.Server;
        await new Proofly.Data.Auth.UserDirectory(_db.Db).EnsureUserAsync(new("u-oidc", "Oidc User", "oidc@sec.test", Sub, true, [new("VIEWER", "*")]));
    }

    public Task DisposeAsync() { _idp.Dispose(); return _f.DisposeAsync().AsTask(); }

    private async Task<HttpStatusCode> Me(string token) => (await _f.As(token).GetAsync("/api/v1/me")).StatusCode;

    [Fact]
    public async Task Control_a_token_from_the_identity_provider_is_accepted() => Assert.Equal(HttpStatusCode.OK, await Me(_idp.Mint(Sub)));

    [Fact]
    public async Task Expired_not_yet_valid_wrong_audience_and_wrong_issuer_are_401()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(_idp.Mint(Sub, d => { d.NotBefore = now.AddHours(-2); d.IssuedAt = now.AddHours(-2); d.Expires = now.AddHours(-1); return d; })));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(_idp.Mint(Sub, d => { d.NotBefore = now.AddHours(1); d.Expires = now.AddHours(2); return d; })));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(_idp.Mint(Sub, d => { d.Audience = "someone-else"; return d; })));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(_idp.Mint(Sub, d => { d.Issuer = "https://evil.example"; return d; })));
    }

    [Fact]
    public async Task Algorithm_none_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, await Me(Jwt.AlgNone(Sub, _idp.Authority, FakeIdp.Audience)));

    [Fact]
    public async Task Hs256_token_keyed_with_the_public_rsa_key_material_is_401()
    {
        // Classic RS256 -> HS256 confusion: the attacker signs with the (public) key bytes as an HMAC secret.
        var pub = _idp.Key.Rsa!.ExportSubjectPublicKeyInfo();
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Jwt.Hmac(Sub, _idp.Authority, FakeIdp.Audience, pub, kid: "k1")));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Jwt.Hmac(Sub, _idp.Authority, FakeIdp.Audience, pub)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Jwt.Hmac(Sub, _idp.Authority, FakeIdp.Audience, Encoding.UTF8.GetBytes(Convert.ToBase64String(pub)), kid: "k1")));
    }

    [Fact]
    public async Task Token_signed_by_an_unknown_key_is_401()
    {
        using var other = RSA.Create(2048);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(_idp.Mint(Sub, key: new RsaSecurityKey(other) { KeyId = "k1" })));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(_idp.Mint(Sub, key: new RsaSecurityKey(other) { KeyId = "other" })));
    }

    [Fact]
    public async Task Locally_signed_tokens_are_rejected_in_oidc_mode()
    {
        var local = Jwt.Hmac("local:viewer", LocalTokenService.Issuer, LocalTokenService.Audience, Encoding.UTF8.GetBytes(SecFactory.SigningKey));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(local));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Jwt.Hmac(Sub, LocalTokenService.Issuer, LocalTokenService.Audience, Encoding.UTF8.GetBytes(SecFactory.SigningKey))));
    }

    [Fact]
    public async Task The_fixed_local_users_do_not_exist_in_oidc_mode()
    {
        Assert.Equal(0, await _db.Scalar<long>("SELECT count(*) FROM users WHERE external_subject LIKE 'local:%'"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(_idp.Mint("local:approver")));
    }
}

public sealed class SessionOidcUnconfiguredTests : IClassFixture<SecDb>
{
    private readonly SecDb _db;
    public SessionOidcUnconfiguredTests(SecDb db) => _db = db;

    [Fact]
    public async Task Oidc_mode_without_an_authority_denies_every_token()
    {
        await using var f = new SecFactory(_db, "Oidc");
        _ = f.Server;
        var local = Jwt.Hmac("local:viewer", LocalTokenService.Issuer, LocalTokenService.Audience, Encoding.UTF8.GetBytes(SecFactory.SigningKey));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.As(local).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.As((string?)null).GetAsync("/api/v1/me")).StatusCode);
    }
}
