namespace ShiftAI.Core;

public sealed record HermesSkillToolContext(
    int SeatNumber,
    IntentRoute Route,
    CartSnapshot Cart);

public sealed class HermesSkillTool
{
    private readonly Func<HermesSkillToolContext, CancellationToken, Task<ToolResult>> _executeAsync;

    public HermesSkillTool(
        string name,
        string description,
        Func<HermesSkillToolContext, CancellationToken, Task<ToolResult>> executeAsync)
    {
        Name = name;
        Description = description;
        _executeAsync = executeAsync;
    }

    public string Name { get; }
    public string Description { get; }

    public Task<ToolResult> ExecuteAsync(HermesSkillToolContext context, CancellationToken cancellationToken = default)
    {
        return _executeAsync(context, cancellationToken);
    }
}

public sealed class HermesSkillToolRegistry
{
    private readonly Dictionary<IntentType, HermesSkillTool> _tools = new();

    public IReadOnlyCollection<HermesSkillTool> Tools => _tools.Values;

    public void Register(IntentType intent, HermesSkillTool tool)
    {
        _tools[intent] = tool;
    }

    public bool CanExecute(IntentType intent)
    {
        return _tools.ContainsKey(intent);
    }

    public Task<ToolResult> ExecuteAsync(HermesSkillToolContext context, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(context.Route.Intent, out var tool))
        {
            return Task.FromResult(new ToolResult(
                "unknown",
                AgentStatus.NeedsClarification,
                "등록되지 않은 Hermes 도구입니다."));
        }

        return tool.ExecuteAsync(context, cancellationToken);
    }

    public static HermesSkillToolRegistry FromAdapter(IPcCafeAdapter adapter)
    {
        var registry = new HermesSkillToolRegistry();

        registry.Register(IntentType.AddFood, new HermesSkillTool(
            "orderFood",
            "현재 장바구니를 PC방 음식 주문 시스템에 담습니다.",
            (context, cancellationToken) => adapter.OrderFoodAsync(context.SeatNumber, context.Cart, cancellationToken)));

        registry.Register(IntentType.PlaceOrder, new HermesSkillTool(
            "orderFood",
            "확정 대기 중인 장바구니를 PC방 음식 주문 시스템에 담습니다.",
            (context, cancellationToken) => adapter.OrderFoodAsync(context.SeatNumber, context.Cart, cancellationToken)));

        registry.Register(IntentType.BrowseMenu, new HermesSkillTool(
            "openFoodSearch",
            "지정한 키워드로 PC방 음식 검색 화면을 열어 사용자가 직접 고르게 합니다.",
            (context, cancellationToken) => adapter.OpenFoodSearchAsync(
                context.SeatNumber,
                context.Route.Keyword ?? context.Route.UserText,
                cancellationToken)));

        registry.Register(IntentType.CallStaff, new HermesSkillTool(
            "callStaff",
            "좌석으로 직원을 호출합니다.",
            (context, cancellationToken) => adapter.CallStaffAsync(context.SeatNumber, cancellationToken)));

        registry.Register(IntentType.TroubleshootAudio, new HermesSkillTool(
            "troubleshootAudio",
            "좌석 오디오 문제 해결을 시작합니다.",
            (context, cancellationToken) => adapter.TroubleshootAudioAsync(context.SeatNumber, cancellationToken)));

        registry.Register(IntentType.LaunchGame, new HermesSkillTool(
            "launchGame",
            "게임 실행을 요청합니다.",
            (context, cancellationToken) => adapter.LaunchGameAsync(
                context.SeatNumber,
                context.Route.GameName ?? "Unknown",
                cancellationToken)));

        registry.Register(IntentType.GetRemainingTime, new HermesSkillTool(
            "getRemainingTime",
            "좌석 남은 시간을 조회합니다.",
            (context, cancellationToken) => adapter.GetRemainingTimeAsync(context.SeatNumber, cancellationToken)));

        registry.Register(IntentType.CancelCurrentAction, new HermesSkillTool(
            "cancelCurrentAction",
            "현재 진행 중인 작업과 장바구니를 취소합니다.",
            (context, cancellationToken) => adapter.CancelCurrentActionAsync(context.SeatNumber, cancellationToken)));

        return registry;
    }
}
