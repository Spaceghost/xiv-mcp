using System.Text.Json.Nodes;
using XivMcp.Core.Protocol;

namespace XivMcp.Core.Tests;

/// <summary>
/// An allocation budget for the path every request takes (parse, classify). The number is bytes per
/// request on one thread, measured with the runtime's own counter; the budget is about twice what it
/// costs today, so a change that doubles the garbage per request fails here instead of in the game.
/// </summary>
public sealed class AllocationBudgetTests
{
    private const string Request = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"hello\"}}}";

    private static long PerOperation(Action action, int n = 2000)
    {
        for (var i = 0; i < 200; i++)
            action();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < n; i++)
            action();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / n;
    }

    [Fact]
    public void ParsingAndClassifyingARequestStaysWithinItsAllocationBudget()
    {
        var bytes = PerOperation(() => JsonRpcMessage.Classify(JsonNode.Parse(Request)));
        Assert.True(bytes < 3000, $"{bytes} bytes allocated per request, budget 3000");
    }
}
