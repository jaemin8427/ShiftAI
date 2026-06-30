namespace ShiftAI.Core;

public sealed class Cart
{
    private readonly Dictionary<string, CartLine> _lines = new(StringComparer.OrdinalIgnoreCase);

    public bool AwaitingConfirmation { get; private set; }

    public CartSnapshot Snapshot => new(_lines.Values.OrderBy(line => line.Item.Name).ToList());

    public void Add(MenuItem item, int quantity)
    {
        if (quantity <= 0)
        {
            quantity = 1;
        }

        if (_lines.TryGetValue(item.Id, out var line))
        {
            _lines[item.Id] = line with { Quantity = line.Quantity + quantity };
        }
        else
        {
            _lines[item.Id] = new CartLine(item, quantity);
        }

        AwaitingConfirmation = true;
    }

    public void MarkOrderPlaced()
    {
        _lines.Clear();
        AwaitingConfirmation = false;
    }

    public void ClearPending()
    {
        _lines.Clear();
        AwaitingConfirmation = false;
    }
}
