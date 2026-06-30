using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ShiftAI.Core;

namespace ShiftAI.App;

public sealed class JsonlActionLog : IActionLog
{
    private readonly string _logPath;
    private readonly JsonSerializerOptions _options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public JsonlActionLog(string logPath)
    {
        _logPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
    }

    public async Task AppendAsync(ActionLogEntry entry, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(entry, _options);
        await File.AppendAllTextAsync(_logPath, json + Environment.NewLine, cancellationToken);
    }
}
