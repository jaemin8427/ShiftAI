using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace ShiftAI.App;

/// <summary>
/// Voice input using the built-in Windows speech recognizer (Windows.Media.SpeechRecognition).
/// Requires the Korean (ko-KR) speech recognizer to be installed. No model download needed, but
/// Korean accuracy is generally lower than Whisper for short PC-bang commands.
/// </summary>
public sealed class WindowsSpeechInputService : IVoiceInputService
{
    public static bool IsKoreanAvailable()
    {
        try
        {
            static bool IsKorean(Language language) =>
                language.LanguageTag.StartsWith("ko", StringComparison.OrdinalIgnoreCase);

            return SpeechRecognizer.SupportedTopicLanguages.Any(IsKorean)
                || SpeechRecognizer.SupportedGrammarLanguages.Any(IsKorean);
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> ListenOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var recognizer = new SpeechRecognizer(new Language("ko-KR"));
            var compiled = await recognizer.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
            {
                return "";
            }

            var result = await recognizer.RecognizeAsync();
            if (result is null || result.Status != SpeechRecognitionResultStatus.Success)
            {
                return "";
            }

            return (result.Text ?? "").Trim();
        }
        catch
        {
            // Missing recognizer / mic / privacy setting -> fall back to the next provider.
            return "";
        }
    }
}
