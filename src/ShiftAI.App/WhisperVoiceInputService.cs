using System.Text;
using System.IO;
using NAudio.Wave;
using Whisper.net;

namespace ShiftAI.App;

public sealed class WhisperVoiceInputService : IVoiceInputService
{
    private readonly string _modelPath;
    private readonly TimeSpan _recordDuration;

    public WhisperVoiceInputService(string modelPath, TimeSpan? recordDuration = null)
    {
        _modelPath = modelPath;
        _recordDuration = recordDuration ?? TimeSpan.FromSeconds(4);
    }

    public async Task<string> ListenOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_modelPath))
        {
            return "";
        }

        var wavPath = Path.Combine(Path.GetTempPath(), $"shift-ai-voice-{Guid.NewGuid():N}.wav");
        try
        {
            await RecordWavAsync(wavPath, cancellationToken);
            return await TranscribeAsync(wavPath, cancellationToken);
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    private async Task RecordWavAsync(string wavPath, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 80
        };
        await using var writer = new WaveFileWriter(wavPath, waveIn.WaveFormat);

        waveIn.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);
                writer.Flush();
            }
        };
        waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                completion.TrySetException(e.Exception);
            }
            else
            {
                completion.TrySetResult();
            }
        };

        waveIn.StartRecording();
        try
        {
            await Task.Delay(_recordDuration, cancellationToken);
        }
        finally
        {
            waveIn.StopRecording();
        }

        await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task<string> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();

        using var whisperFactory = WhisperFactory.FromPath(_modelPath);
        using var processor = whisperFactory.CreateBuilder()
            .WithLanguage("ko")
            .Build();

        await using var fileStream = File.OpenRead(wavPath);
        await foreach (var result in processor.ProcessAsync(fileStream, cancellationToken))
        {
            builder.Append(result.Text);
        }

        return builder.ToString().Trim();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
