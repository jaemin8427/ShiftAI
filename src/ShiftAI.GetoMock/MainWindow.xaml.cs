using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ShiftAI.GetoMock;

public partial class MainWindow : Window
{
    private const string PipeName = "ShiftAI.GetoMock";
    private readonly Dictionary<string, (int Quantity, int Price)> _cart = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cartDisplayToName = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _pipeCancellation = new();

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _pipeCancellation.Cancel();
        StartBackgroundCommandServer();
    }

    private void FoodOrderButton_Click(object sender, RoutedEventArgs e)
    {
        ShowOrderPanel();
    }

    private void CloseOrderButton_Click(object sender, RoutedEventArgs e)
    {
        OrderPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
    }

    private void FoodCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string value)
        {
            return;
        }

        var parts = value.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var price))
        {
            return;
        }

        AddCartItem(parts[0], price, 1);
    }

    private void AddCartItem(string name, int price, int quantity)
    {
        if (quantity <= 0)
        {
            return;
        }

        if (_cart.TryGetValue(name, out var line))
        {
            _cart[name] = (line.Quantity + quantity, line.Price);
        }
        else
        {
            _cart[name] = (quantity, price);
        }

        RenderCart();
    }

    private void ShowOrderPanel()
    {
        MainPanel.Visibility = Visibility.Collapsed;
        OrderPanel.Visibility = Visibility.Visible;
    }

    private void RenderCart()
    {
        CartList.Items.Clear();
        _cartDisplayToName.Clear();
        var total = 0;

        foreach (var item in _cart)
        {
            var lineTotal = item.Value.Quantity * item.Value.Price;
            total += lineTotal;
            var display = $"{item.Key} x {item.Value.Quantity}    {lineTotal.ToString("N0", CultureInfo.InvariantCulture)} 원";
            _cartDisplayToName[display] = item.Key;
            CartList.Items.Add(display);
        }

        EmptyCartText.Visibility = _cart.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TotalText.Text = $"{total.ToString("N0", CultureInfo.InvariantCulture)} 원";
    }

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveSelectedCartLine();
    }

    private void ClearCartButton_Click(object sender, RoutedEventArgs e)
    {
        _cart.Clear();
        RenderCart();
    }

    private void CartList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        RemoveSelectedCartLine();
    }

    private void RemoveSelectedCartLine()
    {
        if (CartList.SelectedItem is not string display || !_cartDisplayToName.TryGetValue(display, out var name))
        {
            return;
        }

        if (!_cart.TryGetValue(name, out var line))
        {
            return;
        }

        if (line.Quantity <= 1)
        {
            _cart.Remove(name);
        }
        else
        {
            _cart[name] = (line.Quantity - 1, line.Price);
        }

        RenderCart();
    }

    private void StartBackgroundCommandServer()
    {
        _ = Task.Run(async () =>
        {
            while (!_pipeCancellation.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(_pipeCancellation.Token);

                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                    var line = await reader.ReadLineAsync(_pipeCancellation.Token);
                    var response = await HandleBackgroundCommandAsync(line, _pipeCancellation.Token);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(150, _pipeCancellation.Token).ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }
        });
    }

    private async Task<BackgroundOrderResponse> HandleBackgroundCommandAsync(string? line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new BackgroundOrderResponse(false, "Empty background command.");
        }

        var command = JsonSerializer.Deserialize<BackgroundOrderCommand>(
            line,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (command?.Type != "add-items" || command.Items.Count == 0)
        {
            return new BackgroundOrderResponse(false, "Unsupported background command.");
        }

        await Dispatcher.InvokeAsync(() =>
        {
            ShowOrderPanel();
            foreach (var item in command.Items)
            {
                AddCartItem(item.Name, item.Price, item.Quantity);
            }
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);

        return new BackgroundOrderResponse(true, "Geto Mock background cart updated.");
    }

    private sealed record BackgroundOrderCommand(string Type, IReadOnlyList<BackgroundOrderItem> Items);
    private sealed record BackgroundOrderItem(string Name, int Price, int Quantity);
    private sealed record BackgroundOrderResponse(bool Success, string Message);
}
