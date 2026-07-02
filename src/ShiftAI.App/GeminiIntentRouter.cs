using System.Net.Http;
using System.Text;
using System.Text.Json;
using ShiftAI.Core;

namespace ShiftAI.App;

public sealed class GeminiIntentRouter : IIntentRouter
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly IReadOnlyList<MenuItem> _menu;
    private readonly MenuMatcher _matcher;
    private readonly HermesAgentSettings _settings;
    private readonly Func<string> _personaProvider;

    public GeminiIntentRouter(
        HttpClient httpClient,
        string apiKey,
        IReadOnlyList<MenuItem> menu,
        HermesAgentSettings settings,
        Func<string>? personaProvider = null)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _menu = menu;
        _matcher = new MenuMatcher(menu);
        _settings = settings;
        _personaProvider = personaProvider ?? (() => "standard");
    }

    public async Task<IntentRoute> RouteAsync(string text, CartSnapshot cart, bool awaitingConfirmation, CancellationToken cancellationToken = default)
    {
        var menuText = string.Join(", ", _menu.Select(item => $"{item.Name}:{item.Price}"));
        var policyText = string.Join("\n- ", _settings.OrderingPolicy);
        var personaPrompt = BuildPersonaPrompt(_personaProvider());
        var prompt =
            $"You are {_settings.AgentName}, the LLM routing brain for {_settings.ProductName}.\n" +
            "You are an intent classifier for a Korean PC cafe seat agent.\n" +
            "Persona mode is used for understanding user tone, but your output must still be strict JSON.\n" +
            $"Persona:\n{personaPrompt}\n" +
            "Return strict JSON only with this schema:\n" +
            "{\"intent\":\"AddFood|PlaceOrder|CallStaff|TroubleshootAudio|LaunchGame|GetRemainingTime|CancelCurrentAction|Unknown\",\"menuName\":null|string,\"quantity\":number,\"gameName\":null|string}\n" +
            "Policy:\n" +
            $"- {policyText}\n" +
            "For \"\uB77C\uBA74\", \"\uB77C\uBA58\", or generic \"\uBA74\" requests, return AddFood with menuName null so the app can ask clarification.\n" +
            "Do not place food orders from add-food commands. Only classify.\n" +
            $"Menu: {menuText}\n" +
            $"Awaiting confirmation: {awaitingConfirmation}\n" +
            $"User: {text}";

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{_settings.Model}:generateContent");
        request.Headers.Add("x-goog-api-key", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            }
        }), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        var modelText = ExtractModelText(responseText);
        var parsed = JsonSerializer.Deserialize<LlmRoute>(CleanJson(modelText), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (parsed is null || !Enum.TryParse<IntentType>(parsed.Intent, out var intent))
        {
            return new IntentRoute(IntentType.Unknown, text);
        }

        var item = string.IsNullOrWhiteSpace(parsed.MenuName) ? null : _matcher.FindByName(parsed.MenuName);
        var candidates = intent == IntentType.AddFood && item is null ? _matcher.FindCandidates(text) : null;
        if (intent == IntentType.AddFood && item is null && candidates?.Count > 1)
        {
            return new IntentRoute(IntentType.ClarifyMenuItem, text, Math.Max(1, parsed.Quantity), Candidates: candidates);
        }

        return new IntentRoute(intent, text, Math.Max(1, parsed.Quantity), item, GameName: parsed.GameName);
    }

    private static string BuildPersonaPrompt(string persona)
    {
        return persona switch
        {
            "gamer" =>
                "- Mode name: 페르소나: 활기차지만 정중한 게이머 친화 모드\n" +
                "- Understand gaming slang and short commands naturally.\n" +
                "- Be upbeat and quick, but never childish or noisy.\n" +
                "- Keep food-order safety rules strict even when the user sounds casual.",
            "focus" =>
                "- Mode name: 페르소나: 간결한 집중 모드\n" +
                "- Prefer short, direct, low-distraction interpretation.\n" +
                "- Avoid chatter. Prioritize the next required action.\n" +
                "- Keep confirmations minimal and precise.",
            _ =>
                "- Mode name: 페르소나: 정중하고 차분한 안내\n" +
                "- Interpret Korean PC cafe requests politely and calmly.\n" +
                "- Use neutral service language.\n" +
                "- Ask for clarification when commands are ambiguous."
        };
    }

    private static string ExtractModelText(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        return document.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "{}";
    }

    private static string CleanJson(string text)
    {
        return text.Replace("```json", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private sealed record LlmRoute(string Intent, string? MenuName, int Quantity, string? GameName);
}
