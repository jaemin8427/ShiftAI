using System.Globalization;

namespace ShiftAI.Core;

public sealed class ActionExecutor
{
    private readonly int _seatNumber;
    private readonly Cart _cart;
    private readonly IIntentRouter _router;
    private readonly HermesSkillToolRegistry _tools;
    private readonly IActionLog _actionLog;

    public ActionExecutor(int seatNumber, Cart cart, IIntentRouter router, IPcCafeAdapter adapter, IActionLog actionLog)
        : this(seatNumber, cart, router, HermesSkillToolRegistry.FromAdapter(adapter), actionLog)
    {
    }

    public ActionExecutor(int seatNumber, Cart cart, IIntentRouter router, HermesSkillToolRegistry tools, IActionLog actionLog)
    {
        _seatNumber = seatNumber;
        _cart = cart;
        _router = router;
        _tools = tools;
        _actionLog = actionLog;
    }

    public Task<AgentResponse> ExecuteAsync(string text, CancellationToken cancellationToken = default)
    {
        return ExecuteRouteAsync(_router.RouteAsync(text, _cart.Snapshot, _cart.AwaitingConfirmation, cancellationToken), cancellationToken);
    }

    public Task<AgentResponse> SelectMenuItemAsync(MenuItem item, int quantity, string originalText, CancellationToken cancellationToken = default)
    {
        var route = Task.FromResult(new IntentRoute(IntentType.AddFood, originalText, Math.Max(1, quantity), MenuItem: item));
        return ExecuteRouteAsync(route, cancellationToken);
    }

    private async Task<AgentResponse> ExecuteRouteAsync(Task<IntentRoute> routeTask, CancellationToken cancellationToken)
    {
        var route = await routeTask;
        ToolResult? toolResult = null;
        AgentStatus status;
        string assistantText;
        string? confirmationText = null;

        switch (route.Intent)
        {
            case IntentType.AddFood when route.MenuItem is not null:
                _cart.Add(route.MenuItem, route.Quantity);
                toolResult = await ExecuteToolAsync(route, cancellationToken);
                await WriteLogAsync(route, toolResult, cancellationToken);
                status = toolResult.Status;
                assistantText = toolResult.Status == AgentStatus.Completed
                    ? "\uC54C\uACA0\uC5B4, \uC2DC\uD0AC\uAC8C!!"
                    : toolResult.Message;
                _cart.MarkOrderPlaced();
                break;

            case IntentType.ClarifyMenuItem:
                status = AgentStatus.NeedsClarification;
                assistantText = "\uC5B4\uB5A4 \uBA54\uB274\uB97C \uC6D0\uD558\uC2DC\uB098\uC694? \uD6C4\uBCF4 \uC911 \uD558\uB098\uB97C \uC120\uD0DD\uD574 \uC8FC\uC138\uC694.";
                break;

            case IntentType.PlaceOrder:
                if (!_cart.AwaitingConfirmation || _cart.Snapshot.IsEmpty)
                {
                    status = AgentStatus.NeedsClarification;
                    assistantText = "\uD655\uC815 \uB300\uAE30 \uC911\uC778 \uC8FC\uBB38\uC774 \uC5C6\uC2B5\uB2C8\uB2E4. \uBA3C\uC800 \uBA54\uB274\uB97C \uB2F4\uC544 \uC8FC\uC138\uC694.";
                    break;
                }

                toolResult = await ExecuteToolAsync(route, cancellationToken);
                status = toolResult.Status;
                assistantText = toolResult.Message;
                await WriteLogAsync(route, toolResult, cancellationToken);
                _cart.MarkOrderPlaced();
                break;

            case IntentType.CallStaff:
                toolResult = await ExecuteToolAsync(route, cancellationToken);
                status = toolResult.Status;
                assistantText = toolResult.Message;
                await WriteLogAsync(route, toolResult, cancellationToken);
                break;

            case IntentType.TroubleshootAudio:
                toolResult = await ExecuteToolAsync(route, cancellationToken);
                status = toolResult.Status;
                assistantText = toolResult.Message;
                await WriteLogAsync(route, toolResult, cancellationToken);
                break;

            case IntentType.LaunchGame:
                toolResult = await ExecuteToolAsync(route, cancellationToken);
                status = toolResult.Status;
                assistantText = toolResult.Message;
                await WriteLogAsync(route, toolResult, cancellationToken);
                break;

            case IntentType.GetRemainingTime:
                toolResult = await ExecuteToolAsync(route, cancellationToken);
                status = toolResult.Status;
                assistantText = toolResult.Message;
                await WriteLogAsync(route, toolResult, cancellationToken);
                break;

            case IntentType.CancelCurrentAction:
                _cart.ClearPending();
                toolResult = await ExecuteToolAsync(route, cancellationToken);
                status = AgentStatus.Cancelled;
                assistantText = toolResult.Message;
                await WriteLogAsync(route, toolResult, cancellationToken);
                break;

            default:
                status = AgentStatus.NeedsClarification;
                assistantText = "\uC544\uC9C1 \uCC98\uB9AC\uD560 \uC218 \uC5C6\uB294 \uBA85\uB839\uC785\uB2C8\uB2E4. \uBA54\uB274 \uC8FC\uBB38, \uC9C1\uC6D0 \uD638\uCD9C, \uC18C\uB9AC \uBB38\uC81C, \uB864 \uC2E4\uD589, \uB0A8\uC740 \uC2DC\uAC04 \uC870\uD68C\uB97C \uB9D0\uD574 \uC8FC\uC138\uC694.";
                break;
        }

        return new AgentResponse(
            route.UserText,
            route,
            status,
            assistantText,
            _cart.Snapshot,
            toolResult,
            route.Candidates,
            confirmationText);
    }

    private async Task WriteLogAsync(IntentRoute route, ToolResult toolResult, CancellationToken cancellationToken)
    {
        await _actionLog.AppendAsync(new ActionLogEntry(
            DateTimeOffset.Now,
            _seatNumber,
            route.UserText,
            route.Intent.ToString(),
            toolResult.Status.ToString(),
            toolResult.ToolName,
            toolResult.Message,
            toolResult.Payload), cancellationToken);
    }

    private Task<ToolResult> ExecuteToolAsync(IntentRoute route, CancellationToken cancellationToken)
    {
        return _tools.ExecuteAsync(new HermesSkillToolContext(_seatNumber, route, _cart.Snapshot), cancellationToken);
    }

    private string BuildConfirmationText(CartSnapshot cart)
    {
        var lines = cart.Lines.Select(line =>
            $"{line.Item.Name} x {line.Quantity} | \uB2E8\uAC00 {line.Item.Price.ToString("N0", CultureInfo.InvariantCulture)}\uC6D0 | \uAE08\uC561 {line.Total.ToString("N0", CultureInfo.InvariantCulture)}\uC6D0");

        return $"\uC88C\uC11D {_seatNumber}\n" +
               string.Join("\n", lines) +
               $"\n\uCD1D\uC561 {cart.Total.ToString("N0", CultureInfo.InvariantCulture)}\uC6D0";
    }
}
