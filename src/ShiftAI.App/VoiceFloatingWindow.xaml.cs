using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShiftAI.App;

public partial class VoiceFloatingWindow : Window
{
    private const double CompactWidth = 150;
    private const double CompactHeight = 48;
    private const double ExpandedWidth = 350;
    private const double ExpandedHeight = 260;

    private readonly DispatcherTimer _animationTimer;
    private bool _pttDown;
    private bool _listeningVisual;
    private bool _dragging;
    private bool _movedWhileDragging;
    private bool _expanded;
    private Point _lastScreenPoint;
    private double _phase;

    public VoiceFloatingWindow()
    {
        InitializeComponent();

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(34)
        };
        _animationTimer.Tick += (_, _) => Animate();
        _animationTimer.Start();
        ApplySize();
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? PttStarted;
    public event EventHandler? PttEnded;

    public void SetReady(string engineStatus)
    {
        _listeningVisual = false;
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(255, 62, 200));
        TranscriptText.Text = $"\"듣기 준비됨 · {engineStatus}\"";
        PttButton.Content = "⌨ 누르고 있는 동안 듣기 (SPACE)";
    }

    public void SetListening()
    {
        _listeningVisual = true;
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(39, 255, 139));
        TranscriptText.Text = "...";
        PttButton.Content = "● 듣는 중...";
    }

    public void SetTranscript(string text)
    {
        _listeningVisual = false;
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(255, 62, 200));
        TranscriptText.Text = $"\"{text}\"";
        PttButton.Content = "⌨ 누르고 있는 동안 듣기 (SPACE)";
    }

    public void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 18;
        Top = area.Bottom - Height - 46;
    }

    private void StartPtt()
    {
        if (_pttDown)
        {
            return;
        }

        _pttDown = true;
        SetListening();
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

    private void Animate()
    {
        _phase += _listeningVisual ? 0.42 : 0.16;
        var width = _expanded ? 312d : 116d;
        var center = _expanded ? 32d : 11.5d;
        var amplitude = _listeningVisual
            ? (_expanded ? 17d : 8.2)
            : (_expanded ? 4.2 : 2.8);
        var points = new PointCollection();

        const int count = 24;
        for (var i = 0; i < count; i++)
        {
            var x = width * i / (count - 1);
            var envelope = 0.35 + 0.65 * Math.Sin(Math.PI * i / (count - 1));
            var pulse = Math.Sin(_phase + i * 0.67) * 0.56
                + Math.Sin(_phase * 0.51 + i * 1.17) * 0.28
                + Math.Sin(_phase * 1.7 + i * 0.21) * 0.16;
            var y = center + pulse * amplitude * envelope;
            points.Add(new Point(x, Math.Clamp(y, 4, _expanded ? 60 : 19)));
        }

        WaveLine.Points = points;
        WaveLine.Opacity = _listeningVisual
            ? 0.78 + 0.22 * Math.Abs(Math.Sin(_phase * 1.1))
            : 0.42 + 0.22 * Math.Abs(Math.Sin(_phase * 0.7));
        LiveDot.Opacity = 0.45 + 0.55 * Math.Abs(Math.Sin(_phase * 1.3));
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
        if (e.OriginalSource is DependencyObject source && IsInPttButton(source))
        {
            return;
        }

        Activate();
        _dragging = true;
        _movedWhileDragging = false;
        _lastScreenPoint = PointToScreen(e.GetPosition(this));
        CaptureMouse();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _lastScreenPoint.X;
        var dy = current.Y - _lastScreenPoint.Y;
        if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
        {
            MoveWindow(dx, dy);
            _movedWhileDragging = true;
        }
        _lastScreenPoint = current;
        e.Handled = true;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        if (!_movedWhileDragging)
        {
            ToggleExpanded();
        }
        e.Handled = true;
    }

    private void MoveWindow(double dx, double dy)
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left + dx, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top + dy, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    protected override void OnClosed(EventArgs e)
    {
        _animationTimer.Stop();
        base.OnClosed(e);
    }

    private void ToggleExpanded()
    {
        _expanded = !_expanded;
        ApplySize();
    }

    private void ApplySize()
    {
        var oldRight = Left + Width;
        var oldBottom = Top + Height;
        Width = _expanded ? ExpandedWidth : CompactWidth;
        Height = _expanded ? ExpandedHeight : CompactHeight;
        WaveBox.Height = _expanded ? 64 : 22;
        TitleText.FontSize = _expanded ? 12 : 10;
        TitleText.Text = _expanded ? "SHIFT AI · 음성 모드" : "SHIFT AI";
        SeatText.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        TranscriptPanel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        PttButton.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        FootText.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        MainBorder.Padding = _expanded ? new Thickness(9, 7, 9, 9) : new Thickness(7, 5, 7, 5);

        if (IsLoaded)
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Clamp(oldRight - Width, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(oldBottom - Height, area.Top, Math.Max(area.Top, area.Bottom - Height));
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void PttButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        StartPtt();
        PttButton.CaptureMouse();
        e.Handled = true;
    }

    private void PttButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        PttButton.ReleaseMouseCapture();
        EndPtt();
        e.Handled = true;
    }

    private static bool IsInPttButton(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { Name: "PttButton" })
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
