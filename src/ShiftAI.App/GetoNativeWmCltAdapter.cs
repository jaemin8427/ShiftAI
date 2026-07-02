using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using ShiftAI.Core;

namespace ShiftAI.App;

/// <summary>
/// Drives the real Geto/WmClt native food-order window (class #32770, ~1641x920) that hosts an
/// in-process Chromium (CEF) product grid. Because the web layer ignores synthetic Win32 messages
/// and exposes no CDP/accessibility surface, this adapter uses a "foreground flash": it briefly
/// brings the WmClt window forward, drives it with real input (cursor + SendInput), then restores
/// focus to whatever window (usually the game) was in front.
///
/// Locators:
///  - Search box + payment buttons: located by UIA Name (stable across native/web), clicked at rect center.
///  - Product card + 주문하기(submit): located by window-relative ratio (calibrated on 1641x920 @ 2026-07-02).
///
/// Env toggles:
///  - SHIFT_AI_GETO_AUTOSUBMIT = "0" to stop before pressing 주문하기 (default: submit).
///  - SHIFT_AI_GETO_PAYMENT     = payment button label (default: "게토앱결제").
/// </summary>
public sealed class GetoNativeWmCltAdapter : IGetoOrderAdapter
{
    public string Name => "geto-native-wmclt";

    // Window-relative click ratios calibrated against the 1641x920 order window.
    private const double SearchXRatio = 0.632;   // fallback if UIA can't find 상품명 검색
    private const double SearchYRatio = 0.122;
    private const double FirstCardXRatio = 0.076; // top-left product card in the CEF grid
    private const double FirstCardYRatio = 0.302;
    private const double AddButtonXRatio = 0.076; // the "담기" button that appears on the selected card
    private const double AddButtonYRatio = 0.389;
    private const double SubmitXRatio = 0.899;    // bottom-right blue 주문하기 (measured center)
    private const double SubmitYRatio = 0.947;
    private const double PaymentXRatio = 0.821;   // fallback for 신용카드
    private const double PaymentYRatio = 0.639;
    private const double CloseModalXRatio = 0.575; // fallback for 상품구매 종료하기 in the completion modal
    private const double CloseModalYRatio = 0.700;

    private static bool AutoSubmit =>
        !string.Equals(Environment.GetEnvironmentVariable("SHIFT_AI_GETO_AUTOSUBMIT"), "0", StringComparison.Ordinal);

    private static string PaymentLabel =>
        Environment.GetEnvironmentVariable("SHIFT_AI_GETO_PAYMENT") is { Length: > 0 } value ? value : "신용카드";

