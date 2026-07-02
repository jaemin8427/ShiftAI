using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using ShiftAI.Core;

namespace ShiftAI.App;

public interface IGetoOrderAdapter
{
    string Name { get; }
    Task<GetoAutomationResult> OrderAsync(CartSnapshot cart, CancellationToken cancellationToken = default);
}

public sealed class GetoNativeOrderAdapter : IGetoOrderAdapter
{
    public string Name => "geto-native-adapter";

    public Task<GetoAutomationResult> OrderAsync(CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var wmclt = Process.GetProcessesByName("WmClt")
            .OrderByDescending(process => process.MainWindowHandle != IntPtr.Zero)
            .FirstOrDefault();
        if (wmclt is null)
        {
            return Task.FromResult(new GetoAutomationResult(false, "WmClt.exe not found. Geto native order host is not running."));
        }

        var childProcesses = GetChildProcesses(wmclt.Id);
        var webViewRoots = FindGetoWebViewRoots();
        var menuBeltRoot = webViewRoots.FirstOrDefault(root =>
            root.Owner.Equals("wmclt.exe", StringComparison.OrdinalIgnoreCase) ||
            root.LatestUrl.Contains("/ad/menu/belt", StringComparison.OrdinalIgnoreCase));
        var gameHubRoot = webViewRoots.FirstOrDefault(root =>
            root.Owner.Equals("GameHub.exe", StringComparison.OrdinalIgnoreCase));

        var diagnostics = new
        {
            wmclt = new
            {
                wmclt.Id,
                wmclt.MainWindowTitle,
                hasWindow = wmclt.MainWindowHandle != IntPtr.Zero
            },
            childProcesses,
            webViewRoots,
            cart = cart.Lines.Select(line => new
            {
                name = line.Item.Name,
                line.Quantity,
                line.Item.Price,
                line.Total
            }),
            total = cart.Total
        };

        var message = menuBeltRoot is not null
            ? $"WmClt native host found. Its WebView root appears to be menu banner/ad only ({menuBeltRoot.Path}); direct native order command is not discovered yet."
            : gameHubRoot is not null
                ? $"GameHub WebView root found ({gameHubRoot.Path}), but food order host appears to be WmClt native UI. Direct native order command is not discovered yet."
                : "WmClt native host found, but no actionable Geto WebView/order command surface was discovered.";

        return Task.FromResult(new GetoAutomationResult(false, message, diagnostics));
    }

    private static List<object> GetChildProcesses(int parentProcessId)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process WHERE ParentProcessId = {parentProcessId}");

            return searcher.Get()
                .Cast<System.Management.ManagementObject>()
                .Select(obj => new
                {
                    processId = Convert.ToInt32(obj["ProcessId"]),
                    name = Convert.ToString(obj["Name"]) ?? "",
                    path = Convert.ToString(obj["ExecutablePath"]) ?? "",
                    commandLine = Convert.ToString(obj["CommandLine"]) ?? ""
                })
                .Cast<object>()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<GetoWebViewRootInfo> FindGetoWebViewRoots()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData",
            "WebView");

        if (!Directory.Exists(baseDir))
        {
            return [];
        }

        return Directory.GetDirectories(baseDir, "Geto_*")
            .Select(path => Path.Combine(path, "EBWebView"))
            .Where(Directory.Exists)
            .Select(path => new GetoWebViewRootInfo(
                GuessOwnerFromProcesses(path),
                path,
                TryReadLatestHistoryUrl(path),
                Directory.GetLastWriteTime(path)))
            .OrderByDescending(root => root.LastWriteTime)
            .ToList();
    }

    private static string GuessOwnerFromProcesses(string rootPath)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'");

            foreach (var obj in searcher.Get().Cast<System.Management.ManagementObject>())
            {
                var commandLine = Convert.ToString(obj["CommandLine"]) ?? "";
                if (!commandLine.Contains(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var match = System.Text.RegularExpressions.Regex.Match(commandLine, @"--webview-exe-name=([^ ]+)");
                return match.Success ? match.Groups[1].Value.Trim('"') : "unknown";
            }
        }
        catch
        {
        }

        return "unknown";
    }

    private static string TryReadLatestHistoryUrl(string rootPath)
    {
        var history = Path.Combine(rootPath, "Default", "History");
        if (!File.Exists(history))
        {
            return "";
        }

        try
        {
            var bytes = File.ReadAllBytes(history);
            var text = Encoding.UTF8.GetString(bytes);
            var matches = System.Text.RegularExpressions.Regex.Matches(text, @"https?://[^\s""'<>\\\u0000]+");
            return matches.Count == 0 ? "" : matches[^1].Value;
        }
        catch
        {
            return "";
        }
    }

    private sealed record GetoWebViewRootInfo(string Owner, string Path, string LatestUrl, DateTime LastWriteTime);
}

