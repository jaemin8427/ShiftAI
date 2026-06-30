using ShiftAI.Core;

namespace ShiftAI.App;

public sealed class MockPcCafeAdapter : IPcCafeAdapter
{
    private readonly GetoMockBackgroundClient _backgroundClient = new();
    private readonly GetoMockAutomation _getoMockAutomation = new();

    public async Task<ToolResult> OrderFoodAsync(int seatNumber, CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        var getoResult = await _backgroundClient.AddCartItemsAsync(cart, cancellationToken);
        var mode = "background";
        if (!getoResult.Success)
        {
            getoResult = await _getoMockAutomation.AddCartItemsAsync(cart, cancellationToken);
            mode = "vision-fallback";
        }

        var payload = new
        {
            seatNumber,
            items = cart.Lines.Select(line => new
            {
                name = line.Item.Name,
                quantity = line.Quantity,
                price = line.Item.Price,
                total = line.Total
            }),
            totalAmount = cart.Total,
            simulated = true,
            mode,
            getoMock = getoResult
        };

        var message = getoResult.Success
            ? mode == "background"
                ? $"\uC88C\uC11D {seatNumber}\uBC88 Geto Mock\uC5D0 \uBC31\uADF8\uB77C\uC6B4\uB4DC\uB85C \uB2F4\uC558\uC2B5\uB2C8\uB2E4. \uCD1D\uC561 {cart.Total:N0}\uC6D0\uC785\uB2C8\uB2E4."
                : $"\uC88C\uC11D {seatNumber}\uBC88 Geto Mock \uC7A5\uBC14\uAD6C\uB2C8\uC5D0 \uC190\uC73C\uB85C \uB2F4\uC558\uC2B5\uB2C8\uB2E4. \uCD1D\uC561 {cart.Total:N0}\uC6D0\uC785\uB2C8\uB2E4."
            : $"\uC8FC\uBB38\uC740 \uD655\uC778\uD588\uC9C0\uB9CC Geto Mock \uD074\uB9AD\uC740 \uC2E4\uD328\uD588\uC2B5\uB2C8\uB2E4. {getoResult.Message}";

        return new ToolResult(
            "orderFood",
            getoResult.Success ? AgentStatus.Completed : AgentStatus.NeedsClarification,
            message,
            payload);
    }

    public Task<ToolResult> CallStaffAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult(
            "callStaff",
            AgentStatus.Completed,
            $"\uC88C\uC11D {seatNumber}\uBC88\uC73C\uB85C \uC9C1\uC6D0\uC744 \uD638\uCD9C\uD55C \uAC83\uC73C\uB85C \uC2DC\uBBAC\uB808\uC774\uC158\uD588\uC2B5\uB2C8\uB2E4.",
            new { seatNumber, simulated = true }));
    }

    public Task<ToolResult> TroubleshootAudioAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult(
            "troubleshootAudio",
            AgentStatus.Completed,
            "Windows \uAE30\uBCF8 \uCD9C\uB825 \uC7A5\uCE58, \uBCFC\uB968, \uAC8C\uC784 \uC0AC\uC6B4\uB4DC \uC124\uC815\uC744 \uC810\uAC80\uD55C \uAC83\uC73C\uB85C \uC2DC\uBBAC\uB808\uC774\uC158\uD588\uC2B5\uB2C8\uB2E4.",
            new { seatNumber, checks = new[] { "default_output_device", "master_volume", "game_audio" }, simulated = true }));
    }

    public Task<ToolResult> LaunchGameAsync(int seatNumber, string gameName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult(
            "launchGame",
            AgentStatus.Completed,
            $"{gameName} \uC2E4\uD589\uC744 \uC694\uCCAD\uD55C \uAC83\uC73C\uB85C \uC2DC\uBBAC\uB808\uC774\uC158\uD588\uC2B5\uB2C8\uB2E4.",
            new { seatNumber, gameName, simulated = true }));
    }

    public Task<ToolResult> GetRemainingTimeAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult(
            "getRemainingTime",
            AgentStatus.Completed,
            $"\uC88C\uC11D {seatNumber}\uBC88\uC758 \uB0A8\uC740 \uC2DC\uAC04\uC740 1\uC2DC\uAC04 42\uBD84\uC73C\uB85C \uC2DC\uBBAC\uB808\uC774\uC158\uD588\uC2B5\uB2C8\uB2E4.",
            new { seatNumber, remainingMinutes = 102, simulated = true }));
    }

    public Task<ToolResult> CancelCurrentActionAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult(
            "cancelCurrentAction",
            AgentStatus.Cancelled,
            "\uC9C4\uD589 \uC911\uC778 \uC791\uC5C5\uACFC \uC7A5\uBC14\uAD6C\uB2C8\uB97C \uCDE8\uC18C\uD588\uC2B5\uB2C8\uB2E4.",
            new { seatNumber, simulated = true }));
    }
}