    public Task<GetoAutomationResult> OrderAsync(CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (cart.IsEmpty)
            {
                return new GetoAutomationResult(false, "장바구니가 비어 있어 Geto 주문을 시작할 수 없습니다.");
            }

            GetoDesktopAutomation.TrySetDpiAware();

            // Remember the game/foreground window up-front so we can restore it even after opening the order screen.
            var previousForeground = GetForegroundWindow();

            // The order flow only works when the food-order screen is open. If it isn't, open it by
            // clicking "먹거리 주문" on the Geto main window, then wait for the big window to appear.
            var window = FindWmCltOrderWindow();
            if (window == IntPtr.Zero)
            {
                window = TryOpenFoodOrderWindow(cancellationToken);
            }

            if (window == IntPtr.Zero)
            {
                if (previousForeground != IntPtr.Zero)
                {
                    RestoreForeground(previousForeground);
                }

                return new GetoAutomationResult(false, "Geto 음식주문 창을 열지 못했습니다. Geto 메인 창(먹거리 주문 버튼)이 화면에 떠 있는지 확인해 주세요.");
            }

            if (!GetoDesktopAutomation.GetWindowRect(window, out var rect))
            {
                return new GetoAutomationResult(false, "WmClt 창 위치를 읽지 못했습니다.");
            }

            var bounds = rect.ToRectangle();
            if (bounds.Width < 1000 || bounds.Height < 600)
            {
                return new GetoAutomationResult(false, "현재 WmClt 창이 음식주문 화면이 아닙니다(창이 너무 작음).");
            }

            try
            {
                GetoDesktopAutomation.BringToFront(window, bounds);
                Thread.Sleep(250);

                foreach (var line in cart.Lines)
                {
                    for (var i = 0; i < line.Quantity; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var added = SearchAndAddItem(window, bounds, line.Item.Name, cancellationToken);
                        if (!added.Success)
                        {
                            return added;
                        }

                        Thread.Sleep(450);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                SelectPayment(window, bounds);
                Thread.Sleep(350);

                var summary = string.Join(", ", cart.Lines.Select(line => $"{line.Item.Name} x{line.Quantity}"));

                if (!AutoSubmit)
                {
                    return new GetoAutomationResult(
                        true,
                        $"'{summary}'를 장바구니에 담고 결제수단({PaymentLabel})을 선택했습니다. 자동 전송이 꺼져 있어 주문하기는 누르지 않았습니다.",
                        BuildDiagnostics(bounds, submitted: false));
                }

                cancellationToken.ThrowIfCancellationRequested();
                ClickSubmit(window, bounds);
                Thread.Sleep(1600); // wait for the "주문이 완료되었습니다" completion modal to render

                // Close the completion modal by clicking 상품구매 종료하기 (never the blue 추가구매하기).
                DismissCompletionModal(window, bounds);
                Thread.Sleep(400);

                return new GetoAutomationResult(
                    true,
                    $"'{summary}' 주문을 Geto에 전송하고 완료 처리했습니다(결제수단 {PaymentLabel}, 총 {cart.Total:N0}원).",
                    BuildDiagnostics(bounds, submitted: true));
            }
            finally
            {
                if (previousForeground != IntPtr.Zero && previousForeground != window)
                {
                    RestoreForeground(previousForeground);
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Opens the food-order screen (if needed) and types <paramref name="keyword"/> into the search box,
    /// then STOPS — no card is clicked and nothing is ordered. The window is left in front so the user
    /// can pick a product manually. Used for ambiguous category requests (라면, 커피, ...).
    /// </summary>
    public Task<GetoAutomationResult> OpenAndSearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            GetoDesktopAutomation.TrySetDpiAware();

            var window = FindWmCltOrderWindow();
            if (window == IntPtr.Zero)
            {
                window = TryOpenFoodOrderWindow(cancellationToken);
            }

            if (window == IntPtr.Zero)
            {
                return new GetoAutomationResult(false, "Geto 음식주문 창을 열지 못했습니다. Geto 메인 창(먹거리 주문 버튼)이 떠 있는지 확인해 주세요.");
            }

            if (!GetoDesktopAutomation.GetWindowRect(window, out var rect))
            {
                return new GetoAutomationResult(false, "WmClt 창 위치를 읽지 못했습니다.");
            }

            var bounds = rect.ToRectangle();
            if (bounds.Width < 1000 || bounds.Height < 600)
            {
                return new GetoAutomationResult(false, "현재 WmClt 창이 음식주문 화면이 아닙니다.");
            }

            var previousForeground = GetForegroundWindow();

            GetoDesktopAutomation.BringToFront(window, bounds);
            Thread.Sleep(200);

            cancellationToken.ThrowIfCancellationRequested();
            var searchPoint = LocateByName(window, "검색") ?? Ratio(bounds, SearchXRatio, SearchYRatio);
            GetoDesktopAutomation.Click(searchPoint);
            Thread.Sleep(150);
            GetoDesktopAutomation.SendCtrlA();
            Thread.Sleep(60);
            GetoDesktopAutomation.SendUnicodeText(keyword);
            Thread.Sleep(120);
            GetoDesktopAutomation.SendVirtualKey(GetoDesktopAutomation.VkReturn);
            Thread.Sleep(700);

            // If the search narrowed to exactly ONE product, order it directly (even if the phrase
            // wasn't an exact item name). If 0 or 2+, leave the screen up for manual selection.
            var count = CountGridProducts(bounds);
            if (count == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SelectProductByOcr(window, bounds, keyword, cancellationToken))
                {
                    GetoDesktopAutomation.Click(Ratio(bounds, FirstCardXRatio, FirstCardYRatio));
                    Thread.Sleep(350);
                    GetoDesktopAutomation.Click(Ratio(bounds, AddButtonXRatio, AddButtonYRatio));
                    Thread.Sleep(300);
                }

                if (!VerifyCartContains(window, bounds, keyword))
                {
                    return new GetoAutomationResult(false, $"'{keyword}' 이(가) 장바구니에 정확히 담겼는지 확인하지 못해 주문을 중단했습니다.");
                }

                SelectPayment(window, bounds);
                Thread.Sleep(300);

                if (AutoSubmit)
                {
                    ClickSubmit(window, bounds);
                    Thread.Sleep(1600);
                    DismissCompletionModal(window, bounds);
                    Thread.Sleep(300);
                    if (previousForeground != IntPtr.Zero && previousForeground != window)
                    {
                        RestoreForeground(previousForeground);
                    }

                    return new GetoAutomationResult(true, $"'{keyword}' 검색 결과가 하나뿐이라 바로 주문했습니다.");
                }

                return new GetoAutomationResult(true, $"'{keyword}' 검색 결과가 하나뿐이라 장바구니에 담았습니다. (자동 전송이 꺼져 있어 주문하기는 누르지 않았습니다.)");
            }

            // Leave the window in front so the user can choose.
            return new GetoAutomationResult(true, count <= 0
                ? $"'{keyword}' 검색 화면을 열었습니다. 원하는 메뉴를 골라 주세요."
                : $"'{keyword}' 검색 결과가 여러 개예요. 화면에서 원하는 메뉴를 눌러 주세요.");
        }, cancellationToken);
    }

    /// <summary>Counts product cards currently shown in the grid by OCR'ing price lines (e.g. "5,400원").</summary>
    private static int CountGridProducts(Rectangle bounds)
    {
        if (!GetoOcr.Available)
        {
            return -1; // unknown -> caller treats as "not single" and leaves the screen for manual pick
        }

        var gridMaxX = (int)(bounds.Width * 0.72);
        var minY = (int)(bounds.Height * 0.14); // below the banner/category bar
        using var capture = CaptureWindow(bounds);
        return GetoOcr.Read(capture)
            .Count(token => token.Bounds.Right < gridMaxX
                && token.Bounds.Top > minY
                && token.Text.Contains('원')
                && token.Text.Any(char.IsDigit));
    }

    private static GetoAutomationResult SearchAndAddItem(IntPtr window, Rectangle bounds, string itemName, CancellationToken cancellationToken)
    {
        GetoDesktopAutomation.BringToFront(window, bounds);

        // 1) Focus the native search box (UIA-located by name, ratio fallback) and type the item name.
        var searchPoint = LocateByName(window, "검색") ?? Ratio(bounds, SearchXRatio, SearchYRatio);
        GetoDesktopAutomation.Click(searchPoint);
        Thread.Sleep(150);
        GetoDesktopAutomation.SendCtrlA();
        Thread.Sleep(60);
        GetoDesktopAutomation.SendUnicodeText(itemName);
        Thread.Sleep(120);
        GetoDesktopAutomation.SendVirtualKey(GetoDesktopAutomation.VkReturn);
        Thread.Sleep(800); // let the CEF grid filter to the searched product

        cancellationToken.ThrowIfCancellationRequested();

        // 2)+3) Select the exact product by its OCR'd name (not a blind coordinate) and click 담기.
        //       Fall back to fixed first-slot ratios only when OCR is unavailable or finds nothing.
        if (!SelectProductByOcr(window, bounds, itemName, cancellationToken))
        {
            GetoDesktopAutomation.Click(Ratio(bounds, FirstCardXRatio, FirstCardYRatio));
            Thread.Sleep(350);
            GetoDesktopAutomation.Click(Ratio(bounds, AddButtonXRatio, AddButtonYRatio));
            Thread.Sleep(300);
        }

        // 4) Verify the item actually landed in the cart so we never submit the wrong food.
        if (!VerifyCartContains(window, bounds, itemName))
        {
            return new GetoAutomationResult(false,
                $"'{itemName}'이(가) 장바구니에 정확히 담겼는지 확인하지 못했습니다. 잘못된 주문을 막기 위해 중단했습니다.");
        }

        return new GetoAutomationResult(true, $"{itemName}을(를) 장바구니에 담았습니다.");
    }

    /// <summary>
    /// Finds the product card whose OCR'd name matches <paramref name="itemName"/>, clicks it, then
    /// clicks the "담기" button that appears. Returns false if OCR is unavailable or no card matches
    /// (caller then uses the coordinate fallback).
    /// </summary>
    private static bool SelectProductByOcr(IntPtr window, Rectangle bounds, string itemName, CancellationToken cancellationToken)
    {
        if (!GetoOcr.Available)
        {
            return false;
        }

        var gridMaxX = (int)(bounds.Width * 0.72); // product grid is the left ~72%; cart is on the right

        List<OcrToken> gridTokens;
        using (var grid = CaptureWindow(bounds))
        {
            gridTokens = GetoOcr.Read(grid).Where(token => token.Bounds.Right < gridMaxX).ToList();
        }

        var match = BestNameMatch(gridTokens, itemName);
        if (match is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        GetoDesktopAutomation.Click(new Point(
            bounds.Left + match.Bounds.Left + match.Bounds.Width / 2,
            bounds.Top + match.Bounds.Top + match.Bounds.Height / 2));
        Thread.Sleep(450);

        cancellationToken.ThrowIfCancellationRequested();
        OcrToken? add;
        using (var after = CaptureWindow(bounds))
        {
            add = GetoOcr.Read(after)
                .Where(token => token.Bounds.Right < gridMaxX && Normalize(token.Text).Contains("담기"))
                .OrderBy(token => token.Bounds.Top)
                .FirstOrDefault();
        }

        if (add is not null)
        {
            GetoDesktopAutomation.Click(new Point(
                bounds.Left + add.Bounds.Left + add.Bounds.Width / 2,
                bounds.Top + add.Bounds.Top + add.Bounds.Height / 2));
        }
        else
        {
            // 담기 button appears just below the matched card name.
            GetoDesktopAutomation.Click(new Point(
                bounds.Left + match.Bounds.Left + match.Bounds.Width / 2,
                bounds.Top + match.Bounds.Bottom + (int)(bounds.Height * 0.035)));
        }

        Thread.Sleep(300);
        return true;
    }

    private static bool VerifyCartContains(IntPtr window, Rectangle bounds, string itemName)
    {
        if (!GetoOcr.Available)
        {
            return true; // cannot verify -> do not block the flow
        }

        var cartMinX = (int)(bounds.Width * 0.72);
        List<OcrToken> cartTokens;
        using (var capture = CaptureWindow(bounds))
        {
            cartTokens = GetoOcr.Read(capture).Where(token => token.Bounds.Left > cartMinX).ToList();
        }

        var target = Normalize(itemName);
        return cartTokens.Any(token =>
        {
            var name = Normalize(token.Text);
            return name.Length >= 2 && (name.Contains(target) || target.Contains(name));
        });
    }

    /// <summary>Best product-name match for the item: exact first, then shortest (e.g. 콜라 -> 코카콜라, not 코카콜라zero).</summary>
    private static OcrToken? BestNameMatch(List<OcrToken> tokens, string itemName)
    {
        var target = Normalize(itemName);
        if (target.Length < 2)
        {
            return null;
        }

        var candidates = tokens.Where(token =>
        {
            var name = Normalize(token.Text);
            return name.Length >= 2 && (name.Contains(target) || target.Contains(name));
        }).ToList();

        return candidates
            .OrderBy(token => Normalize(token.Text) == target ? 0 : 1)
            .ThenBy(token => Normalize(token.Text).Length)
            .ThenBy(token => token.Bounds.Top)
            .ThenBy(token => token.Bounds.Left)
            .FirstOrDefault();
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant();
    }

    private static void SelectPayment(IntPtr window, Rectangle bounds)
    {
        GetoDesktopAutomation.BringToFront(window, bounds);
        var point = LocateByName(window, PaymentLabel) ?? Ratio(bounds, PaymentXRatio, PaymentYRatio);
        GetoDesktopAutomation.Click(point);
    }

    private static void ClickSubmit(IntPtr window, Rectangle bounds)
    {
        GetoDesktopAutomation.BringToFront(window, bounds);
        Thread.Sleep(150);
        // 주문하기 is inside the CEF panel (no UIA) -> detect the blue button in the bottom-right strip,
        // fall back to a measured ratio.
        var point = FindBlueButtonCenter(bounds, 0.62, 0.99, 0.88, 0.99)
            ?? Ratio(bounds, SubmitXRatio, SubmitYRatio);
        GetoDesktopAutomation.Click(point);
    }

    /// <summary>
    /// Closes the "주문이 완료되었습니다" modal by clicking 상품구매 종료하기. The modal shows a blue
    /// "추가구매하기" on the left and a gray "상품구매 종료하기" on the right; we detect the blue button
    /// and click just to its right so we never hit 추가구매하기 by accident.
    /// </summary>
    private static void DismissCompletionModal(IntPtr window, Rectangle bounds)
    {
        GetoDesktopAutomation.BringToFront(window, bounds);
        Thread.Sleep(150);

        var blue = FindBlueButtonRect(bounds, 0.28, 0.72, 0.58, 0.82);
        var point = blue is { } b
            ? new Point(bounds.Left + b.Right + (int)(b.Width * 0.6), bounds.Top + b.Top + b.Height / 2)
            : Ratio(bounds, CloseModalXRatio, CloseModalYRatio);
        GetoDesktopAutomation.Click(point);
    }

    private static Point? FindBlueButtonCenter(Rectangle bounds, double minXRatio, double maxXRatio, double minYRatio, double maxYRatio)
    {
        var rect = FindBlueButtonRect(bounds, minXRatio, maxXRatio, minYRatio, maxYRatio);
        return rect is { } b
            ? new Point(bounds.Left + b.Left + b.Width / 2, bounds.Top + b.Top + b.Height / 2)
            : null;
    }

    /// <summary>Bounding box (window-local) of the Geto blue action button within the given ratio region, or null.</summary>
    private static Rectangle? FindBlueButtonRect(Rectangle bounds, double minXRatio, double maxXRatio, double minYRatio, double maxYRatio)
    {
        try
        {
            using var bmp = CaptureWindow(bounds);
            var minX = (int)(bmp.Width * minXRatio);
            var maxX = Math.Min(bmp.Width, (int)(bmp.Width * maxXRatio));
            var minY = (int)(bmp.Height * minYRatio);
            var maxY = Math.Min(bmp.Height, (int)(bmp.Height * maxYRatio));

            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, count = 0;
            for (var y = minY; y < maxY; y += 2)
            {
                for (var x = minX; x < maxX; x += 2)
                {
                    var px = bmp.GetPixel(x, y);
                    if (px.B > 170 && px.R < 90 && px.G is >= 80 and <= 175)
                    {
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                        count++;
                    }
                }
            }

            if (count < 40 || right <= left || bottom <= top)
            {
                return null;
            }

            return Rectangle.FromLTRB(left, top, right, bottom);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap CaptureWindow(Rectangle bounds)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        return bmp;
    }

    /// <summary>Find a UIA element whose Name contains <paramref name="text"/> and return its screen-space center.</summary>
    private static Point? LocateByName(IntPtr window, string text)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            if (root is null)
            {
                return null;
            }

            var matches = root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>();
            var best = matches
                .Where(element => (element.Current.Name ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Current.BoundingRectangle)
                .Where(bound => bound is { Width: > 1, Height: > 1 } && !double.IsInfinity(bound.X))
                .OrderBy(bound => bound.Y)
                .FirstOrDefault();

            if (best == default || best.Width < 1)
            {
                return null;
            }

            return new Point((int)(best.X + best.Width / 2), (int)(best.Y + best.Height / 2));
        }
        catch
        {
            return null;
        }
    }

    private static Point Ratio(Rectangle bounds, double x, double y)
    {
        return new Point(bounds.Left + (int)(bounds.Width * x), bounds.Top + (int)(bounds.Height * y));
    }

    private static object BuildDiagnostics(Rectangle bounds, bool submitted)
    {
        return new
        {
            window = new { bounds.Left, bounds.Top, bounds.Width, bounds.Height },
            payment = PaymentLabel,
            autoSubmit = AutoSubmit,
            submitted
        };
    }

    private static IntPtr FindWmCltOrderWindow()
    {
        var wmcltPids = Process.GetProcessesByName("WmClt").Select(process => process.Id).ToHashSet();
        if (wmcltPids.Count == 0)
        {
            return IntPtr.Zero;
        }

        var best = IntPtr.Zero;
        var bestArea = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var pid);
            if (!wmcltPids.Contains((int)pid) || !IsWindowVisible(handle) || !GetoDesktopAutomation.GetWindowRect(handle, out var rect))
            {
                return true;
            }

            var bounds = rect.ToRectangle();
            var area = bounds.Width * bounds.Height;
            if (bounds.Width > 1000 && bounds.Height > 600 && area > bestArea)
            {
                bestArea = area;
                best = handle;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }

    /// <summary>
    /// Opens the food-order screen from the Geto main window when it isn't already open.
    /// The entry button label varies by Geto version ("먹거리 주문", "음식 주문", "음식주문", ...),
    /// so we match any element whose Name contains "주문" together with "먹거리" or "음식",
    /// excluding the submit button ("주문하기"), the history button ("주문내역"), and the
    /// "♥이달의 음식♥" category button.
    /// </summary>
    private static IntPtr TryOpenFoodOrderWindow(CancellationToken cancellationToken)
    {
        var located = LocateFoodOrderEntry();
        if (located is null)
        {
            return IntPtr.Zero;
        }

        var (owner, point) = located.Value;
        if (GetoDesktopAutomation.GetWindowRect(owner, out var rect))
        {
            GetoDesktopAutomation.BringToFront(owner, rect.ToRectangle());
            Thread.Sleep(200);
        }

        GetoDesktopAutomation.Click(point);

        // Wait for the large food-order window to render (up to ~6s).
        for (var i = 0; i < 24; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(250);
            var window = FindWmCltOrderWindow();
            if (window != IntPtr.Zero)
            {
                Thread.Sleep(500); // let the CEF grid finish loading
                return window;
            }
        }

        return IntPtr.Zero;
    }

    private static (IntPtr Owner, Point Point)? LocateFoodOrderEntry()
    {
        foreach (var top in EnumWmCltTopWindows())
        {
            AutomationElement? root;
            try
            {
                root = AutomationElement.FromHandle(top);
            }
            catch
            {
                continue;
            }

            if (root is null)
            {
                continue;
            }

            try
            {
                var match = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .Where(element => IsFoodOrderEntryName(element.Current.Name))
                    .Select(element => element.Current.BoundingRectangle)
                    .Where(bound => bound is { Width: > 1 and < 400, Height: > 1 and < 200 } && !double.IsInfinity(bound.X))
                    .OrderBy(bound => bound.Y)
                    .FirstOrDefault();

                if (match != default && match.Width >= 1)
                {
                    return (top, new Point((int)(match.X + match.Width / 2), (int)(match.Y + match.Height / 2)));
                }
            }
            catch
            {
                // Ignore a flaky UIA tree and try the next window.
            }
        }

        return null;
    }

    private static bool IsFoodOrderEntryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var hasOrder = name.Contains("주문", StringComparison.Ordinal);
        var hasFoodWord = name.Contains("먹거리", StringComparison.Ordinal) || name.Contains("음식", StringComparison.Ordinal);
        var isExcluded = name.Contains("주문하기", StringComparison.Ordinal)   // submit button
            || name.Contains("주문내역", StringComparison.Ordinal)              // order-history button
            || name.Contains("이달의", StringComparison.Ordinal);              // "♥이달의 음식♥" category

        return hasOrder && hasFoodWord && !isExcluded;
    }

    private static List<IntPtr> EnumWmCltTopWindows()
    {
        var pids = Process.GetProcessesByName("WmClt").Select(process => process.Id).ToHashSet();
        var handles = new List<IntPtr>();
        if (pids.Count == 0)
        {
            return handles;
        }

        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var pid);
            if (pids.Contains((int)pid) && IsWindowVisible(handle))
            {
                handles.Add(handle);
            }

            return true;
        }, IntPtr.Zero);

        return handles;
    }

    private static void RestoreForeground(IntPtr window)
    {
        try
        {
            ShowWindow(window, ShowRestore);
            SetForegroundWindow(window);
        }
        catch
        {
            // Best effort: never let focus-restore failure surface as an order failure.
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int ShowRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
