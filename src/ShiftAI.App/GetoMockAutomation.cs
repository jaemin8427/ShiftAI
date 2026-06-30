using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using ShiftAI.Core;

namespace ShiftAI.App;

public sealed class GetoMockAutomation
{
    private static int _debugSequence;

    public Task<GetoAutomationResult> AddCartItemsAsync(CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            TrySetDpiAware();
            var window = FindWindow(null, "Geto Mock");
            if (window == IntPtr.Zero)
            {
                return new GetoAutomationResult(false, "Geto Mock 창을 찾지 못했습니다. 먼저 Geto Mock을 실행해 주세요.");
            }

            if (!GetWindowRect(window, out var rect))
            {
                return new GetoAutomationResult(false, "Geto Mock 창 위치를 읽지 못했습니다.");
            }

            var bounds = rect.ToRectangle();
            if (!BringToFront(window, bounds))
            {
                return new GetoAutomationResult(false, "Geto Mock 창을 전면으로 가져오지 못했습니다.");
            }

            foreach (var line in cart.Lines)
            {
                for (var i = 0; i < line.Quantity; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var clicked = ClickMenuItemByImage(window, bounds, line.Item.Name, cancellationToken);
                    if (!clicked.Success)
                    {
                        return clicked;
                    }
                }
            }

            return new GetoAutomationResult(true, "Geto Mock 화면을 이미지로 찾아 장바구니에 담았습니다.");
        }, cancellationToken);
    }

    private static GetoAutomationResult ClickMenuItemByImage(IntPtr window, Rectangle bounds, string itemName, CancellationToken cancellationToken)
    {
        var itemKind = ResolveItemKind(itemName);
        if (itemKind is null)
        {
            return new GetoAutomationResult(false, $"{itemName}은 아직 이미지 클릭 규칙이 없습니다.");
        }

        if (!OpenFoodOrderScreen(window, bounds, cancellationToken))
        {
            return new GetoAutomationResult(false, "음식주문 화면을 열지 못했습니다.");
        }

        if (itemKind == "cola")
        {
            DragOrderScrollBarDown(window, bounds);
            Thread.Sleep(500);
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BringToFront(window, bounds))
            {
                return new GetoAutomationResult(false, "Geto Mock 창이 전면이 아니라 이미지 클릭을 중단했습니다.");
            }

            using var screenshot = Capture(bounds);

            var target = itemKind switch
            {
                "cola" => FindColorCluster(screenshot, bounds, Color.FromArgb(139, 23, 48), minYRatio: 0.25),
                "ragongtan" => FindApproximateMenuTextBand(screenshot, bounds, verticalOrder: 0),
                "raudon" => FindApproximateMenuTextBand(screenshot, bounds, verticalOrder: 1),
                _ => null
            };

            if (target is not null)
            {
                SaveDebugCapture(screenshot, bounds, target.Value, $"item-{itemKind}-image");
                ClickReliable(window, target.Value.X, target.Value.Y);
                Thread.Sleep(350);
                return new GetoAutomationResult(true, $"{itemName}을 이미지로 찾아 클릭했습니다.");
            }

            var fallbackTarget = GetFallbackItemPoint(itemKind, bounds);
            if (fallbackTarget is not null && attempt >= 2)
            {
                SaveDebugCapture(screenshot, bounds, fallbackTarget.Value, $"item-{itemKind}-fallback");
                ClickReliable(window, fallbackTarget.Value.X, fallbackTarget.Value.Y);
                Thread.Sleep(350);
                return new GetoAutomationResult(true, $"{itemName}을 fallback 좌표로 클릭했습니다.");
            }

            ScrollWindow(bounds, delta: -5);
            Thread.Sleep(300);
        }

        return new GetoAutomationResult(false, $"{itemName}을 화면 이미지에서 찾지 못했습니다.");
    }

    private static bool OpenFoodOrderScreen(IntPtr window, Rectangle bounds, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 1; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BringToFront(window, bounds))
            {
                return false;
            }

            using var screenshot = Capture(bounds);
            if (!ClickFoodOrderByImage(window, screenshot, bounds))
            {
                return false;
            }

            Thread.Sleep(700);
        }

        return true;
    }

    private static string? ResolveItemKind(string itemName)
    {
        return itemName switch
        {
            "\uCF5C\uB77C" => "cola",
            "\uB77C\uACF5\uD0C4" => "ragongtan",
            "\uC77C\uBC18 \uB77C\uBA74" => "ragongtan",
            "\uB77C\uC6B0\uB3D9" => "raudon",
            _ => null
        };
    }

    private static bool ClickFoodOrderByImage(IntPtr window, Bitmap screenshot, Rectangle bounds)
    {
        var yStart = Math.Max(0, bounds.Height / 6);
        var yEnd = Math.Min(bounds.Height, bounds.Height / 3);
        var xStart = bounds.Width / 4;
        var xEnd = bounds.Width - 10;
        var visited = new bool[screenshot.Width, screenshot.Height];
        var clusters = new List<Rectangle>();

        for (var y = yStart; y < yEnd; y += 4)
        {
            for (var x = xStart; x < xEnd; x += 4)
            {
                if (visited[x, y] || !IsWhiteButtonPixel(screenshot.GetPixel(x, y)))
                {
                    continue;
                }

                var cluster = FloodWhiteCluster(screenshot, visited, x, y, xStart, xEnd, yStart, yEnd);
                if (cluster.Width > 160 && cluster.Height > 24)
                {
                    clusters.Add(cluster);
                }
            }
        }

        var target = clusters
            .OrderByDescending(cluster => cluster.X)
            .FirstOrDefault();
        Point clickPoint;
        if (target == Rectangle.Empty)
        {
            clickPoint = new Point(
                bounds.Left + (int)(bounds.Width * 0.765),
                bounds.Top + (int)(bounds.Height * 0.225));
            SaveDebugCapture(screenshot, bounds, clickPoint, "food-order-fallback");
            ClickReliable(window, clickPoint.X, clickPoint.Y);
            return true;
        }

        clickPoint = new Point(bounds.Left + target.X + target.Width / 2, bounds.Top + target.Y + target.Height / 2);
        SaveDebugCapture(screenshot, bounds, clickPoint, "food-order-image");
        ClickReliable(window, clickPoint.X, clickPoint.Y);
        return true;
    }

    private static Point? GetFallbackItemPoint(string itemKind, Rectangle bounds)
    {
        return itemKind switch
        {
            "cola" => new Point(bounds.Left + (int)(bounds.Width * 0.30), bounds.Top + (int)(bounds.Height * 0.62)),
            "ragongtan" => new Point(bounds.Left + (int)(bounds.Width * 0.43), bounds.Top + (int)(bounds.Height * 0.43)),
            "raudon" => new Point(bounds.Left + (int)(bounds.Width * 0.43), bounds.Top + (int)(bounds.Height * 0.43)),
            _ => null
        };
    }

    private static Rectangle FloodWhiteCluster(Bitmap screenshot, bool[,] visited, int startX, int startY, int minX, int maxX, int minY, int maxY)
    {
        var queue = new Queue<Point>();
        queue.Enqueue(new Point(startX, startY));
        visited[startX, startY] = true;
        var left = startX;
        var right = startX;
        var top = startY;
        var bottom = startY;

        while (queue.Count > 0)
        {
            var point = queue.Dequeue();
            left = Math.Min(left, point.X);
            right = Math.Max(right, point.X);
            top = Math.Min(top, point.Y);
            bottom = Math.Max(bottom, point.Y);

            foreach (var next in new[]
                     {
                         new Point(point.X + 4, point.Y),
                         new Point(point.X - 4, point.Y),
                         new Point(point.X, point.Y + 4),
                         new Point(point.X, point.Y - 4)
                     })
            {
                if (next.X < minX || next.X >= maxX || next.Y < minY || next.Y >= maxY)
                {
                    continue;
                }

                if (visited[next.X, next.Y] || !IsWhiteButtonPixel(screenshot.GetPixel(next.X, next.Y)))
                {
                    continue;
                }

                visited[next.X, next.Y] = true;
                queue.Enqueue(next);
            }
        }

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static bool IsWhiteButtonPixel(Color pixel)
    {
        return pixel.R > 235 && pixel.G > 235 && pixel.B > 235;
    }

    private static Point? FindColorCluster(Bitmap screenshot, Rectangle bounds, Color targetColor, double minYRatio)
    {
        var minX = (int)(bounds.Width * 0.22);
        var maxX = (int)(bounds.Width * 0.72);
        var minY = (int)(bounds.Height * minYRatio);
        var maxY = (int)(bounds.Height * 0.94);
        var visited = new bool[screenshot.Width, screenshot.Height];
        var clusters = new List<List<Point>>();

        for (var y = minY; y < maxY; y += 3)
        {
            for (var x = minX; x < maxX; x += 3)
            {
                if (visited[x, y] || ColorDistance(screenshot.GetPixel(x, y), targetColor) >= 60)
                {
                    continue;
                }

                var cluster = FloodColorCluster(screenshot, visited, x, y, minX, maxX, minY, maxY, targetColor);
                if (cluster.Count >= 12)
                {
                    clusters.Add(cluster);
                }
            }
        }

        var best = clusters.OrderByDescending(cluster => cluster.Count).FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        var centerX = (int)best.Average(point => point.X) + bounds.Left;
        var centerY = (int)best.Average(point => point.Y) + bounds.Top;
        return new Point(centerX, centerY);
    }

    private static List<Point> FloodColorCluster(Bitmap screenshot, bool[,] visited, int startX, int startY, int minX, int maxX, int minY, int maxY, Color targetColor)
    {
        var points = new List<Point>();
        var queue = new Queue<Point>();
        queue.Enqueue(new Point(startX, startY));
        visited[startX, startY] = true;

        while (queue.Count > 0)
        {
            var point = queue.Dequeue();
            points.Add(point);

            foreach (var next in new[]
                     {
                         new Point(point.X + 3, point.Y),
                         new Point(point.X - 3, point.Y),
                         new Point(point.X, point.Y + 3),
                         new Point(point.X, point.Y - 3)
                     })
            {
                if (next.X < minX || next.X >= maxX || next.Y < minY || next.Y >= maxY)
                {
                    continue;
                }

                if (visited[next.X, next.Y] || ColorDistance(screenshot.GetPixel(next.X, next.Y), targetColor) >= 60)
                {
                    continue;
                }

                visited[next.X, next.Y] = true;
                queue.Enqueue(next);
            }
        }

        return points;
    }

    private static Point? FindApproximateMenuTextBand(Bitmap screenshot, Rectangle bounds, int verticalOrder)
    {
        // Mock ramen cards are mostly light card regions in the menu area. This is intentionally visual.
        var xStart = (int)(bounds.Width * 0.35);
        var xEnd = (int)(bounds.Width * 0.72);
        var yStart = (int)(bounds.Height * 0.20);
        var yEnd = (int)(bounds.Height * 0.72);

        var rows = new List<int>();
        for (var y = yStart; y < yEnd; y += 4)
        {
            var bright = 0;
            for (var x = xStart; x < xEnd; x += 4)
            {
                var pixel = screenshot.GetPixel(x, y);
                if (pixel.R > 220 && pixel.G > 220 && pixel.B > 220)
                {
                    bright++;
                }
            }

            if (bright > 20)
            {
                rows.Add(y);
            }
        }

        var bands = ToBands(rows);
        if (bands.Count <= verticalOrder)
        {
            return null;
        }

        var band = bands[verticalOrder];
        var centerY = bounds.Top + (band.Start + band.End) / 2;
        var centerX = bounds.Left + (int)(bounds.Width * 0.48);
        return new Point(centerX, centerY);
    }

    private static List<(int Start, int End)> ToBands(List<int> rows)
    {
        var bands = new List<(int Start, int End)>();
        if (rows.Count == 0)
        {
            return bands;
        }

        var start = rows[0];
        var previous = rows[0];
        foreach (var row in rows.Skip(1))
        {
            if (row - previous > 12)
            {
                bands.Add((start, previous));
                start = row;
            }

            previous = row;
        }

        bands.Add((start, previous));
        return bands.Where(band => band.End - band.Start > 24).ToList();
    }

    private static Bitmap Capture(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        return bitmap;
    }

    private static double ColorDistance(Color a, Color b)
    {
        var r = a.R - b.R;
        var g = a.G - b.G;
        var bl = a.B - b.B;
        return Math.Sqrt(r * r + g * g + bl * bl);
    }

    private static void SaveDebugCapture(Bitmap screenshot, Rectangle bounds, Point screenPoint, string label)
    {
        try
        {
            var root = FindWorkspaceRoot();
            var dir = Path.Combine(root, "logs", "vision");
            Directory.CreateDirectory(dir);
            using var copy = new Bitmap(screenshot);
            using var graphics = Graphics.FromImage(copy);
            var localX = screenPoint.X - bounds.Left;
            var localY = screenPoint.Y - bounds.Top;
            using var pen = new Pen(Color.Red, 4);
            graphics.DrawLine(pen, localX - 18, localY, localX + 18, localY);
            graphics.DrawLine(pen, localX, localY - 18, localX, localY + 18);
            var seq = Interlocked.Increment(ref _debugSequence);
            copy.Save(Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{seq:000}-{label}.png"));
            File.AppendAllText(Path.Combine(dir, "clicks.log"), $"{DateTime.Now:O} {label} x={screenPoint.X} y={screenPoint.Y} window={bounds}\n");
        }
        catch
        {
            // Debug capture must not block the hand action.
        }
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "logs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static void ClickReliable(IntPtr window, int x, int y)
    {
        if (GetWindowRect(window, out var rect))
        {
            BringToFront(window, rect.ToRectangle());
        }

        Thread.Sleep(120);
        SetCursorPos(x, y);
        Thread.Sleep(80);
        var inputs = new Input[2];
        inputs[0].Type = InputMouse;
        inputs[0].Mouse.Flags = MouseEventLeftDown;
        inputs[1].Type = InputMouse;
        inputs[1].Mouse.Flags = MouseEventLeftUp;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static bool BringToFront(IntPtr window, Rectangle bounds)
    {
        ShowWindow(window, ShowRestore);
        SetWindowPos(window, HwndTopMost, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpShowWindow);
        Thread.Sleep(150);
        SetForegroundWindow(window);
        Thread.Sleep(150);
        SetWindowPos(window, HwndNoTopMost, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpShowWindow);
        Thread.Sleep(150);
        return true;
    }

    private static void ScrollWindow(Rectangle bounds, int delta)
    {
        SetCursorPos(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        mouse_event(MouseEventWheel, 0, 0, delta * 120, UIntPtr.Zero);
    }

    private static void DragOrderScrollBarDown(IntPtr window, Rectangle bounds)
    {
        BringToFront(window, bounds);
        var x = bounds.Left + (int)(bounds.Width * 0.60);
        var startY = bounds.Top + (int)(bounds.Height * 0.34);
        var endY = bounds.Top + (int)(bounds.Height * 0.64);

        SetCursorPos(x, startY);
        Thread.Sleep(80);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(80);

        for (var i = 1; i <= 12; i++)
        {
            var y = startY + (endY - startY) * i / 12;
            SetCursorPos(x, y);
            Thread.Sleep(20);
        }

        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    private const int InputMouse = 0;
    private const int ShowRestore = 9;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventWheel = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public Rectangle ToRectangle()
        {
            return Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }
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
}

public sealed record GetoAutomationResult(bool Success, string Message);
