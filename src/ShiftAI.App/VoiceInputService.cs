namespace ShiftAI.App;

public interface IVoiceInputService
{
    Task<string> ListenOnceAsync(CancellationToken cancellationToken = default);
}

public sealed class DemoVoiceInputService : IVoiceInputService
{
    public Task<string> ListenOnceAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult("콜라 하나 추가해");
    }
}

public sealed class FallbackVoiceInputService : IVoiceInputService
{
    private readonly IVoiceInputService _primary;
    private readonly IVoiceInputService _fallback;

    public FallbackVoiceInputService(IVoiceInputService primary, IVoiceInputService fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public async Task<string> ListenOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _primary.ListenOnceAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(result)
                ? await _fallback.ListenOnceAsync(cancellationToken)
                : result;
        }
        catch
        {
            return await _fallback.ListenOnceAsync(cancellationToken);
        }
    }
}
