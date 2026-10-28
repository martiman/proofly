using Proofly.Connectors.Common;

namespace Proofly.Connectors.Tests;

public class RejectionTests
{
    [Fact]
    public void Rejection_carries_reason() =>
        Assert.Equal("bad amount", new RowRejection(3, "amount", "bad amount").Reason);
}
