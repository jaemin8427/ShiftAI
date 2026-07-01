using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ShiftAI.App;

public partial class VoiceFloatingWindow : Window
{
    private bool _pttDown;

    public VoiceFloatingWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? PttStarted;
    public event EventHandler? PttEnded;

    public void SetReady(string engineStatus)
    {
        PttButton.Content = "⌨ 누르고 있는 동안 듣기 (SPACE)";
        TranscriptText.Text = $"\"듣기 준비됨 · {engineStatus}\"";
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(255, 62, 200));
    }

    public void SetListening()
    {
        PttButton.Content = "● 듣는 중...";
        TranscriptText.Text = "...";
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(39, 255, 139));
    }

    public void SetTranscript(string text)
    {
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
}
