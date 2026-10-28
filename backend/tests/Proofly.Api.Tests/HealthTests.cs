using Microsoft.AspNetCore.Mvc.Testing;

namespace Proofly.Api.Tests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Healthz_returns_ok()
    {
        var client = factory.WithWebHostBuilder(b => b.UseSetting(
            "ConnectionStrings:Proofly", "Host=localhost;Port=55432;Username=proofly;Database=proofly_test")).CreateClient();
        var res = await client.GetAsync("/healthz");
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
    }
}