public sealed class GetoUiAutomationOrderAdapter : IGetoOrderAdapter
{
    public string Name => "windows-ui-automation";

    public Task<GetoAutomationResult> OrderAsync(CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!IsSupportedCart(cart))
            {
                return new GetoAutomationResult(false, "UI Automation adapter currently supports one visible menu item at a time.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var window = GetoWindowFinder.FindGameHubWindow();
            if (window == IntPtr.Zero)
            {
                return new GetoAutomationResult(false, "GameHub window not found for UI Automation.");
            }

            if (!GetoDesktopAutomation.GetWindowRect(window, out var rect))
            {
                return new GetoAutomationResult(false, "Could not read GameHub window bounds for UI Automation.");
            }

            GetoDesktopAutomation.BringToFront(window, rect.ToRectangle());

            var root = AutomationElement.FromHandle(window);
            if (root is null)
            {
                return new GetoAutomationResult(false, "GameHub UI Automation root was empty.");
            }

            var webViewRoot = FindWebViewRoot(root) ?? root;
            SaveTreeDebug(webViewRoot, "before-order");

            var itemName = cart.Lines[0].Item.Name;
            var quantity = cart.Lines[0].Quantity;

            if (!InvokeByText(webViewRoot, ["음식", "푸드", "주문", "먹거리", "Food", "Order"], cancellationToken))
            {
                return FailWithTree(webViewRoot, "Could not find a food/order entry inside the GameHub WebView UIA tree.");
            }

            WaitForTreeRefresh();
            webViewRoot = FindWebViewRoot(root) ?? root;

            TrySetSearchText(webViewRoot, itemName, cancellationToken);
            WaitForTreeRefresh();
            webViewRoot = FindWebViewRoot(root) ?? root;

            if (!InvokeByText(webViewRoot, [itemName, NormalizeMenuAlias(itemName)], cancellationToken))
            {
                return FailWithTree(webViewRoot, $"Could not find menu item '{itemName}' in the WebView UIA tree.");
            }

            for (var i = 1; i < quantity; i++)
            {
                WaitForTreeRefresh(milliseconds: 250);
                webViewRoot = FindWebViewRoot(root) ?? root;
                if (!InvokeByText(webViewRoot, ["+", "추가", "수량"], cancellationToken))
                {
                    return FailWithTree(webViewRoot, $"Could not increase '{itemName}' quantity to {quantity}.");
                }
            }

            WaitForTreeRefresh();
            webViewRoot = FindWebViewRoot(root) ?? root;

            if (!InvokeByText(webViewRoot, ["현장", "카드", "신용", "Credit", "Card"], cancellationToken))
            {
                return FailWithTree(webViewRoot, "Could not select pay-at-seat card method in the WebView UIA tree.");
            }

            WaitForTreeRefresh(milliseconds: 350);
            webViewRoot = FindWebViewRoot(root) ?? root;

            if (!InvokeByText(webViewRoot, ["주문", "결제", "확인", "Order", "Pay"], cancellationToken))
            {
                return FailWithTree(webViewRoot, "Could not find final order/submit button in the WebView UIA tree.");
            }

            return new GetoAutomationResult(true, "UI Automation controlled the GameHub WebView tree and submitted pay-at-seat card order.");
        }, cancellationToken);
    }

    private static bool IsSupportedCart(CartSnapshot cart)
    {
        return cart.Lines.Count == 1 && cart.Lines[0].Quantity >= 1;
    }

    private static AutomationElement? FindWebViewRoot(AutomationElement root)
    {
        var descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .ToList();

        return descendants
            .Where(IsLikelyWebViewElement)
            .OrderByDescending(GetArea)
            .FirstOrDefault()
            ?? descendants
                .Where(IsPaneOrDocument)
                .OrderByDescending(GetArea)
                .FirstOrDefault();
    }

    private static bool IsLikelyWebViewElement(AutomationElement element)
    {
        var name = GetName(element);
        var className = GetClassName(element);
        var automationId = GetAutomationId(element);
        var combined = $"{name} {className} {automationId}";

        return ContainsAny(combined, "WebView", "Chrome", "Edge", "Internet Explorer_Server", "Chrome_RenderWidgetHostHWND")
            || SafeGet(element, AutomationElement.ControlTypeProperty) == ControlType.Document;
    }

    private static bool IsPaneOrDocument(AutomationElement element)
    {
        var controlType = SafeGet(element, AutomationElement.ControlTypeProperty);
        return Equals(controlType, ControlType.Pane) || Equals(controlType, ControlType.Document);
    }

    private static bool TrySetSearchText(AutomationElement root, string text, CancellationToken cancellationToken)
    {
        var searchBox = FindEditableElement(root, ["검색", "search", "메뉴", "상품"]);
        if (searchBox is null)
        {
            searchBox = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>()
                .OrderBy(GetTop)
                .FirstOrDefault();
        }

        if (searchBox is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryFocus(searchBox))
        {
            return false;
        }

        if (searchBox.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj)
            && valuePatternObj is ValuePattern valuePattern)
        {
            valuePattern.SetValue(text);
            return true;
        }

        GetoDesktopAutomation.SendCtrlA();
        Thread.Sleep(50);
        GetoDesktopAutomation.SendUnicodeText(text);
        Thread.Sleep(100);
        GetoDesktopAutomation.SendVirtualKey(GetoDesktopAutomation.VkReturn);
        return true;
    }

    private static AutomationElement? FindEditableElement(AutomationElement root, string[] terms)
    {
        return root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(element => SafeGet(element, AutomationElement.ControlTypeProperty) == ControlType.Edit)
            .Where(element => TextMatches(element, terms))
            .OrderBy(GetTop)
            .FirstOrDefault();
    }

    private static bool InvokeByText(AutomationElement root, string[] terms, CancellationToken cancellationToken)
    {
        var target = FindClickableByText(root, terms);
        if (target is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return InvokeOrClick(target);
    }

    private static AutomationElement? FindClickableByText(AutomationElement root, string[] terms)
    {
        return root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(element => TextMatches(element, terms))
            .OrderByDescending(IsDirectlyInvokable)
            .ThenByDescending(GetArea)
            .FirstOrDefault();
    }

    private static bool InvokeOrClick(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObj)
            && invokeObj is InvokePattern invokePattern)
        {
            invokePattern.Invoke();
            return true;
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionObj)
            && selectionObj is SelectionItemPattern selectionPattern)
        {
            selectionPattern.Select();
            return true;
        }

        if (!TryFocus(element))
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(element);
            if (parent is not null && IsDirectlyInvokable(parent))
            {
                return InvokeOrClick(parent);
            }
        }

        return TryClickCenter(element);
    }

    private static bool TryFocus(AutomationElement element)
    {
        try
        {
            element.SetFocus();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryClickCenter(AutomationElement element)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1)
            {
                return false;
            }

        GetoDesktopAutomation.Click(new Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2)));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TextMatches(AutomationElement element, string[] terms)
    {
        var text = $"{GetName(element)} {GetAutomationId(element)} {GetClassName(element)}";
        return terms.Where(term => !string.IsNullOrWhiteSpace(term))
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDirectlyInvokable(AutomationElement element)
    {
        return element.TryGetCurrentPattern(InvokePattern.Pattern, out _)
            || element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)
            || IsClickableControlType(element);
    }

    private static bool IsClickableControlType(AutomationElement element)
    {
        var controlType = SafeGet(element, AutomationElement.ControlTypeProperty);
        return Equals(controlType, ControlType.Button)
            || Equals(controlType, ControlType.ListItem)
            || Equals(controlType, ControlType.MenuItem)
            || Equals(controlType, ControlType.Hyperlink);
    }

    private static GetoAutomationResult FailWithTree(AutomationElement root, string message)
    {
        SaveTreeDebug(root, "failure");
        return new GetoAutomationResult(false, message);
    }

    private static void SaveTreeDebug(AutomationElement root, string label)
    {
        try
        {
            var dir = Path.Combine(GetoDesktopAutomation.FindWorkspaceRoot(), "logs", "uia");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}.txt");
            var builder = new StringBuilder();
            AppendTree(root, builder, 0, maxDepth: 7, maxNodes: 600, new NodeCounter());
            File.WriteAllText(path, builder.ToString());
        }
        catch
        {
            // UIA debug capture must not block adapter fallback.
        }
    }

    private static void AppendTree(AutomationElement element, StringBuilder builder, int depth, int maxDepth, int maxNodes, NodeCounter counter)
    {
        if (depth > maxDepth || counter.Count >= maxNodes)
        {
            return;
        }

        counter.Count++;
        var indent = new string(' ', depth * 2);
        var controlType = SafeGet(element, AutomationElement.ControlTypeProperty) as ControlType;
        builder.Append(indent)
            .Append(controlType?.ProgrammaticName ?? "ControlType.Unknown")
            .Append(" | name=\"").Append(GetName(element))
            .Append("\" | aid=\"").Append(GetAutomationId(element))
            .Append("\" | class=\"").Append(GetClassName(element))
            .Append("\" | bounds=").Append(GetBoundsText(element))
            .AppendLine();

        var children = element.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>();
        foreach (var child in children)
        {
            AppendTree(child, builder, depth + 1, maxDepth, maxNodes, counter);
        }
    }

    private static string NormalizeMenuAlias(string itemName)
    {
        return itemName switch
        {
            var value when value.Contains("콜라", StringComparison.OrdinalIgnoreCase) => "COLA",
            var value when value.Contains("아이스티", StringComparison.OrdinalIgnoreCase) => "ICE TEA",
            _ => itemName.Replace(" ", "", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string GetName(AutomationElement element)
    {
        return SafeGet(element, AutomationElement.NameProperty) as string ?? "";
    }

    private static string GetClassName(AutomationElement element)
    {
        return SafeGet(element, AutomationElement.ClassNameProperty) as string ?? "";
    }

    private static string GetAutomationId(AutomationElement element)
    {
        return SafeGet(element, AutomationElement.AutomationIdProperty) as string ?? "";
    }

    private static string GetBoundsText(AutomationElement element)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            return $"{bounds.Left:N0},{bounds.Top:N0},{bounds.Width:N0}x{bounds.Height:N0}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static object? SafeGet(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property, true);
            return value == AutomationElement.NotSupported ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static double GetArea(AutomationElement element)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            return Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
        }
        catch
        {
            return 0;
        }
    }

    private static double GetTop(AutomationElement element)
    {
        try
        {
            return element.Current.BoundingRectangle.Top;
        }
        catch
        {
            return double.MaxValue;
        }
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static void WaitForTreeRefresh(int milliseconds = 700)
    {
        Thread.Sleep(milliseconds);
    }

    private sealed class NodeCounter
    {
        public int Count { get; set; }
    }
}

public sealed class GetoVisionMouseFallbackOrderAdapter : IGetoOrderAdapter
{
    public string Name => "vision-mouse-fallback";

    public Task<GetoAutomationResult> OrderAsync(CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!IsSupportedCart(cart))
            {
                return new GetoAutomationResult(false, "Vision fallback requires at least one food item with quantity greater than zero.");
            }

            TrySetDpiAware();
            var window = GetoWindowFinder.FindGameHubWindow();
            if (window == IntPtr.Zero)
            {
                return new GetoAutomationResult(false, "GameHub window not found.");
            }

            if (!GetWindowRect(window, out var rect))
            {
                return new GetoAutomationResult(false, "Could not read GameHub window bounds.");
            }

            var bounds = rect.ToRectangle();
            BringToFront(window, bounds);

            cancellationToken.ThrowIfCancellationRequested();
            ClickFoodOrder(bounds);
            Thread.Sleep(900);

            var clickIndex = 1;
            foreach (var line in cart.Lines)
            {
                for (var quantity = 0; quantity < line.Quantity; quantity++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SearchMenuItem(bounds, line.Item.Name, clickIndex);
                    Thread.Sleep(900);

                    cancellationToken.ThrowIfCancellationRequested();
                    ClickFirstProduct(bounds, line.Item.Name, clickIndex);
                    Thread.Sleep(700);
                    clickIndex++;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            ClickCreditCard(bounds);
            Thread.Sleep(350);

            cancellationToken.ThrowIfCancellationRequested();
            ClickOrderButton(bounds);

            var itemSummary = string.Join(", ", cart.Lines.Select(line => $"{line.Item.Name} x{line.Quantity}"));
            return new GetoAutomationResult(true, $"Vision fallback sent food order ({itemSummary}) with pay-at-seat card method.");
        }, cancellationToken);
    }

    private static bool IsSupportedCart(CartSnapshot cart)
    {
        return cart.Lines.Count > 0
            && cart.Lines.All(line => !string.IsNullOrWhiteSpace(line.Item.Name) && line.Quantity > 0);
    }

    private static void ClickFoodOrder(Rectangle bounds)
    {
        using var screenshot = Capture(bounds);
        var point = FindFoodOrderButton(screenshot, bounds)
            ?? RatioPoint(bounds, 0.755, 0.205);
        SaveDebug(screenshot, bounds, point, "01-food-order");
        Click(point);
    }

    private static void SearchMenuItem(Rectangle bounds, string itemName, int index)
    {
        var point = RatioPoint(bounds, 0.075, 0.055);
        using (var screenshot = Capture(bounds))
        {
            SaveDebug(screenshot, bounds, point, $"{index:00}-search-{SafeDebugName(itemName)}");
        }

        Click(point);
        Thread.Sleep(100);
        SendCtrlA();
        Thread.Sleep(50);
        SendUnicodeText(itemName);
        Thread.Sleep(150);
        SendVirtualKey(VkReturn);
    }

    private static void ClickFirstProduct(Rectangle bounds, string itemName, int index)
    {
        using var screenshot = Capture(bounds);
        var point = FindFirstProductCard(screenshot, bounds)
            ?? RatioPoint(bounds, 0.285, 0.305);
        SaveDebug(screenshot, bounds, point, $"{index:00}-product-{SafeDebugName(itemName)}");
        Click(point);
    }

    private static void ClickCreditCard(Rectangle bounds)
    {
        using var screenshot = Capture(bounds);
        var point = FindPaymentButton(screenshot, bounds)
            ?? RatioPoint(bounds, 0.835, 0.735);
        SaveDebug(screenshot, bounds, point, "04-credit-card");
        Click(point);
    }

    private static void ClickOrderButton(Rectangle bounds)
    {
        using var screenshot = Capture(bounds);
        var point = FindBlueButton(screenshot, bounds)
            ?? RatioPoint(bounds, 0.845, 0.925);
        SaveDebug(screenshot, bounds, point, "05-submit-order");
        Click(point);
    }

    private static Point? FindFoodOrderButton(Bitmap screenshot, Rectangle bounds)
    {
        var clusters = FindClusters(
            screenshot,
            bounds,
            minXRatio: 0.35,
            maxXRatio: 0.98,
            minYRatio: 0.13,
            maxYRatio: 0.32,
            IsWhitePixel,
            step: 4,
            minWidth: bounds.Width * 0.18,
            minHeight: bounds.Height * 0.04);

        var target = clusters.OrderByDescending(cluster => cluster.X).FirstOrDefault();
        return target == Rectangle.Empty ? null : Center(bounds, target);
    }

    private static Point? FindFirstProductCard(Bitmap screenshot, Rectangle bounds)
    {
        var clusters = FindClusters(
            screenshot,
            bounds,
            minXRatio: 0.13,
            maxXRatio: 0.72,
            minYRatio: 0.12,
            maxYRatio: 0.65,
            IsWhitePixel,
            step: 5,
            minWidth: bounds.Width * 0.08,
            minHeight: bounds.Height * 0.08);

        var target = clusters.OrderBy(cluster => cluster.Y).ThenBy(cluster => cluster.X).FirstOrDefault();
        return target == Rectangle.Empty ? null : Center(bounds, target);
    }

    private static Point? FindPaymentButton(Bitmap screenshot, Rectangle bounds)
    {
        var clusters = FindClusters(
            screenshot,
            bounds,
            minXRatio: 0.72,
            maxXRatio: 0.98,
            minYRatio: 0.62,
            maxYRatio: 0.82,
            IsGrayPaymentPixel,
            step: 4,
            minWidth: bounds.Width * 0.055,
            minHeight: bounds.Height * 0.035);

        var target = clusters
            .OrderBy(cluster => Math.Abs((cluster.X + cluster.Width / 2.0) - bounds.Width * 0.835))
            .FirstOrDefault();
        return target == Rectangle.Empty ? null : Center(bounds, target);
    }

    private static Point? FindBlueButton(Bitmap screenshot, Rectangle bounds)
    {
        var clusters = FindClusters(
            screenshot,
            bounds,
            minXRatio: 0.68,
            maxXRatio: 0.99,
            minYRatio: 0.80,
            maxYRatio: 0.99,
            IsBlueOrderPixel,
            step: 4,
            minWidth: bounds.Width * 0.10,
            minHeight: bounds.Height * 0.04);

        var target = clusters.OrderByDescending(cluster => cluster.Width * cluster.Height).FirstOrDefault();
        return target == Rectangle.Empty ? null : Center(bounds, target);
    }

    private static List<Rectangle> FindClusters(
        Bitmap screenshot,
        Rectangle bounds,
        double minXRatio,
        double maxXRatio,
        double minYRatio,
        double maxYRatio,
        Func<Color, bool> match,
        int step,
        double minWidth,
        double minHeight)
    {
        var minX = (int)(bounds.Width * minXRatio);
        var maxX = (int)(bounds.Width * maxXRatio);
        var minY = (int)(bounds.Height * minYRatio);
        var maxY = (int)(bounds.Height * maxYRatio);
        var visited = new bool[screenshot.Width, screenshot.Height];
        var clusters = new List<Rectangle>();

        for (var y = minY; y < maxY; y += step)
        {
            for (var x = minX; x < maxX; x += step)
            {
                if (visited[x, y] || !match(screenshot.GetPixel(x, y)))
                {
                    continue;
                }

                var cluster = Flood(screenshot, visited, x, y, minX, maxX, minY, maxY, match, step);
                if (cluster.Width >= minWidth && cluster.Height >= minHeight)
                {
                    clusters.Add(cluster);
                }
            }
        }

        return clusters;
    }

    private static Rectangle Flood(
        Bitmap screenshot,
        bool[,] visited,
        int startX,
        int startY,
        int minX,
        int maxX,
        int minY,
        int maxY,
        Func<Color, bool> match,
        int step)
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
                         new Point(point.X + step, point.Y),
                         new Point(point.X - step, point.Y),
                         new Point(point.X, point.Y + step),
                         new Point(point.X, point.Y - step)
                     })
            {
                if (next.X < minX || next.X >= maxX || next.Y < minY || next.Y >= maxY)
                {
                    continue;
                }

                if (visited[next.X, next.Y] || !match(screenshot.GetPixel(next.X, next.Y)))
                {
                    continue;
                }

                visited[next.X, next.Y] = true;
                queue.Enqueue(next);
            }
        }

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static bool IsWhitePixel(Color pixel)
    {
        return pixel.R > 230 && pixel.G > 230 && pixel.B > 230;
    }

    private static bool IsGrayPaymentPixel(Color pixel)
    {
        return pixel.R is >= 70 and <= 150 &&
               pixel.G is >= 70 and <= 150 &&
               pixel.B is >= 70 and <= 150;
    }

    private static bool IsBlueOrderPixel(Color pixel)
    {
        return pixel.B > 170 && pixel.G > 100 && pixel.R < 90;
    }

    private static Point Center(Rectangle bounds, Rectangle local)
    {
        return new Point(bounds.Left + local.X + local.Width / 2, bounds.Top + local.Y + local.Height / 2);
    }

    private static Point RatioPoint(Rectangle bounds, double x, double y)
    {
        return new Point(bounds.Left + (int)(bounds.Width * x), bounds.Top + (int)(bounds.Height * y));
    }

    private static Bitmap Capture(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        return bitmap;
    }

    private static void Click(Point point)
    {
        SetCursorPos(point.X, point.Y);
        Thread.Sleep(80);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    private static void SendCtrlA()
    {
        SendVirtualKey(VkControl, keyDown: true);
        SendVirtualKey(VkA, keyDown: true);
        SendVirtualKey(VkA, keyDown: false);
        SendVirtualKey(VkControl, keyDown: false);
    }

    private static void SendUnicodeText(string text)
    {
        foreach (var ch in text)
        {
            var inputs = new Input[2];
            inputs[0].Type = InputKeyboard;
            inputs[0].Union.Keyboard = new KeyboardInput
            {
                Scan = ch,
                Flags = KeyEventUnicode
            };
            inputs[1].Type = InputKeyboard;
            inputs[1].Union.Keyboard = new KeyboardInput
            {
                Scan = ch,
                Flags = KeyEventUnicode | KeyEventKeyUp
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            Thread.Sleep(20);
        }
    }

    private static void SendVirtualKey(ushort key, bool keyDown = true)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = key,
                    Flags = keyDown ? 0 : KeyEventKeyUp
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    private static void BringToFront(IntPtr window, Rectangle bounds)
    {
        ShowWindow(window, ShowRestore);
        SetWindowPos(window, HwndTopMost, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpShowWindow);
        Thread.Sleep(120);
        SetForegroundWindow(window);
        Thread.Sleep(120);
        SetWindowPos(window, HwndNoTopMost, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpShowWindow);
        Thread.Sleep(120);
    }

    private static void SaveDebug(Bitmap screenshot, Rectangle bounds, Point screenPoint, string label)
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
            using var pen = new Pen(Color.Lime, 4);
            graphics.DrawLine(pen, localX - 18, localY, localX + 18, localY);
            graphics.DrawLine(pen, localX, localY - 18, localX, localY + 18);
            copy.Save(Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}.png"));
        }
        catch
        {
            // Debug capture must not block the order attempt.
        }
    }

    private static string SafeDebugName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        var safe = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "item" : safe;
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
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    private const int ShowRestore = 9;
    private const int InputKeyboard = 1;
    private const uint SwpShowWindow = 0x0040;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VkControl = 0x11;
    private const ushort VkA = 0x41;
    private const ushort VkReturn = 0x0D;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
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
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}

