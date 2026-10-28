using Proofly.Data.Sources;

namespace Proofly.Data.Tests.Sources;

public class DeliveryRulesTests
{
    [Theory]
    [InlineData("REJECTED", false, false, true)]
    [InlineData("REJECTED", true, false, true)]
    [InlineData("REJECTED", false, true, false)]   // duplicate
    [InlineData("RECEIVED", false, false, true)]
    [InlineData("RECEIVED", true, false, false)]   // already ingested
    [InlineData("QUALIFIED", true, false, false)]
    [InlineData("SUPERSEDED", true, false, false)]
    public void Retry_rule(string state, bool ingested, bool duplicate, bool expected) =>
        Assert.Equal(expected, DeliveryRules.CanRetryIngestion(state, ingested, duplicate));

    [Theory]
    [InlineData("RECEIVED", false, true)]
    [InlineData("QUALIFIED", false, true)]
    [InlineData("QUALIFIED", true, false)]
    [InlineData("REJECTED", false, false)]
    [InlineData("SUPERSEDED", false, false)]
    public void Supersede_rule(string state, bool already, bool expected) =>
        Assert.Equal(expected, DeliveryRules.CanSupersede(state, already));
}
