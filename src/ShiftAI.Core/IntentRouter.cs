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

    // Exact orderable items: any trigger phrase maps straight to an auto-order with a precise Geto search term.
    private static readonly (string[] Triggers, MenuItem Item)[] ExactItems =
    [
        (["\uC544\uC774\uC2A4\uC544\uBA54\uB9AC\uCE74\uB178", "\uC544\uC774\uC2A4\uC544\uBA54", "\uC544\uC544"], new MenuItem("iced-americano", "\uC544\uC774\uC2A4\uC544\uBA54\uB9AC\uCE74\uB178", 3000)),
        (["\uCF54\uCE74\uCF5C\uB77C", "\uCF5C\uB77C", "\uCF54\uCE74"], new MenuItem("coca-cola", "\uCF54\uCE74\uCF5C\uB77C", 2500)),
    ];

    // Category keywords: no single exact item -> open the Geto search screen filtered to the keyword and let the user pick.
    private static readonly string[] BrowseKeywords =
    [
        "\uB5A1\uBCF6\uC774", "\uBCF6\uC74C\uBC25", "\uD56B\uB3C4\uADF8", "\uAE40\uBC25", "\uB3C4\uC2DC\uB77D", "\uD584\uBC84\uAC70", "\uC544\uBA54\uB9AC\uCE74\uB178",
        "\uB77C\uBA74", "\uCEE4\uD53C", "\uCE58\uD0A8", "\uD53C\uC790", "\uBD84\uC2DD", "\uAC04\uC2DD", "\uACFC\uC790", "\uC74C\uB8CC", "\uC0AC\uC774\uB2E4", "\uCE74\uD398", "\uB77C\uBA58"
    ];

    public Task<IntentRoute> RouteAsync(string text, CartSnapshot cart, bool awaitingConfirmation, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(text);
        var quantity = MenuMatcher.ExtractQuantity(text);

        if (ContainsAny(normalized, "\uCDE8\uC18C", "\uADF8\uB9CC", "\uCDE8\uC18C\uD574"))
        {
            return Task.FromResult(new IntentRoute(IntentType.CancelCurrentAction, text, quantity));
        }

        if (ContainsAny(normalized, "\uC9C1\uC6D0", "\uC54C\uBC14", "\uC0AC\uC7A5", "\uBD88\uB7EC"))
        {
            return Task.FromResult(new IntentRoute(IntentType.CallStaff, text, quantity));
        }

        if (ContainsAny(normalized, "\uC18C\uB9AC", "\uC624\uB514\uC624", "\uD5E4\uB4DC\uC14B", "\uC2A4\uD53C\uCEE4", "\uC548\uB098\uC640", "\uC548\uB4E4\uB824"))
        {
            return Task.FromResult(new IntentRoute(IntentType.TroubleshootAudio, text, quantity));
        }

        if (ContainsAny(normalized, "\uC5BC\uB9C8\uB098\uB0A8", "\uB0A8\uC558", "\uB0A8\uC740\uC2DC\uAC04"))
        {
            return Task.FromResult(new IntentRoute(IntentType.GetRemainingTime, text, quantity));
        }

        if (ContainsAny(normalized, "\uB864", "\uB9AC\uADF8\uC624\uBE0C\uB808\uC804\uB4DC", "leagueoflegends"))
        {
            return Task.FromResult(new IntentRoute(IntentType.LaunchGame, text, quantity, GameName: "League of Legends"));
        }

        // Exact orderable item (e.g. \uCF5C\uB77C -> \uCF54\uCE74\uCF5C\uB77C, \uC544\uC544 -> \uC544\uC774\uC2A4\uC544\uBA54\uB9AC\uCE74\uB178) -> auto order.
        foreach (var (triggers, item) in ExactItems)
        {
            if (triggers.Any(normalized.Contains))
            {
                return Task.FromResult(new IntentRoute(IntentType.AddFood, text, quantity, MenuItem: item));
            }
        }

        // Category keyword (\uB77C\uBA74, \uCEE4\uD53C ...) -> open the search screen for manual selection.
        var keyword = BrowseKeywords
            .Where(normalized.Contains)
            .OrderByDescending(word => word.Length)
            .FirstOrDefault();
        if (keyword is not null)
        {
            return Task.FromResult(new IntentRoute(IntentType.BrowseMenu, text, quantity, Keyword: keyword));
        }

        // Confirm a pending order (only when no food term was detected above).
        if (ContainsAny(normalized, "\uC8FC\uBB38\uD574", "\uACB0\uC81C\uD574", "\uD655\uC815", "\uD655\uC778"))
        {
            return Task.FromResult(new IntentRoute(IntentType.PlaceOrder, text, quantity));
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
