using System.Text.Encodings.Web;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace ShiftAI.App;

public partial class VoiceFloatingWindow : Window
{
    private const double CompactWidth = 220;
    private const double CompactHeight = 72;
    private const double ExpandedWidth = 350;
    private const double ExpandedHeight = 260;

    private bool _pttDown;
    private bool _webReady;
    private bool _expanded;
    private string _pendingEngineStatus = "STT";
    private string _pendingTranscript = "대기 중";

    public VoiceFloatingWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeWebViewAsync();
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? PttStarted;
    public event EventHandler? PttEnded;

    public void SetReady(string engineStatus)
    {
        _pendingEngineStatus = engineStatus;
        _pendingTranscript = $"듣기 준비됨 · {engineStatus}";
        _ = ExecuteVoiceScriptAsync($"window.shiftVoice?.setReady({Json(engineStatus)});");
    }

    public void SetListening()
    {
        _ = ExecuteVoiceScriptAsync("window.shiftVoice?.setListening();");
    }

    public void SetTranscript(string text)
    {
        _pendingTranscript = text;
        _ = ExecuteVoiceScriptAsync($"window.shiftVoice?.setTranscript({Json(text)});");
    }

    public void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 22;
        Top = area.Bottom - Height - 58;
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await VoiceWebView.EnsureCoreWebView2Async();
            VoiceWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            VoiceWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            VoiceWebView.CoreWebView2.WebMessageReceived += (_, args) => HandleWebMessage(args);
            VoiceWebView.NavigationCompleted += async (_, _) =>
            {
                _webReady = true;
                await ExecuteVoiceScriptAsync("window.shiftVoice?.setSeat('PC-38');");
                await ExecuteVoiceScriptAsync($"window.shiftVoice?.setReady({Json(_pendingEngineStatus)});");
                if (_pendingTranscript != $"듣기 준비됨 · {_pendingEngineStatus}")
                {
                    await ExecuteVoiceScriptAsync($"window.shiftVoice?.setTranscript({Json(_pendingTranscript)});");
                }
            };

            var widgetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "voice-widget.html");
            VoiceWebView.Source = new Uri(widgetPath);
        }
        catch
        {
            // The caller can still use keyboard shortcuts even if WebView2 is unavailable.
        }
    }

    private void HandleWebMessage(CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.WebMessageAsJson);
        if (!document.RootElement.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        switch (typeElement.GetString())
        {
            case "ptt-start":
                StartPtt();
                break;
            case "ptt-end":
                EndPtt();
                break;
            case "close":
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "toggle":
                ToggleExpanded();
                break;
            case "compact":
                SetExpanded(false);
                break;
            case "expand":
                SetExpanded(true);
                break;
            case "drag":
                TryDragMove();
                break;
        }
    }

    private void StartPtt()
    {
        if (_pttDown)
        {
            return;
        }

        _pttDown = true;
        PttStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndPtt()
    {
        if (!_pttDown)
        {
            return;
        }

        _pttDown = false;
        PttEnded?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteVoiceScriptAsync(string script)
    {
        if (!_webReady || VoiceWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await VoiceWebView.ExecuteScriptAsync(script);
        }
        catch
        {
            // Best-effort visual state sync only.
        }
    }

    private void ToggleExpanded()
    {
        SetExpanded(!_expanded);
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        var oldRight = Left + Width;
        var oldBottom = Top + Height;
        _expanded = expanded;
        Width = expanded ? ExpandedWidth : CompactWidth;
        Height = expanded ? ExpandedHeight : CompactHeight;
        Left = Math.Max(SystemParameters.WorkArea.Left, oldRight - Width);
        Top = Math.Max(SystemParameters.WorkArea.Top, oldBottom - Height);
        _ = ExecuteVoiceScriptAsync(expanded
            ? "window.shiftVoice?.setExpanded(true);"
            : "window.shiftVoice?.setExpanded(false);");
    }

    private static string Json(string value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private void TryDragMove()
    {
        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can throw when the pointer is no longer pressed.
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _ = ExecuteVoiceScriptAsync("window.shiftVoice?.setListening();");
            StartPtt();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            EndPtt();
            e.Handled = true;
        }
    }
}
