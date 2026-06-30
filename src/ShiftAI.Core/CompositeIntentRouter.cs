namespace ShiftAI.Core;

public sealed class CompositeIntentRouter : IIntentRouter
{
    private readonly IIntentRouter _localRouter;
    private readonly IIntentRouter? _llmRouter;

    public CompositeIntentRouter(IIntentRouter localRouter, IIntentRouter? llmRouter)
    {
        _localRouter = localRouter;
        _llmRouter = llmRouter;
    }

    public async Task<IntentRoute> RouteAsync(string text, CartSnapshot cart, bool awaitingConfirmation, CancellationToken cancellationToken = default)
    {
        var localRoute = await _localRouter.RouteAsync(text, cart, awaitingConfirmation, cancellationToken);
        if (localRoute.Intent != IntentType.Unknown)
        {
            return localRoute;
        }

        if (_llmRouter is not null)
        {
            try
            {
                var llmRoute = await _llmRouter.RouteAsync(text, cart, awaitingConfirmation, cancellationToken);
                if (llmRoute.Intent != IntentType.Unknown && llmRoute.Intent != IntentType.ClarifyMenuItem)
                {
                    return llmRoute with { UsedLlm = true };
                }
            }
            catch
            {
                // Local routing keeps the kiosk usable if the network or LLM fails.
            }
        }

        return localRoute;
    }
}
