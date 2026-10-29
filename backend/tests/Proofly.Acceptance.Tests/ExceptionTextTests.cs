using Proofly.Acceptance.Tests.Support;

namespace Proofly.Acceptance.Tests;

/// <summary>Exceptions are created with a short headline title and a longer description; the detail's summary is the one-line explanation, never the same text as the description.</summary>
public sealed class ExceptionTextTests : IAsyncLifetime
{
    private readonly World _w = new();
    public Task InitializeAsync() => _w.InitializeAsync();
    public Task DisposeAsync() => _w.DisposeAsync();

    [Fact]
    public async Task Title_is_a_short_headline_and_description_the_detailed_text()
    {
        await _w.Users();
        var run = await _w.ProcessedRun(Flow.Le);
        Assert.True(await _w.Count($"SELECT count(*) FROM exceptions WHERE run_id='{run}'") > 3);
        Assert.Equal(0, await _w.Count($"SELECT count(*) FROM exceptions WHERE run_id='{run}' AND char_length(title) > 80"));
        var bad = string.Join(" | ", await _w.Strings($"SELECT title || ' // ' || COALESCE(description,'NULL') FROM exceptions WHERE run_id='{run}' AND (description IS NULL OR description = title OR char_length(description) <= char_length(title) OR title ~ '[A-Z]+_[A-Z]+')"));
        Assert.True(bad == "", bad);
        Assert.Equal(0, await _w.Count($"SELECT count(*) FROM exceptions WHERE run_id='{run}' AND (description IS NULL OR description = title OR char_length(description) <= char_length(title))"));
        Assert.Equal(0, await _w.Count($"SELECT count(*) FROM exceptions WHERE run_id='{run}' AND title ~ '[A-Z]+_[A-Z]+'")); // no raw reason codes in a headline

        var list = await _w.Get("viewer1", $"/api/v1/runs/{run}/exceptions?limit=200");
        list.Is(200);
        foreach (var e in list.Items())
        {
            Assert.InRange(e.GetProperty("title").GetString()!.Length, 1, 80);
            Assert.NotEqual(e.GetProperty("title").GetString(), e.GetProperty("description").GetString());
        }

        var id = list.Items()[0].GetProperty("id").GetString()!;
        var detail = await _w.Get("viewer1", $"/api/v1/exceptions/{id}");
        detail.Is(200);
        Assert.Equal(list.Items()[0].GetProperty("description").GetString(), detail.Str("description"));
        Assert.NotEqual(detail.Str("summary"), detail.Str("description"));
        Assert.NotEqual(detail.Str("summary"), detail.Str("title"));
        Assert.Equal(detail.Str("summary"), detail["trace"].GetProperty("calculation").GetString()); // the control's own one-line message
    }
}
