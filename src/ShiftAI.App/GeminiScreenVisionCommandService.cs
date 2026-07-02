using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ShiftAI.Core;

namespace ShiftAI.App;

public sealed class GeminiScreenVisionCommandService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _root;
    private readonly IReadOnlyList<MenuItem> _menu;

    public GeminiScreenVisionCommandService(
        HttpClient httpClient,
        string apiKey,
        string model,
        string root,
        IReadOnlyList<MenuItem> menu)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _model = model;
        _root = root;
        _menu = menu;
    }

    public async Task<ScreenVisionCommandResult> ReadCommandAsync(CancellationToken cancellationToken = default)
    {
        var capturePath = CaptureScreenToFile();
        try
        {
            var imageBytes = await File.ReadAllBytesAsync(capturePath, cancellationToken);
            var imageBase64 = Convert.ToBase64String(imageBytes);
            var menuText = string.Join(", ", _menu.Select(item => $"{item.Name}:{item.Price}"));
            var prompt =
                "You are the screen-reading command brain for a Korean PC cafe seat assistant.\n" +
                "Read the screenshot text and visible UI. Decide one actionable user command for Shift AI.\n" +
                "Return strict JSON only with this schema: {\"command\":null|string,\"reason\":string}\n" +
                "Allowed command meanings: food order/add item, staff call, audio troubleshoot, launch League of Legends, remaining time, cancel.\n" +
                "If a visible menu item looks selected or clearly intended, return a Korean command like \"콜라 하나 추가해\".\n" +
                "If League of Legends or LoL launch is visible or intended, return \"롤 실행해\".\n" +
                "If the screen is ambiguous, return command null.\n" +
                $"Known food menu: {menuText}";

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent");
            request.Headers.Add("x-goog-api-key", _apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = "image/png",
                                    data = imageBase64
                                }
                            }
                        }
                    }
                }
            }), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var modelText = ExtractModelText(responseText);
            var parsed = JsonSerializer.Deserialize<VisionRoute>(CleanJson(modelText), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var command = parsed?.Command?.Trim() ?? "";
            var reason = parsed?.Reason?.Trim() ?? "화면 판독 결과가 비어 있습니다.";
            return new ScreenVisionCommandResult(
                !string.IsNullOrWhiteSpace(command),
                command,
                reason,
                capturePath,
                modelText);
        }
        catch (Exception ex)
        {
            return new ScreenVisionCommandResult(
                false,
                "",
                $"화면 판독에 실패했습니다. {ex.Message}",
                capturePath,
                null);
        }
    }

    private string CaptureScreenToFile()
    {
        TrySetDpiAware();
        var bounds = GetVirtualScreenBounds();
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        }

        var directory = Path.Combine(_root, "logs", "vision");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-screen-vision.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static Rectangle GetVirtualScreenBounds()
    {
        var left = GetSystemMetrics(SystemMetricVirtualScreenX);
        var top = GetSystemMetrics(SystemMetricVirtualScreenY);
        var width = GetSystemMetrics(SystemMetricVirtualScreenWidth);
        var height = GetSystemMetrics(SystemMetricVirtualScreenHeight);
        return new Rectangle(left, top, width, height);
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

    private static void TrySetDpiAware()
    {
        try
        {
            SetProcessDPIAware();
        }
        catch
        {
            // Best effort only.
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private const int SystemMetricVirtualScreenX = 76;
    private const int SystemMetricVirtualScreenY = 77;
    private const int SystemMetricVirtualScreenWidth = 78;
    private const int SystemMetricVirtualScreenHeight = 79;

    private sealed record VisionRoute(string? Command, string? Reason);
}

public sealed record ScreenVisionCommandResult(
    bool Success,
    string Command,
    string Reason,
    string? CapturePath,
    string? RawText);
