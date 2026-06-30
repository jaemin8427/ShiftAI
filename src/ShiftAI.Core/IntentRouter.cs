namespace ShiftAI.Core;

public interface IIntentRouter
{
    Task<IntentRoute> RouteAsync(string text, CartSnapshot cart, bool awaitingConfirmation, CancellationToken cancellationToken = default);
}

public sealed class IntentRouter : IIntentRouter
{
    private readonly MenuMatcher _menuMatcher;

    public IntentRouter(MenuMatcher menuMatcher)
    {
        _menuMatcher = menuMatcher;
    }

    public Task<IntentRoute> RouteAsync(string text, CartSnapshot cart, bool awaitingConfirmation, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(text);
        var quantity = MenuMatcher.ExtractQuantity(text);

        if (ContainsAny(normalized, "\uCDE8\uC18C", "\uADF8\uB9CC", "\uCDE8\uC18C\uD574"))
        {
            return Task.FromResult(new IntentRoute(IntentType.CancelCurrentAction, text, quantity));
        }

        if (ContainsAny(normalized, "\uC8FC\uBB38\uD574", "\uACB0\uC81C\uD574", "\uD655\uC815", "\uD655\uC778"))
        {
            return Task.FromResult(new IntentRoute(IntentType.PlaceOrder, text, quantity));
        }

        if (ContainsAny(normalized, "\uC9C1\uC6D0", "\uC54C\uBC14", "\uC0AC\uC7A5", "\uBD88\uB7EC"))
        {
            return Task.FromResult(new IntentRoute(IntentType.CallStaff, text, quantity));
        }

        if (ContainsAny(normalized, "\uC18C\uB9AC", "\uC624\uB514\uC624", "\uD5E4\uB4DC\uC14B", "\uC2A4\uD53C\uCEE4", "\uC548\uB098\uC640", "\uC548\uB4E4\uB824"))
        {
            return Task.FromResult(new IntentRoute(IntentType.TroubleshootAudio, text, quantity));
        }

        if (ContainsAny(normalized, "\uC5BC\uB9C8\uB098\uB0A8", "\uB0A8\uC558", "\uB0A8\uC740\uC2DC\uAC04", "\uC2DC\uAC04"))
        {
            return Task.FromResult(new IntentRoute(IntentType.GetRemainingTime, text, quantity));
        }

        if (ContainsAny(normalized, "\uB864", "\uB9AC\uADF8\uC624\uBE0C\uB808\uC804\uB4DC", "leagueoflegends"))
        {
            return Task.FromResult(new IntentRoute(IntentType.LaunchGame, text, quantity, GameName: "League of Legends"));
        }

        var candidates = _menuMatcher.FindCandidates(text);
        if (candidates.Count == 1)
        {
            return Task.FromResult(new IntentRoute(IntentType.AddFood, text, quantity, MenuItem: candidates[0]));
        }

        if (candidates.Count > 1)
        {
            return Task.FromResult(new IntentRoute(IntentType.ClarifyMenuItem, text, quantity, Candidates: candidates));
        }

        return Task.FromResult(new IntentRoute(IntentType.Unknown, text, quantity, Reason: "\uC9C0\uC6D0\uD558\uC9C0 \uC54A\uB294 \uBA85\uB839\uC785\uB2C8\uB2E4."));
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(text.Contains);
    }

    private static string Normalize(string text)
    {
        return new string(text.Where(ch => !char.IsWhiteSpace(ch) && ch != '?' && ch != '!' && ch != '.').ToArray())
            .ToLowerInvariant();
    }
}
