using System.Text.RegularExpressions;

namespace ShiftAI.Core;

public sealed class MenuMatcher
{
    private readonly IReadOnlyList<MenuItem> _menu;

    public MenuMatcher(IReadOnlyList<MenuItem> menu)
    {
        _menu = menu;
    }

    public IReadOnlyList<MenuItem> FindCandidates(string text)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var directMatches = _menu
            .Where(item => normalized.Contains(Normalize(item.Name)) || Normalize(item.Name).Contains(normalized))
            .ToList();

        if (directMatches.Count > 0)
        {
            return directMatches;
        }

        if (normalized.Contains("\uB77C\uBA74") || normalized.Contains("\uB77C\uBA58") || normalized.Contains("\uBA74"))
        {
            return _menu.Where(item => item.Name is "\uB77C\uACF5\uD0C4" or "\uB77C\uC6B0\uB3D9" or "\uC77C\uBC18 \uB77C\uBA74").ToList();
        }

        if (normalized.Contains("\uCF5C\uB77C") || normalized.Contains("\uCF54\uCE74"))
        {
            return _menu.Where(item => item.Name == "\uCF5C\uB77C").ToList();
        }

        if (normalized.Contains("\uC544\uC774\uC2A4\uD2F0") || normalized.Contains("\uC544\uD2F0"))
        {
            return _menu.Where(item => item.Name == "\uC544\uC774\uC2A4\uD2F0").ToList();
        }

        if (normalized.Contains("\uBCF6\uC74C\uBC25") || normalized.Contains("\uBC25"))
        {
            return _menu.Where(item => item.Name == "\uAE40\uCE58\uBCF6\uC74C\uBC25").ToList();
        }

        if (normalized.Contains("\uD56B\uB3C4\uADF8") || normalized.Contains("\uD56B\uB3C5"))
        {
            return _menu.Where(item => item.Name == "\uD56B\uB3C4\uADF8").ToList();
        }

        return [];
    }

    public MenuItem? FindByName(string text)
    {
        var normalized = Normalize(text);
        return _menu.FirstOrDefault(item => Normalize(item.Name) == normalized)
            ?? _menu.FirstOrDefault(item => normalized.Contains(Normalize(item.Name)));
    }

    public static int ExtractQuantity(string text)
    {
        var normalized = Normalize(text);
        var digitMatch = Regex.Match(normalized, @"(\d+)");
        if (digitMatch.Success && int.TryParse(digitMatch.Groups[1].Value, out var digitQuantity))
        {
            return Math.Max(1, digitQuantity);
        }

        var words = new Dictionary<string, int>
        {
            ["\uD558\uB098"] = 1,
            ["\uD55C\uAC1C"] = 1,
            ["\uD55C\uC794"] = 1,
            ["\uD55C\uADF8\uB987"] = 1,
            ["\uB458"] = 2,
            ["\uB450\uAC1C"] = 2,
            ["\uB450\uC794"] = 2,
            ["\uB450\uADF8\uB987"] = 2,
            ["\uC14B"] = 3,
            ["\uC138\uAC1C"] = 3,
            ["\uC138\uC794"] = 3,
            ["\uC138\uADF8\uB987"] = 3
        };

        foreach (var pair in words)
        {
            if (normalized.Contains(pair.Key))
            {
                return pair.Value;
            }
        }

        return 1;
    }

    private static string Normalize(string text)
    {
        return new string(text
            .Where(ch => !char.IsWhiteSpace(ch) && ch != '?' && ch != '!' && ch != '.')
            .ToArray())
            .Trim()
            .ToLowerInvariant();
    }
}
