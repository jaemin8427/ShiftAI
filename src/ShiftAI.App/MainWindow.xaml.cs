using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShiftAI.Core;
using MenuModel = ShiftAI.Core.MenuItem;
using MediaColor = System.Windows.Media.Color;
using WpfButton = System.Windows.Controls.Button;

namespace ShiftAI.App;

public partial class MainWindow : Window
{
    private readonly ActionExecutor _executor;
    private readonly IVoiceInputService _voiceInput;
    private readonly string _voiceEngineStatus;
    private readonly SpeechFeedbackService _speech = new();
    private readonly ObservableCollection<string> _conversation = [];
    private readonly ObservableCollection<string> _gameConversation = [];
    private readonly List<string> _activationSequence = [];
    private int _pendingQuantity = 1;
    private string _pendingText = "";
    private string _screen = "desktop";
    private bool _voiceMode;
    private bool _pttHold;

    public MainWindow()
    {
        InitializeComponent();
        ConversationList.ItemsSource = _conversation;
        GameConversationList.ItemsSource = _gameConversation;

        var root = FindWorkspaceRoot();
        var settings = HermesAgentSettings.Load(Path.Combine(root, "data", "hermes.agent.json"));
        (_voiceInput, _voiceEngineStatus) = CreateVoiceInput(root);
        var menu = MenuLoader.Load(Path.Combine(root, "data", "menu.sample.json"));
        var matcher = new MenuMatcher(menu);
        var localRouter = new IntentRouter(matcher);
        var geminiKey = GeminiKeyProvider.GetApiKey();
        IIntentRouter? geminiRouter = string.IsNullOrWhiteSpace(geminiKey)
            ? null
            : new GeminiIntentRouter(new HttpClient { Timeout = TimeSpan.FromSeconds(4) }, geminiKey, menu, settings);

        var router = new CompositeIntentRouter(localRouter, geminiRouter);
        var log = new JsonlActionLog(Path.Combine(root, "logs", "actions.jsonl"));
        var tools = HermesSkillToolRegistry.FromAdapter(new MockPcCafeAdapter());
        _executor = new ActionExecutor(settings.SeatNumber, new Cart(), router, tools, log);

        AddSampleButtons();
        SetStatus(AgentStatus.Idle);
        AddAssistant($"{settings.AgentName} 설정을 불러왔습니다. 음식 주문은 말로 요청하고, 애매하면 후보를 선택해 주세요.");
        _gameConversation.Add("Shift AI  게임은 계속하세요. 필요한 주문만 빠르게 처리할게요.");
        FooterVoiceStatus.Text = $"{_voiceEngineStatus} · BACKGROUND GETO CONTROL";
        VoiceEngineText.Text = _voiceEngineStatus;
        ShowDesktop();
    }

    protected override void OnClosed(EventArgs e)
    {
        _speech.Dispose();
        base.OnClosed(e);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync(CommandInput.Text);
    }

