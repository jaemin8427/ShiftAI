using System.Speech.Synthesis;

namespace ShiftAI.App;

public sealed class SpeechFeedbackService : IDisposable
{
    private readonly SpeechSynthesizer _synthesizer = new();

    public SpeechFeedbackService()
    {
        _synthesizer.SetOutputToDefaultAudioDevice();
        _synthesizer.Rate = 1;
        _synthesizer.Volume = 85;
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _synthesizer.SpeakAsyncCancelAll();
        _synthesizer.SpeakAsync(text);
    }

    public void Dispose()
    {
        _synthesizer.Dispose();
    }
}