internal static class GetoDesktopAutomation
{
    public const ushort VkReturn = 0x0D;

    public static void Click(Point point)
    {
        SetCursorPos(point.X, point.Y);
        Thread.Sleep(80);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    public static void SendCtrlA()
    {
        SendVirtualKey(VkControl, keyDown: true);
        SendVirtualKey(VkA, keyDown: true);
        SendVirtualKey(VkA, keyDown: false);
        SendVirtualKey(VkControl, keyDown: false);
    }

    public static void SendUnicodeText(string text)
    {
        foreach (var ch in text)
        {
            var inputs = new Input[2];
            inputs[0].Type = InputKeyboard;
            inputs[0].Union.Keyboard = new KeyboardInput
            {
                Scan = ch,
                Flags = KeyEventUnicode
            };
            inputs[1].Type = InputKeyboard;
            inputs[1].Union.Keyboard = new KeyboardInput
            {
                Scan = ch,
                Flags = KeyEventUnicode | KeyEventKeyUp
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            Thread.Sleep(20);
        }
    }

    public static void SendVirtualKey(ushort key, bool keyDown = true)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = key,
                    Flags = keyDown ? 0 : KeyEventKeyUp
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    public static void BringToFront(IntPtr window, Rectangle bounds)
    {
        ShowWindow(window, ShowRestore);
        SetWindowPos(window, HwndTopMost, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpShowWindow);
        Thread.Sleep(120);
        SetForegroundWindow(window);
        Thread.Sleep(120);
        SetWindowPos(window, HwndNoTopMost, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpShowWindow);
        Thread.Sleep(120);
    }

    public static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "logs"))
                || File.Exists(Path.Combine(directory.FullName, "data", "menu.sample.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    public static void TrySetDpiAware()
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
    public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    private const int ShowRestore = 9;
    private const int InputKeyboard = 1;
    private const uint SwpShowWindow = 0x0040;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VkControl = 0x11;
    private const ushort VkA = 0x41;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
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
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}

public static class GetoWindowFinder
{
    public static IntPtr FindGameHubWindow()
    {
        var gameHubPids = Process.GetProcessesByName("GameHub")
            .Select(process => process.Id)
            .ToHashSet();

        if (gameHubPids.Count == 0)
        {
            return IntPtr.Zero;
        }

        var best = IntPtr.Zero;
        var bestArea = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (!gameHubPids.Contains((int)pid) || !IsWindowVisible(window) || !GetWindowRect(window, out var rect))
            {
                return true;
            }

            var bounds = rect.ToRectangle();
            var area = bounds.Width * bounds.Height;
            if (bounds.Width > 300 && bounds.Height > 250 && area > bestArea)
            {
                bestArea = area;
                best = window;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeRect
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
