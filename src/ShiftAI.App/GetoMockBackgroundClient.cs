using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using ShiftAI.Core;

namespace ShiftAI.App;

public sealed class GetoMockBackgroundClient
{
    private const string PipeName = "ShiftAI.GetoMock";

    public async Task<GetoAutomationResult> AddCartItemsAsync(CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(350, cancellationToken);

            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);

            var command = new BackgroundOrderCommand(
                "add-items",
                cart.Lines.Select(line => new BackgroundOrderItem(line.Item.Name, line.Item.Price, line.Quantity)).ToList());

            await writer.WriteLineAsync(JsonSerializer.Serialize(command));
            var responseLine = await reader.ReadLineAsync(cancellationToken);
            var response = string.IsNullOrWhiteSpace(responseLine)
                ? null
                : JsonSerializer.Deserialize<BackgroundOrderResponse>(responseLine);

            return response?.Success == true
                ? new GetoAutomationResult(true, response.Message)
                : new GetoAutomationResult(false, response?.Message ?? "Geto Mock background response was empty.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            return new GetoAutomationResult(false, $"Geto Mock background channel unavailable: {ex.Message}");
        }
    }

    private sealed record BackgroundOrderCommand(string Type, IReadOnlyList<BackgroundOrderItem> Items);
    private sealed record BackgroundOrderItem(string Name, int Price, int Quantity);
    private sealed record BackgroundOrderResponse(bool Success, string Message);
}
