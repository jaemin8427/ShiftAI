using System.IO;
using System.Text.Json;

namespace ShiftAI.App;

public sealed record HermesAgentSettings(
    string AgentName,
    string ProductName,
    int SeatNumber,
    string LlmProvider,
    string Model,
    string Locale,
    IReadOnlyList<string> Scope,
    IReadOnlyList<string> OrderingPolicy)
{
    public static HermesAgentSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return Default;
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<HermesAgentSettings>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? Default;
    }

    public static HermesAgentSettings Default { get; } = new(
        "Hermes Agent",
        "Shift AI",
        38,
        "Gemini",
        "gemini-3.5-flash",
        "ko-KR",
        ["pc_cafe_seat_agent"],
        [
            "Never place a food order from an add-food command.",
            "Only order when confirmation is pending."
        ]);
}
