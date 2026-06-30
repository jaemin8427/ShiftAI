using System.IO;

namespace ShiftAI.App;

public static class GeminiKeyProvider
{
    private const string KeyFileName = "shiftaikey.txt";

    public static string? GetApiKey()
    {
        var environmentKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            return environmentKey.Trim();
        }

        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var keyPath = Path.Combine(documentsPath, KeyFileName);
        if (!File.Exists(keyPath))
        {
            return null;
        }

        var fileKey = File.ReadAllText(keyPath).Trim();
        return string.IsNullOrWhiteSpace(fileKey) ? null : fileKey;
    }
}
