namespace ShiftAI.Core;

public enum IntentType
{
    Unknown,
    AddFood,
    PlaceOrder,
    BrowseMenu,
    CallStaff,
    TroubleshootAudio,
    LaunchGame,
    GetRemainingTime,
    CancelCurrentAction,
    ClarifyMenuItem
}

public enum AgentStatus
{
    Idle,
    AwaitingConfirmation,
    Completed,
    Cancelled,
    NeedsClarification
}

public sealed record MenuItem(string Id, string Name, int Price);

public sealed record CartLine(MenuItem Item, int Quantity)
{
    public int Total => Item.Price * Quantity;
}

public sealed record CartSnapshot(IReadOnlyList<CartLine> Lines)
{
    public int Total => Lines.Sum(line => line.Total);
    public bool IsEmpty => Lines.Count == 0;
}

public sealed record IntentRoute(
    IntentType Intent,
    string UserText,
    int Quantity = 1,
    MenuItem? MenuItem = null,
    IReadOnlyList<MenuItem>? Candidates = null,
    string? GameName = null,
    string? Reason = null,
    string? Keyword = null,
    bool UsedLlm = false);

public sealed record ToolResult(
    string ToolName,
    AgentStatus Status,
    string Message,
    object? Payload = null);

public sealed record AgentResponse(
    string UserText,
    IntentRoute Route,
    AgentStatus Status,
    string AssistantText,
    CartSnapshot Cart,
    ToolResult? ToolResult = null,
    IReadOnlyList<MenuItem>? Candidates = null,
    string? ConfirmationText = null);

public sealed record ActionLogEntry(
    DateTimeOffset Timestamp,
    int SeatNumber,
    string UserText,
    string Intent,
    string Status,
    string ToolName,
    string Message,
    object? Payload);

public interface IActionLog
{
    Task AppendAsync(ActionLogEntry entry, CancellationToken cancellationToken = default);
}

public interface IPcCafeAdapter
{
    Task<ToolResult> OrderFoodAsync(int seatNumber, CartSnapshot cart, CancellationToken cancellationToken = default);
    Task<ToolResult> OpenFoodSearchAsync(int seatNumber, string keyword, CancellationToken cancellationToken = default);
    Task<ToolResult> CallStaffAsync(int seatNumber, CancellationToken cancellationToken = default);
    Task<ToolResult> TroubleshootAudioAsync(int seatNumber, CancellationToken cancellationToken = default);
    Task<ToolResult> LaunchGameAsync(int seatNumber, string gameName, CancellationToken cancellationToken = default);
    Task<ToolResult> GetRemainingTimeAsync(int seatNumber, CancellationToken cancellationToken = default);
    Task<ToolResult> CancelCurrentActionAsync(int seatNumber, CancellationToken cancellationToken = default);
}