    private async void CommandInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ExecuteCommandAsync(CommandInput.Text);
        }
    }

    private async Task ExecuteCommandAsync(string text)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        CommandInput.Text = "";
        AddUser(text);
        SendButton.IsEnabled = false;

        try
        {
            var response = await _executor.ExecuteAsync(text);
            RenderResponse(response);
            SpeakAndToast(response.AssistantText);
        }
        catch (Exception ex)
        {
            var message = $"처리 중 오류가 발생했습니다. {ex.Message}";
            AddAssistant(message);
            SpeakAndToast(message);
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    private async Task SelectCandidateAsync(MenuModel item)
    {
        AddUser($"{item.Name} 선택");
        var response = await _executor.SelectMenuItemAsync(item, _pendingQuantity, _pendingText);
        RenderResponse(response);
        SpeakAndToast(response.AssistantText);
    }

    private void RenderResponse(AgentResponse response)
    {
        SetStatus(response.Status);
        AddAssistant(response.AssistantText);

        CandidatePanel.Children.Clear();
        if (response.Candidates is { Count: > 0 })
        {
            _pendingQuantity = response.Route.Quantity;
            _pendingText = response.UserText;
            foreach (var candidate in response.Candidates)
            {
                var button = new WpfButton
                {
                    Content = $"{candidate.Name} - {candidate.Price.ToString("N0", CultureInfo.InvariantCulture)}원",
                    Tag = candidate,
                    Margin = new Thickness(0, 0, 8, 8)
                };
                button.Click += async (_, _) => await SelectCandidateAsync((MenuModel)button.Tag);
                CandidatePanel.Children.Add(button);
            }
        }
    }

    private void AddSampleButtons()
    {
        string[] commands =
        [
            "롤 켜줘",
            "콜라 하나 추가해",
            "라면 시켜줘",
            "주문 상태",
            "시간 얼마나 남았어?",
            "직원 불러줘",
            "소리 안 나와",
            "취소",
            "주문해"
        ];

        foreach (var command in commands)
        {
            var button = new WpfButton { Content = command };
            button.Click += async (_, _) => await ExecuteCommandAsync(command);
            SampleCommandPanel.Children.Add(button);
        }
    }

    private void OpenChatButton_Click(object sender, RoutedEventArgs e)
    {
        ShowChat();
    }

    private void OpenVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        EnterVoice();
    }

    private void OpenGameButton_Click(object sender, RoutedEventArgs e)
    {
        ShowGame();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceMode)
        {
            ExitVoice();
        }

        ShowDesktop();
    }

    private void DesktopToastButton_Click(object sender, RoutedEventArgs e)
    {
        SpeakAndToast("프린트, OTT, EDU 기능은 다음 어댑터에서 연결할 예정입니다.");
    }

    private void OpenGamePanelButton_Click(object sender, RoutedEventArgs e)
    {
        OpenGamePanel();
    }

    private void CloseGamePanelButton_Click(object sender, RoutedEventArgs e)
    {
        GamePanel.Visibility = Visibility.Collapsed;
    }

    private async void GameQuickCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton button && button.CommandParameter is string command)
        {
            await ExecuteGameCommandAsync(command);
        }
    }

    private async void GameSendButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteGameCommandAsync(GameInput.Text);
    }

    private async void GameInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ExecuteGameCommandAsync(GameInput.Text);
        }
    }

    private async Task ExecuteGameCommandAsync(string text)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        GameInput.Text = "";
        _gameConversation.Add($"나  {text}");
        GameConversationList.ScrollIntoView(_gameConversation[^1]);

        var response = await _executor.ExecuteAsync(text);
        SetStatus(response.Status);
        _gameConversation.Add($"Shift AI  {response.AssistantText}");
        GameConversationList.ScrollIntoView(_gameConversation[^1]);
        SpeakAndToast(response.AssistantText);

        if (response.Candidates is { Count: > 0 })
        {
            ShowChat();
            RenderResponse(response);
        }
    }

    private void CloseVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        ExitVoice();
    }

    private void PttButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        StartPtt();
    }

    private async void PttButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        await EndPttAsync();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (GamePanel.Visibility == Visibility.Visible)
            {
                GamePanel.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }

            if (_voiceMode)
            {
                ExitVoice();
                e.Handled = true;
                return;
            }

            if (_screen != "desktop")
            {
                ShowDesktop();
                e.Handled = true;
                return;
            }
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && e.Key == Key.V)
        {
            if (_voiceMode)
            {
                ExitVoice();
            }
            else
            {
                EnterVoice();
            }

            e.Handled = true;
            return;
        }

        if (_voiceMode && e.Key == Key.Space)
        {
            StartPtt();
            e.Handled = true;
            return;
        }

        TrackActivationSequence(e.Key);
    }

    private async void Window_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_voiceMode && e.Key == Key.Space)
        {
            await EndPttAsync();
            e.Handled = true;
        }
    }

    private void TrackActivationSequence(Key key)
    {
        var value = key switch
        {
            Key.LeftShift or Key.RightShift => "shift",
            Key.A => "a",
            Key.I => "i",
            _ => ""
        };

        if (string.IsNullOrEmpty(value))
        {
            if (key is not Key.LeftShift and not Key.RightShift)
            {
                _activationSequence.Clear();
            }

            return;
        }

        if (value == "shift")
        {
            _activationSequence.Clear();
            _activationSequence.Add(value);
            return;
        }

        if (value == "a" && _activationSequence.SequenceEqual(new[] { "shift" }))
        {
            _activationSequence.Add(value);
            return;
        }

        if (value == "i" && _activationSequence.SequenceEqual(new[] { "shift", "a" }))
        {
            _activationSequence.Clear();
            if (_screen == "game")
            {
                OpenGamePanel();
            }
            else
            {
                ShowChat();
            }

            return;
        }

        _activationSequence.Clear();
    }

    private void ShowDesktop()
    {
        _screen = "desktop";
        DesktopScreen.Visibility = Visibility.Visible;
        ChatScreen.Visibility = Visibility.Collapsed;
        GameScreen.Visibility = Visibility.Collapsed;
        GamePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowChat()
    {
        _screen = "chat";
        DesktopScreen.Visibility = Visibility.Collapsed;
        ChatScreen.Visibility = Visibility.Visible;
        GameScreen.Visibility = Visibility.Collapsed;
        CommandInput.Focus();
        ShowToast("Shift AI 호출됨");
    }

    private void ShowGame()
    {
        _screen = "game";
        DesktopScreen.Visibility = Visibility.Collapsed;
        ChatScreen.Visibility = Visibility.Collapsed;
        GameScreen.Visibility = Visibility.Visible;
        ShowToast("게임 모드. SHIFT+A+I로 빠른 주문 패널을 엽니다.");
    }

    private void OpenGamePanel()
    {
        ShowGame();
        GamePanel.Visibility = Visibility.Visible;
        GameInput.Focus();
        ShowToast("게임 중 주문 패널 열림");
    }

    private void EnterVoice()
    {
        _voiceMode = true;
        VoiceWidget.Visibility = Visibility.Visible;
        VoiceTranscript.Text = $"듣기 준비됨. SPACE 또는 버튼을 누르고 말하세요. ({_voiceEngineStatus})";
        ShowToast("음성 모드 ON");
    }

    private void ExitVoice()
    {
        _voiceMode = false;
        _pttHold = false;
        PttButton.Content = "누르는 동안 듣기 (SPACE)";
        VoiceWidget.Visibility = Visibility.Collapsed;
        ShowToast("음성 모드 OFF");
    }

    private void StartPtt()
    {
        if (!_voiceMode || _pttHold)
        {
            return;
        }

        _pttHold = true;
        PttButton.Content = "듣는 중...";
        VoiceTranscript.Text = "...";
    }

    private async Task EndPttAsync()
    {
        if (!_voiceMode || !_pttHold)
        {
            return;
        }

        _pttHold = false;
        PttButton.Content = "누르는 동안 듣기 (SPACE)";
        var voiceText = await _voiceInput.ListenOnceAsync();
        VoiceTranscript.Text = $"\"{voiceText}\"";
        await ExecuteCommandAsync(voiceText);
    }

    private void AddUser(string text)
    {
        _conversation.Add($"나  {text}");
        ConversationList.ScrollIntoView(_conversation[^1]);
    }

    private void AddAssistant(string text)
    {
        _conversation.Add($"Shift AI  {text}");
        ConversationList.ScrollIntoView(_conversation[^1]);
    }

    private void SpeakAndToast(string message)
    {
        ShowToast(message);
        _speech.Speak(message);
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastHost.Visibility = Visibility.Visible;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.7) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ToastHost.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    private void SetStatus(AgentStatus status)
    {
        StatusText.Text = status switch
        {
            AgentStatus.AwaitingConfirmation => "awaiting confirmation",
            AgentStatus.Completed => "completed",
            AgentStatus.Cancelled => "cancelled",
            AgentStatus.NeedsClarification => "needs clarification",
            _ => "idle"
        };

        StatusBadge.Background = status switch
        {
            AgentStatus.AwaitingConfirmation => new SolidColorBrush(MediaColor.FromRgb(120, 83, 28)),
            AgentStatus.Completed => new SolidColorBrush(MediaColor.FromRgb(24, 105, 76)),
            AgentStatus.Cancelled => new SolidColorBrush(MediaColor.FromRgb(113, 45, 56)),
            AgentStatus.NeedsClarification => new SolidColorBrush(MediaColor.FromRgb(47, 83, 138)),
            _ => new SolidColorBrush(MediaColor.FromRgb(37, 52, 73))
        };
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "data", "menu.sample.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static (IVoiceInputService Service, string Status) CreateVoiceInput(string root)
    {
        var modelPath = Path.Combine(root, "data", "models", "ggml-tiny.bin");
        var primary = new WhisperVoiceInputService(modelPath);
        var fallback = new DemoVoiceInputService();
        var status = File.Exists(modelPath)
            ? "WHISPER.NET LOCAL STT READY"
            : "DEMO STT FALLBACK";

        return (new FallbackVoiceInputService(primary, fallback), status);
    }
}
