using ShiftAI.Core;

var menu = new List<MenuItem>
{
    new("ragongtan", "\uB77C\uACF5\uD0C4", 6900),
    new("raudon", "\uB77C\uC6B0\uB3D9", 8000),
    new("ramen-basic", "\uC77C\uBC18 \uB77C\uBA74", 3500),
    new("cola", "\uCF5C\uB77C", 2500),
    new("iced-tea", "\uC544\uC774\uC2A4\uD2F0", 3000),
    new("kimchi-fried-rice", "\uAE40\uCE58\uBCF6\uC74C\uBC25", 6500),
    new("hotdog", "\uD56B\uB3C4\uADF8", 4000)
};

var matcher = new MenuMatcher(menu);
var router = new IntentRouter(matcher);
var emptyCart = new CartSnapshot([]);

Assert(matcher.FindCandidates("\uB77C\uBA74 \uC2DC\uCF1C\uC918").Count == 3, "Generic ramen should produce three candidates.");
Assert(matcher.FindCandidates("\uCF5C\uB77C \uD558\uB098 \uCD94\uAC00\uD574").Single().Name == "\uCF5C\uB77C", "Cola should match one menu item.");
Assert(MenuMatcher.ExtractQuantity("\uCF5C\uB77C 2\uAC1C \uCD94\uAC00\uD574") == 2, "Numeric quantity should parse.");
Assert(MenuMatcher.ExtractQuantity("\uCF5C\uB77C \uD558\uB098 \uCD94\uAC00\uD574") == 1, "Korean quantity should parse.");

var ramenRoute = await router.RouteAsync("\uB77C\uBA74 \uC2DC\uCF1C\uC918", emptyCart, false);
Assert(ramenRoute.Intent == IntentType.ClarifyMenuItem, "Generic ramen must not place an immediate order.");

var colaRoute = await router.RouteAsync("\uCF5C\uB77C \uD558\uB098 \uCD94\uAC00\uD574", emptyCart, false);
Assert(colaRoute.Intent == IntentType.AddFood && colaRoute.MenuItem?.Name == "\uCF5C\uB77C", "Cola should route to AddFood.");

var launchRoute = await router.RouteAsync("\uB864 \uCF1C\uC918", emptyCart, false);
Assert(launchRoute.Intent == IntentType.LaunchGame && launchRoute.GameName == "League of Legends", "롤 켜줘 should route to LaunchGame.");

var orderRoute = await router.RouteAsync("\uC8FC\uBB38\uD574", emptyCart, true);
Assert(orderRoute.Intent == IntentType.PlaceOrder, "주문해 should route to PlaceOrder.");

var fakeLog = new FakeActionLog();
var fakeAdapter = new FakePcCafeAdapter();
var tools = HermesSkillToolRegistry.FromAdapter(fakeAdapter);
Assert(tools.CanExecute(IntentType.AddFood), "Hermes registry should expose orderFood.");
Assert(tools.CanExecute(IntentType.CallStaff), "Hermes registry should expose callStaff.");
var executor = new ActionExecutor(38, new Cart(), router, tools, fakeLog);

var clarify = await executor.ExecuteAsync("\uB77C\uBA74 \uC2DC\uCF1C\uC918");
Assert(clarify.Status == AgentStatus.NeedsClarification, "Ramen flow should ask for clarification first.");
Assert(clarify.Candidates?.Count == 3, "Ramen clarification should show three candidates.");
Assert(fakeAdapter.OrderFoodCalls == 0, "Ramen command alone must not call orderFood.");

var selected = await executor.SelectMenuItemAsync(menu.Single(item => item.Id == "ramen-basic"), 1, "\uB77C\uBA74 \uC2DC\uCF1C\uC918");
Assert(selected.Status == AgentStatus.Completed, "Selecting ramen should immediately use the Geto hand action.");
Assert(selected.AssistantText == "\uC54C\uACA0\uC5B4, \uC2DC\uD0AC\uAC8C!!", "Food add should answer conversationally.");
Assert(fakeAdapter.OrderFoodCalls == 1, "Food selection should call orderFood/Geto hand action once.");
Assert(fakeLog.Entries.Count == 1 && fakeLog.Entries[0].ToolName == "orderFood", "orderFood action should be written to log.");

Console.WriteLine("All ShiftAI core tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class FakeActionLog : IActionLog
{
    public List<ActionLogEntry> Entries { get; } = [];

    public Task AppendAsync(ActionLogEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class FakePcCafeAdapter : IPcCafeAdapter
{
    public int OrderFoodCalls { get; private set; }

    public Task<ToolResult> OrderFoodAsync(int seatNumber, CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        OrderFoodCalls++;
        return Task.FromResult(new ToolResult("orderFood", AgentStatus.Completed, "ok", new { seatNumber, total = cart.Total }));
    }

    public Task<ToolResult> CallStaffAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult("callStaff", AgentStatus.Completed, "ok"));
    }

    public Task<ToolResult> TroubleshootAudioAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult("troubleshootAudio", AgentStatus.Completed, "ok"));
    }

    public Task<ToolResult> LaunchGameAsync(int seatNumber, string gameName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult("launchGame", AgentStatus.Completed, "ok"));
    }

    public Task<ToolResult> GetRemainingTimeAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult("getRemainingTime", AgentStatus.Completed, "ok"));
    }

    public Task<ToolResult> CancelCurrentActionAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult("cancelCurrentAction", AgentStatus.Cancelled, "ok"));
    }
}
