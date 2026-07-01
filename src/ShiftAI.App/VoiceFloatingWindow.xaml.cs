using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShiftAI.App;

public partial class VoiceFloatingWindow : Window
{
    private readonly DispatcherTimer _animationTimer;
    private bool _pttDown;
    private bool _listeningVisual;
    private double _phase;

    public VoiceFloatingWindow()
    {
        InitializeComponent();

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(42)
        };
        _animationTimer.Tick += (_, _) => AnimateVoiceHud();
        _animationTimer.Start();
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? PttStarted;
    public event EventHandler? PttEnded;

    public void SetReady(string engineStatus)
    {
        _listeningVisual = false;
        PttButton.Content = "⌨ 누르고 있는 동안 듣기 (SPACE)";
        TranscriptText.Text = $"\"듣기 준비됨 · {engineStatus}\"";
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(255, 62, 200));
    }

    public void SetListening()
    {
        _listeningVisual = true;
        PttButton.Content = "● 듣는 중...";
        TranscriptText.Text = "...";
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(39, 255, 139));
    }

    public void SetTranscript(string text)
    {
        _listeningVisual = false;
        PttButton.Content = "⌨ 누르고 있는 동안 듣기 (SPACE)";
        TranscriptText.Text = $"\"{text}\"";
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(255, 62, 200));
    }

    public void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 22;
        Top = area.Bottom - Height - 58;
    }

    private void StartPtt()
    {
        if (_pttDown)
        {
            return;
        }

        _pttDown = true;
        _listeningVisual = true;
        PttStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndPtt()
    {
        if (!_pttDown)
        {
            return;
        }

        _pttDown = false;
        _listeningVisual = false;
        PttEnded?.Invoke(this, EventArgs.Empty);
    }

    private void AnimateVoiceHud()
    {
        _phase += _listeningVisual ? 0.34 : 0.12;
        var width = Math.Max(318, WaveLine.ActualWidth);
        var height = 42d;
        var center = 21d;
        var amplitude = _listeningVisual ? 15d : 4d;
        var points = new PointCollection();

        const int count = 28;
        for (var i = 0; i < count; i++)
        {
            var x = width * i / (count - 1);
            var pulse = Math.Sin(_phase + i * 0.72) * 0.58
                + Math.Sin(_phase * 0.47 + i * 1.31) * 0.26
                + Math.Sin(_phase * 1.8 + i * 0.19) * 0.16;
            var envelope = 0.32 + 0.68 * Math.Sin(Math.PI * i / (count - 1));
            var y = center + pulse * amplitude * envelope;
            points.Add(new Point(x, Math.Clamp(y, 4, height - 4)));
        }

        WaveLine.Points = points;
        LiveDot.Opacity = 0.55 + 0.45 * Math.Abs(Math.Sin(_phase * 1.4));
        WaveLine.Opacity = _listeningVisual
            ? 0.78 + 0.22 * Math.Abs(Math.Sin(_phase * 1.1))
            : 0.36 + 0.18 * Math.Abs(Math.Sin(_phase * 0.7));
    }

    private void PttButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        StartPtt();
        e.Handled = true;
    }

    private void PttButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        EndPtt();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
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

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _animationTimer.Stop();
        base.OnClosed(e);
    }
}
