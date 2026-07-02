using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
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
    private readonly GeminiScreenVisionCommandService? _screenVision;
    private readonly SpeechFeedbackService _speech = new();
    private readonly ObservableCollection<string> _conversation = [];
    private readonly ObservableCollection<string> _gameConversation = [];
    private readonly List<string> _activationSequence = [];
    private VoiceFloatingWindow? _voiceWindow;
    private int _pendingQuantity = 1;
    private string _pendingText = "";
    private string _screen = "desktop";
    private bool _voiceMode;
    private bool _pttHold;
    private bool _soundEnabled;
    private Task<string>? _voiceListenTask;
    private string _faceMode = "core";
    private string _personaMode = "standard";
    private string _agentMode = "hermes";
    private HwndSource? _hotkeySource;
    private bool _globalVoiceBusy;
    private const int GlobalVoiceHotkeyId = 0xA11;

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
        _screenVision = string.IsNullOrWhiteSpace(geminiKey)
            ? null
            : new GeminiScreenVisionCommandService(
                new HttpClient { Timeout = TimeSpan.FromSeconds(12) },
                geminiKey,
                settings.Model,
                root,
                menu);
        IIntentRouter? geminiRouter = string.IsNullOrWhiteSpace(geminiKey)
            ? null
            : new GeminiIntentRouter(
                new HttpClient { Timeout = TimeSpan.FromSeconds(4) },
                geminiKey,
                menu,
                settings,
                () => _personaMode);

        var router = new CompositeIntentRouter(localRouter, geminiRouter);
        var log = new JsonlActionLog(Path.Combine(root, "logs", "actions.jsonl"));
        var tools = HermesSkillToolRegistry.FromAdapter(new MockPcCafeAdapter());
        _executor = new ActionExecutor(settings.SeatNumber, new Cart(), router, tools, log);

        AddSampleButtons();
        SetStatus(AgentStatus.Idle);
        AddAssistant($"{settings.AgentName} 설정을 불러왔습니다. 음식 주문은 말로 요청하고, 애매하면 후보를 선택해 주세요.");
        _gameConversation.Add("Shift AI  게임은 계속하세요. 필요한 주문만 빠르게 처리할게요.");
        FooterVoiceStatus.Text = "SHIFT + V 를 누르면 언제든 음성으로 전환됩니다";
        VoiceEngineText.Text = _voiceEngineStatus;
        _ = InitializeFaceWebViewAsync();
        UpdateHudButtons();
        ShowDesktop();
    }

    // Register a system-wide hotkey (Ctrl+Shift+Space) so voice can be triggered even while a game or
    // another app is in the foreground. RegisterHotKey delivers WM_HOTKEY regardless of focus.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _hotkeySource = HwndSource.FromHwnd(handle);
        _hotkeySource?.AddHook(HotkeyWndProc);
        RegisterHotKey(handle, GlobalVoiceHotkeyId, ModControl | ModShift | ModNoRepeat, VkSpace);
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmHotkey = 0x0312;
        if (msg == WmHotkey && wParam.ToInt32() == GlobalVoiceHotkeyId)
        {
            handled = true;
            _ = Dispatcher.InvokeAsync(async () => await GlobalVoiceListenAsync());
        }

        return IntPtr.Zero;
    }

    /// <summary>One-shot voice capture fired by the global hotkey; works while any app is focused.</summary>
    private async Task GlobalVoiceListenAsync()
    {
        if (_globalVoiceBusy || _pttHold)
        {
            return;
        }

        _globalVoiceBusy = true;
        try
        {
            if (!_voiceMode)
            {
                EnterVoice();
            }

            _voiceWindow?.SetListening();
            VoiceTranscript.Text = "...";

            string voiceText;
            try
            {
                voiceText = await _voiceInput.ListenOnceAsync();
            }
            catch
            {
                voiceText = "";
            }

            if (string.IsNullOrWhiteSpace(voiceText))
            {
                const string retry = "음성을 인식하지 못했어요. 다시 말해 주세요.";
                VoiceTranscript.Text = retry;
                _voiceWindow?.SetTranscript(retry);
                return;
            }

            VoiceTranscript.Text = $"\"{voiceText}\"";
            _voiceWindow?.SetTranscript(voiceText);
            await ExecuteCommandAsync(voiceText, spoken: true);
        }
        finally
        {
            _globalVoiceBusy = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                UnregisterHotKey(handle, GlobalVoiceHotkeyId);
            }

            _hotkeySource?.RemoveHook(HotkeyWndProc);
        }
        catch
        {
            // Ignore hotkey cleanup failures on shutdown.
        }

        _speech.Dispose();
        base.OnClosed(e);
    }

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Maximized;
        UpdateWindowChromeState();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = ease
        });

        RootShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.985, 1, TimeSpan.FromMilliseconds(620))
        {
            EasingFunction = ease
        });
        RootShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.985, 1, TimeSpan.FromMilliseconds(620))
        {
            EasingFunction = ease
        });
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateWindowChromeState();
    }

    private async Task InitializeFaceWebViewAsync()
    {
        try
        {
            await FaceWebView.EnsureCoreWebView2Async();
            FaceWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            FaceWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            FaceWebView.NavigationCompleted += async (_, _) =>
            {
                await SyncFaceHudAsync();
            };

            var facePath = Path.Combine(AppContext.BaseDirectory, "Assets", "shift-face.html");
            var faceUri = new UriBuilder(new Uri(facePath))
            {
                Query = $"v={File.GetLastWriteTimeUtc(facePath).Ticks}"
            };
            FaceWebView.Source = faceUri.Uri;
        }
        catch (Exception ex)
        {
            AddAssistant($"로봇 페이스 WebView2 초기화 실패: {ex.Message}");
        }
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

    private async Task ExecuteCommandAsync(string text, bool spoken = false)
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
            var response = await _executor.ExecuteAsync(text, spoken);
            var assistantText = RenderResponse(response);
            SpeakAndToast(assistantText);
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
        var assistantText = RenderResponse(response);
        SpeakAndToast(assistantText);
    }

    private string RenderResponse(AgentResponse response)
    {
        SetStatus(response.Status);
        var assistantText = ApplyPersona(response.AssistantText, response);
        AddAssistant(assistantText);

        CandidatePanel.Children.Clear();
        if (response.Candidates is { Count: > 0 })
        {
            _pendingQuantity = response.Route.Quantity;
            _pendingText = response.UserText;
            foreach (var candidate in response.Candidates)
            {
                var label = $"{candidate.Name}  {candidate.Price.ToString("N0", CultureInfo.InvariantCulture)}원";
                CandidatePanel.Children.Add(CreateCommandChip(label, async () => await SelectCandidateAsync(candidate)));
            }
        }

        return assistantText;
    }

    private void AddSampleButtons()
    {
        (string Label, string Command)[] commands =
        [
            ("⚔ 롤 실행", "롤 실행해줘"),
            ("🎮 게임 추천", "할만한 게임 추천해줘"),
            ("☕ 아아 주문", "아아 주문해줘"),
            ("🥤 콜라 주문", "콜라 주문해줘"),
            ("🍜 라면 주문", "라면 주문해줘"),
            ("📦 주문 상태", "주문 상태 알려줘"),
            ("⏳ 남은 시간·요금", "남은 시간이랑 요금 알려줘"),
            ("▶ OTT 재생", "OTT 틀어줘"),
            ("🖨 프린트", "이 화면 컬러로 3장 출력해줘"),
            ("⏱ 시간 충전", "시간 1시간 충전해줘"),
            ("↔ 자리 이동", "창가 자리로 옮기고 싶어"),
            ("👥 친구랑 자리 붙기", "친구랑 자리 붙여줘"),
            ("💡 조도 조절", "조명 좀 어둡게 해줘"),
            ("🔔 직원 호출", "직원 호출해줘"),
            ("★ 멤버십·적립", "내 적립 포인트 알려줘"),
            ("🎓 EDU·해커톤", "해커톤 세션 들어갈래")
        ];

        foreach (var (label, command) in commands)
        {
            SampleCommandPanel.Children.Add(CreateCommandChip(label, async () => await ExecuteCommandAsync(command)));
        }
    }

    private Border CreateCommandChip(string label, Func<Task> action)
    {
        var parts = SplitChipLabel(label);
        var textPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (!string.IsNullOrEmpty(parts.Icon))
        {
            textPanel.Children.Add(new TextBlock
            {
                Text = parts.Icon,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(34, 240, 255)),
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        textPanel.Children.Add(new TextBlock
        {
            Text = parts.Text,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(34, 240, 255)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(96, 34, 240, 255)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(MediaColor.FromArgb(14, 34, 240, 255)),
            Padding = new Thickness(13, 7, 13, 7),
            Margin = new Thickness(4, 4, 4, 4),
            MinHeight = 32,
            Cursor = Cursors.Hand,
            Child = textPanel
        };

        chip.MouseEnter += (_, _) =>
        {
            chip.Background = new SolidColorBrush(MediaColor.FromRgb(34, 240, 255));
            foreach (var child in textPanel.Children.OfType<TextBlock>())
            {
                child.Foreground = new SolidColorBrush(MediaColor.FromRgb(4, 18, 26));
            }
        };
        chip.MouseLeave += (_, _) =>
        {
            chip.Background = new SolidColorBrush(MediaColor.FromArgb(14, 34, 240, 255));
            foreach (var child in textPanel.Children.OfType<TextBlock>())
            {
                child.Foreground = new SolidColorBrush(MediaColor.FromRgb(34, 240, 255));
            }
        };
        chip.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await action();
        };

        return chip;
    }

    private static (string Icon, string Text) SplitChipLabel(string label)
    {
        var index = label.IndexOf(' ');
        if (index <= 0)
        {
            return ("", label);
        }

        var icon = label[..index];
        var text = label[(index + 1)..];
        return (icon, text);
    }

    private void OpenChatButton_Click(object sender, RoutedEventArgs e)
    {
        ShowChat();
    }

    private async void FaceCoreButton_Click(object sender, RoutedEventArgs e)
    {
        _faceMode = "core";
        UpdateHudButtons();
        await SyncFaceHudAsync();
    }

    private async void FaceCrtButton_Click(object sender, RoutedEventArgs e)
    {
        _faceMode = "term";
        UpdateHudButtons();
        await SyncFaceHudAsync();
    }

    private async void PersonaStandardButton_Click(object sender, RoutedEventArgs e)
    {
        _personaMode = "standard";
        UpdateHudButtons();
        await SyncFaceHudAsync();
        AddAssistant("페르소나: 정중하고 차분한 안내");
    }

    private async void PersonaGamerButton_Click(object sender, RoutedEventArgs e)
    {
        _personaMode = "gamer";
        UpdateHudButtons();
        await SyncFaceHudAsync();
        AddAssistant("페르소나: 활기차지만 정중한 게이머 친화 모드");
    }

    private async void PersonaFocusButton_Click(object sender, RoutedEventArgs e)
    {
        _personaMode = "focus";
        UpdateHudButtons();
        await SyncFaceHudAsync();
        AddAssistant("페르소나: 간결한 집중 모드");
    }

    private void AgentHermesButton_Click(object sender, RoutedEventArgs e)
    {
        _agentMode = "hermes";
        UpdateHudButtons();
        ShowToast("Hermes Agent");
    }

    private void AgentLocalButton_Click(object sender, RoutedEventArgs e)
    {
        _agentMode = "local";
        UpdateHudButtons();
        ShowToast("Local Router");
    }

    private void SoundToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _soundEnabled = !_soundEnabled;
        _speech.Enabled = _soundEnabled;
        SoundToggleButton.Content = _soundEnabled ? "🔊 소리 끄기" : "🔇 소리 켜기";
        SoundToggleButton.Foreground = _soundEnabled
            ? new SolidColorBrush(MediaColor.FromRgb(34, 240, 255))
            : new SolidColorBrush(MediaColor.FromRgb(95, 117, 150));
        SoundToggleButton.BorderBrush = _soundEnabled
            ? new SolidColorBrush(MediaColor.FromRgb(34, 240, 255))
            : new SolidColorBrush(MediaColor.FromArgb(90, 34, 240, 255));

        if (_soundEnabled)
        {
            ShowToast("소리 켜짐");
            _speech.Speak("소리 켜짐");
        }
        else
        {
            ShowToast("소리 꺼짐");
        }
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void RestoreWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateWindowChromeState();
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ChromeDragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            RestoreWindowButton_Click(sender, e);
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse is released during the handoff.
        }
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

    private async void ScreenVisionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunScreenVisionAsync();
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
        var assistantText = ApplyPersona(response.AssistantText, response);
        _gameConversation.Add($"Shift AI  {assistantText}");
        GameConversationList.ScrollIntoView(_gameConversation[^1]);
        SpeakAndToast(assistantText);

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

    // When a Korean IME is active, WPF reports e.Key == ImeProcessed and the real key sits in ImeProcessedKey.
    // Resolve it so the hotkeys (Shift+V, Shift+G, Shift+A+I, Space) still fire in Korean input mode.
    private static Key ResolveKey(System.Windows.Input.KeyEventArgs e)
    {
        return e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = ResolveKey(e);

        if (key == Key.Escape)
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

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && key == Key.V)
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

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && key == Key.G)
        {
            _ = RunScreenVisionAsync();
            e.Handled = true;
            return;
        }

        if (_screen == "desktop" && key == Key.Enter)
        {
            ShowChat();
            e.Handled = true;
            return;
        }

        if (_voiceMode && key == Key.Space)
        {
            StartPtt();
            e.Handled = true;
            return;
        }

        TrackActivationSequence(key);
    }

    private async void Window_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_voiceMode && ResolveKey(e) == Key.Space)
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
        VoiceWidget.Visibility = Visibility.Collapsed;
        VoiceTranscript.Text = $"듣기 준비됨. SPACE 또는 버튼을 누르고 말하세요. ({_voiceEngineStatus})";
        // Voice mode is a single always-on-top floating widget; the main window is NOT minimized so the
        // hotkey keeps working (including the global hotkey) while other apps run.
        ShowVoiceWindow();
        ShowToast("음성 모드 ON");
    }

    private void ExitVoice()
    {
        _voiceMode = false;
        _pttHold = false;
        PttButton.Content = "누르는 동안 듣기 (SPACE)";
        VoiceWidget.Visibility = Visibility.Collapsed;
        HideVoiceWindow();
        ShowToast("음성 모드 OFF");
    }

    private void ShowVoiceWindow()
    {
        if (_voiceWindow is null)
        {
            _voiceWindow = new VoiceFloatingWindow();
            _voiceWindow.CloseRequested += (_, _) => ExitVoice();
            _voiceWindow.PttStarted += (_, _) => StartPtt();
            _voiceWindow.PttEnded += async (_, _) => await EndPttAsync();
        }

        _voiceWindow.SetReady(_voiceEngineStatus);
        _voiceWindow.Topmost = true; // always on top so it stays visible over games / other apps
        _voiceWindow.PositionBottomRight();
        _voiceWindow.Show();
        _voiceWindow.Activate();
    }

    private void HideVoiceWindow()
    {
        _voiceWindow?.Hide();
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
        _voiceWindow?.SetListening();
        _voiceListenTask = _voiceInput.ListenOnceAsync();
    }

    private async Task EndPttAsync()
    {
        if (!_voiceMode || !_pttHold)
        {
            return;
        }

        _pttHold = false;
        PttButton.Content = "누르는 동안 듣기 (SPACE)";
        string voiceText;
        try
        {
            voiceText = _voiceListenTask is null
                ? await _voiceInput.ListenOnceAsync()
                : await _voiceListenTask;
        }
        catch
        {
            voiceText = "";
        }
        finally
        {
            _voiceListenTask = null;
        }

        if (string.IsNullOrWhiteSpace(voiceText))
        {
            const string retryMessage = "음성을 인식하지 못했어요. 다시 말해 주세요.";
            VoiceTranscript.Text = retryMessage;
            _voiceWindow?.SetTranscript(retryMessage);
            ShowToast(retryMessage);
            return;
        }

        VoiceTranscript.Text = $"\"{voiceText}\"";
        _voiceWindow?.SetTranscript(voiceText);
        await ExecuteCommandAsync(voiceText, spoken: true);
    }

    private async Task RunScreenVisionAsync()
    {
        if (_voiceMode)
        {
            ShowToast("보이스 모드에서는 화면 판독을 사용할 수 없습니다.");
            return;
        }

        if (_screenVision is null)
        {
            const string message = "화면 판독에는 GEMINI_API_KEY 또는 Documents\\shiftaikey.txt가 필요합니다.";
            AddAssistant(message);
            SpeakAndToast(message);
            return;
        }

        var previousState = WindowState;
        var previousScreen = _screen;
        ShowToast("화면을 캡처해서 읽는 중...");

        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(450);
            var result = await _screenVision.ReadCommandAsync();

            WindowState = previousState;
            UpdateWindowChromeState();
            Activate();

            if (!result.Success)
            {
                var message = $"화면 판독 결과: {result.Reason}";
                if (previousScreen == "game")
                {
                    _gameConversation.Add($"Shift AI  {message}");
                    GameConversationList.ScrollIntoView(_gameConversation[^1]);
                }
                else
                {
                    AddAssistant(message);
                }

                SpeakAndToast(message);
                return;
            }

            var notice = $"화면 판독 명령: {result.Command}";
            if (previousScreen == "game")
            {
                _gameConversation.Add($"Shift AI  {notice}");
                GameConversationList.ScrollIntoView(_gameConversation[^1]);
                await ExecuteGameCommandAsync(result.Command);
            }
            else
            {
                AddAssistant(notice);
                await ExecuteCommandAsync(result.Command);
            }
        }
        catch (Exception ex)
        {
            WindowState = previousState;
            UpdateWindowChromeState();
            Activate();
            var message = $"화면 판독 중 오류가 발생했습니다. {ex.Message}";
            AddAssistant(message);
            SpeakAndToast(message);
        }
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

    private string ApplyPersona(string text, AgentResponse response)
    {
        return _personaMode switch
        {
            "gamer" => ApplyGamerPersona(text, response),
            "focus" => ApplyFocusPersona(text, response),
            _ => ApplyStandardPersona(text, response)
        };
    }

    private static string ApplyStandardPersona(string text, AgentResponse response)
    {
        return response.Status switch
        {
            AgentStatus.NeedsClarification when response.Candidates is { Count: > 0 } =>
                "원하시는 메뉴를 선택해 주세요. 후보 중 하나를 고르면 장바구니에 담아두겠습니다.",
            AgentStatus.Completed when text.Contains("시킬게", StringComparison.Ordinal) =>
                "확인했습니다. 주문 후보를 장바구니에 담았습니다.",
            _ => text
        };
    }

    private static string ApplyGamerPersona(string text, AgentResponse response)
    {
        return response.Status switch
        {
            AgentStatus.NeedsClarification when response.Candidates is { Count: > 0 } =>
                "좋아요. 라면 종류만 골라 주세요. 게임 흐름 끊기지 않게 바로 담아둘게요.",
            AgentStatus.Completed when response.Route.Intent == IntentType.AddFood =>
                "좋아요, 담아둘게요. 확정하려면 주문해라고 말해 주세요.",
            AgentStatus.Completed when response.Route.Intent == IntentType.LaunchGame =>
                "좋아요. 바로 실행합니다. 좋은 게임 되세요.",
            AgentStatus.Completed when response.Route.Intent == IntentType.CallStaff =>
                "직원 호출 넣었습니다. 잠깐만 기다려 주세요.",
            _ => text.StartsWith("아직", StringComparison.Ordinal)
                ? "아직은 처리 못 하는 명령이에요. 주문, 직원 호출, 소리 문제, 롤 실행, 남은 시간 조회를 말해 주세요."
                : text
        };
    }

    private static string ApplyFocusPersona(string text, AgentResponse response)
    {
        return response.Status switch
        {
            AgentStatus.NeedsClarification when response.Candidates is { Count: > 0 } =>
                "메뉴를 선택해 주세요.",
            AgentStatus.Completed when response.Route.Intent == IntentType.AddFood =>
                "담았습니다. 확정은 주문해.",
            AgentStatus.Completed when response.Route.Intent == IntentType.PlaceOrder =>
                "주문 접수 완료.",
            AgentStatus.Completed when response.Route.Intent == IntentType.CallStaff =>
                "직원 호출 완료.",
            AgentStatus.Completed when response.Route.Intent == IntentType.TroubleshootAudio =>
                "오디오 점검 요청 완료.",
            AgentStatus.Completed when response.Route.Intent == IntentType.LaunchGame =>
                "실행합니다.",
            AgentStatus.Completed when response.Route.Intent == IntentType.GetRemainingTime =>
                text,
            AgentStatus.Cancelled =>
                "취소했습니다.",
            _ => text.StartsWith("아직", StringComparison.Ordinal)
                ? "처리할 수 없습니다. 다른 명령을 말해 주세요."
                : text
        };
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

    private async Task SyncFaceHudAsync()
    {
        if (FaceWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await FaceWebView.ExecuteScriptAsync(
                $"window.shiftFace?.setFace('{_faceMode}'); window.shiftFace?.setPersona('{_personaMode}');");
        }
        catch
        {
            // Visual sync only.
        }
    }

    private void UpdateHudButtons()
    {
        SetSegmentButton(FaceCoreButton, _faceMode == "core", "cyan");
        SetSegmentButton(FaceCrtButton, _faceMode == "term" || _faceMode == "crt", "cyan");

        SetSegmentButton(PersonaStandardButton, _personaMode == "standard", "magenta");
        SetSegmentButton(PersonaGamerButton, _personaMode == "gamer", "magenta");
        SetSegmentButton(PersonaFocusButton, _personaMode == "focus", "magenta");

        SetSegmentButton(AgentHermesButton, _agentMode == "hermes", "cyan");
        SetSegmentButton(AgentLocalButton, _agentMode == "local", "cyan");
    }

    private void SetSegmentButton(WpfButton button, bool active, string accent)
    {
        var activeColor = accent == "magenta"
            ? MediaColor.FromRgb(255, 62, 200)
            : MediaColor.FromRgb(34, 240, 255);

        button.Background = active
            ? new SolidColorBrush(activeColor)
            : Brushes.Transparent;
        button.Foreground = active
            ? new SolidColorBrush(MediaColor.FromRgb(4, 18, 26))
            : new SolidColorBrush(MediaColor.FromRgb(95, 117, 150));
        button.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(90, 34, 240, 255));
        button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
    }

    private void UpdateWindowChromeState()
    {
        if (RestoreWindowButton is null)
        {
            return;
        }

        RestoreWindowButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
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
        var modelsDir = Path.Combine(root, "data", "models");
        // Prefer higher-accuracy Whisper models when present, else the built-in Windows STT (no model
        // download), else a demo fallback.
        var whisperModel = new[] { "ggml-small.bin", "ggml-base.bin", "ggml-tiny.bin" }
            .Select(name => Path.Combine(modelsDir, name))
            .FirstOrDefault(File.Exists);

        var providers = new List<(IVoiceInputService Service, string Tag)>();
        if (whisperModel is not null)
        {
            providers.Add((new WhisperVoiceInputService(whisperModel), $"WHISPER.NET STT ({Path.GetFileName(whisperModel)})"));
        }

        if (WindowsSpeechInputService.IsKoreanAvailable())
        {
            providers.Add((new WindowsSpeechInputService(), "WINDOWS STT (ko-KR)"));
        }

        providers.Add((new DemoVoiceInputService(), "DEMO STT FALLBACK"));

        // Chain providers so each falls back to the next when it returns an empty result.
        IVoiceInputService chain = providers[^1].Service;
        for (var i = providers.Count - 2; i >= 0; i--)
        {
            chain = new FallbackVoiceInputService(providers[i].Service, chain);
        }

        return (chain, providers[0].Tag);
    }
}
