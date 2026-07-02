using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var outputRoot = args.FirstOrDefault(arg => arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase)) is { } outArg
    ? outArg["--out=".Length..]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ShiftAI-GetoProbe");

Directory.CreateDirectory(outputRoot);

var probe = new GetoProbe();
var report = probe.Run();

var reportPath = Path.Combine(outputRoot, "geto-probe-report.json");
File.WriteAllText(
    reportPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
    Encoding.UTF8);

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"Shift AI GetoProbe complete: {reportPath}");
Console.WriteLine($"GameHub processes: {report.Processes.Count(p => p.Name.Equals("GameHub.exe", StringComparison.OrdinalIgnoreCase))}");
Console.WriteLine($"GameHub WebView roots: {report.WebViewRoots.Count(root => root.Owner.Equals("GameHub.exe", StringComparison.OrdinalIgnoreCase))}");
Console.WriteLine($"Native surfaces: {report.NativeSurfaces.Count}");
Console.WriteLine($"Candidate URLs: {report.CandidateUrls.Count}");
Console.WriteLine($"Candidate commands: {report.BinaryStrings.Count}");

public sealed class GetoProbe
{
    private static readonly Regex UrlRegex = new(@"https?://[^\s""'<>\\\u0000]+", RegexOptions.Compiled);
    private static readonly Regex AsciiStringRegex = new(@"[\x20-\x7E]{5,}", RegexOptions.Compiled);
    private static readonly string[] InterestingTerms =
    [
        "playgeto", "gamehub", "authapi", "eventapi", "api", "order", "cart", "food", "menu", "pay", "payment",
        "shop", "payload", "cmd", "WebMessage", "chrome.webview", "postMessage", "fetch", "axios", "XMLHttpRequest",
        "\uC8FC\uBB38", "\uACB0\uC81C", "\uC7A5\uBC14\uAD6C\uB2C8", "\uC74C\uC2DD", "\uBA54\uB274", "\uC0C1\uD488"
    ];

    public GetoProbeReport Run()
    {
        var processes = ReadProcesses();
        var webViewRoots = FindWebViewRoots(processes);
        var candidateUrls = new List<CandidateUrl>();
        var cacheHits = new List<FileHit>();

        foreach (var root in webViewRoots)
        {
            candidateUrls.AddRange(ScanUrls(root));
            cacheHits.AddRange(ScanTextHits(root));
        }

        var binaryStrings = ScanBinaries(processes);
        var tcp = ReadTcp(processes);
        var windows = ReadTopLevelWindows(processes);
        var nativeSurfaces = BuildNativeSurfaces(processes, windows, webViewRoots);

        return new GetoProbeReport(
            DateTimeOffset.Now,
            Environment.MachineName,
            Environment.UserName,
            processes,
            windows,
            tcp,
            nativeSurfaces,
            webViewRoots,
            candidateUrls
                .DistinctBy(url => (url.SourceFile, url.Url))
                .OrderBy(url => url.Owner)
                .ThenBy(url => url.SourceFile)
                .ThenBy(url => url.Url)
                .ToList(),
            cacheHits
                .DistinctBy(hit => (hit.Owner, hit.Path, hit.Term))
                .OrderBy(hit => hit.Owner)
                .ThenBy(hit => hit.Path)
                .ThenBy(hit => hit.Term)
                .ToList(),
            binaryStrings
                .DistinctBy(hit => (hit.File, hit.Value))
                .OrderBy(hit => hit.File)
                .ThenBy(hit => hit.Value)
                .ToList());
    }

    private static List<ProcessInfo> ReadProcesses()
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");

        return searcher.Get()
            .Cast<System.Management.ManagementObject>()
            .Select(obj => new ProcessInfo(
                ToInt(obj["ProcessId"]),
                ToInt(obj["ParentProcessId"]),
                Convert.ToString(obj["Name"]) ?? "",
                Convert.ToString(obj["ExecutablePath"]) ?? "",
                Convert.ToString(obj["CommandLine"]) ?? ""))
            .Where(IsGetoProcess)
            .OrderBy(process => process.ParentProcessId)
            .ThenBy(process => process.ProcessId)
            .ToList();
    }

    private static bool IsGetoProcess(ProcessInfo process)
    {
        if (ContainsAny(process.Name, "GameHub", "WmClt", "UserInfo", "PromotionHub"))
        {
            return true;
        }

        return process.Name.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)
            && ContainsAny(process.CommandLine, "Geto_", "Mobilnet", "GameHub", "WmClt", "UserInfo", "PromotionHub");
    }

    private static List<WebViewRoot> FindWebViewRoots(List<ProcessInfo> processes)
    {
        var roots = new List<WebViewRoot>();
        foreach (var process in processes)
        {
            var root = ExtractUserDataDir(process.CommandLine);
            if (root is null)
            {
                continue;
            }

            roots.Add(new WebViewRoot(
                GuessWebViewOwner(process.CommandLine),
                process.ProcessId,
                root,
                Directory.Exists(root),
                Directory.Exists(Path.Combine(root, "Default")),
                Directory.Exists(Path.Combine(root, "Guest Profile"))));
        }

        return roots
            .GroupBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(root => root.Owner.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                .ThenBy(root => root.ProcessId)
                .First())
            .OrderBy(root => root.Owner)
            .ThenBy(root => root.Path)
            .ToList();
    }

    private static List<NativeSurfaceInfo> BuildNativeSurfaces(
        List<ProcessInfo> processes,
        List<WindowInfo> windows,
        List<WebViewRoot> webViewRoots)
    {
        return processes
            .Where(process => ContainsAny(process.Name, "WmClt", "GameHub", "UserInfo", "PromotionHub"))
            .Select(process =>
            {
                var childProcesses = processes
                    .Where(candidate => candidate.ParentProcessId == process.ProcessId)
                    .OrderBy(candidate => candidate.Name)
                    .ThenBy(candidate => candidate.ProcessId)
                    .ToList();
                var processWindows = windows
                    .Where(window => window.ProcessId == process.ProcessId)
                    .OrderByDescending(window => window.Width * window.Height)
                    .ToList();
                var ownedWebViews = webViewRoots
                    .Where(root => root.Owner.Equals(process.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(root => root.Path)
                    .ToList();

                return new NativeSurfaceInfo(
                    process.ProcessId,
                    process.Name,
                    process.Path,
                    processWindows,
                    childProcesses,
                    ownedWebViews,
                    ClassifyNativeSurface(process, processWindows, ownedWebViews));
            })
            .OrderBy(surface => surface.ProcessName)
            .ThenBy(surface => surface.ProcessId)
            .ToList();
    }

    private static string ClassifyNativeSurface(
        ProcessInfo process,
        List<WindowInfo> windows,
        List<WebViewRoot> webViewRoots)
    {
        var largestWindow = windows.OrderByDescending(window => window.Width * window.Height).FirstOrDefault();
        var urls = string.Join(" ", webViewRoots.Select(root => TryReadLatestUrl(root.Path)));

        if (process.Name.Equals("WmClt.exe", StringComparison.OrdinalIgnoreCase)
            && largestWindow is not null
            && largestWindow.Width >= 1000
            && largestWindow.Height >= 600)
        {
            return urls.Contains("/ad/menu/belt", StringComparison.OrdinalIgnoreCase)
                ? "food-order-native-host-with-menu-banner-webview"
                : "large-wmclt-native-window";
        }

        if (process.Name.Equals("GameHub.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "gamehub-webview-host";
        }

        if (process.Name.Equals("UserInfo.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "seat-info-widget";
        }

        if (process.Name.Equals("PromotionHub.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "promotion-webview-host";
        }

        return "geto-native-process";
    }

    private static string TryReadLatestUrl(string webViewRoot)
    {
        var history = Path.Combine(webViewRoot, "Default", "History");
        var text = TryReadText(history);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var matches = UrlRegex.Matches(text);
        return matches.Count == 0 ? "" : RedactLongPayload(TrimUrl(matches[^1].Value));
    }

    private static string GuessWebViewOwner(string commandLine)
    {
        var match = Regex.Match(commandLine, @"--webview-exe-name=([^ ]+)");
        if (match.Success)
        {
            return match.Groups[1].Value.Trim('"');
        }

        return "unknown";
    }

    private static string? ExtractUserDataDir(string commandLine)
    {
        var quoted = Regex.Match(commandLine, @"--user-data-dir=""([^""]+)""");
        if (quoted.Success)
        {
            return quoted.Groups[1].Value;
        }

        var unquoted = Regex.Match(commandLine, @"--user-data-dir=([^ ]+)");
        return unquoted.Success ? unquoted.Groups[1].Value : null;
    }

    private List<CandidateUrl> ScanUrls(WebViewRoot root)
    {
        var files = EnumerateSmallFiles(root.Path, 20 * 1024 * 1024);
        var urls = new List<CandidateUrl>();

        foreach (var file in files)
        {
            var text = TryReadText(file);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            foreach (Match match in UrlRegex.Matches(text))
            {
                var url = TrimUrl(match.Value);
                if (IsInteresting(url))
                {
                    urls.Add(new CandidateUrl(root.Owner, Shorten(root.Path, file), RedactLongPayload(url)));
                }
            }
        }

        return urls;
    }

    private List<FileHit> ScanTextHits(WebViewRoot root)
    {
        var hits = new List<FileHit>();
        foreach (var file in EnumerateSmallFiles(root.Path, 8 * 1024 * 1024))
        {
            var relative = Shorten(root.Path, file);
            if (relative.Contains("Subresource Filter", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("Trust Protection Lists", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("ZxcvbnData", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = TryReadText(file);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            foreach (var term in InterestingTerms)
            {
                if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new FileHit(root.Owner, relative, new FileInfo(file).Length, term, ExtractSnippet(text, term)));
                }
            }
        }

        return hits;
    }

    private static List<BinaryStringHit> ScanBinaries(List<ProcessInfo> processes)
    {
        var candidates = processes
            .Where(process => File.Exists(process.Path))
            .Where(process => ContainsAny(process.Name, "GameHub", "WmClt", "UserInfo", "PromotionHub"))
            .Select(process => process.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hits = new List<BinaryStringHit>();
        foreach (var file in candidates)
        {
            var bytes = TryReadBytes(file);
            if (bytes is null)
            {
                continue;
            }

            var ascii = Encoding.ASCII.GetString(bytes);
            var unicode = Encoding.Unicode.GetString(bytes);
            foreach (var value in ExtractInterestingStrings(ascii).Concat(ExtractInterestingStrings(unicode)))
            {
                hits.Add(new BinaryStringHit(file, value));
            }
        }

        return hits;
    }

    private static IEnumerable<string> ExtractInterestingStrings(string text)
    {
        foreach (Match match in AsciiStringRegex.Matches(text))
        {
            var value = match.Value.Trim();
            if (value.Length > 260)
            {
                value = value[..260];
            }

            if (IsInteresting(value) ||
                ContainsAny(value, "SharedMem", "WebMsg", "SendJson", "SendMessage", "PostMessage", "MapViewOfFile", "OpenFileMapping"))
            {
                yield return value;
            }
        }
    }

    private static List<TcpConnectionInfo> ReadTcp(List<ProcessInfo> processes)
    {
        var pids = processes.Select(process => process.ProcessId).ToHashSet();
        return IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpConnections()
            .Where(connection => pids.Contains(GetTcpOwningProcessBestEffort(connection)))
            .Select(connection => new TcpConnectionInfo(
                connection.State.ToString(),
                connection.LocalEndPoint.ToString(),
                connection.RemoteEndPoint.ToString(),
                GetTcpOwningProcessBestEffort(connection)))
            .ToList();
    }

    private static int GetTcpOwningProcessBestEffort(TcpConnectionInformation connection)
    {
        // System.Net does not expose owning PID. Netstat-level PID capture is handled by PowerShell if needed.
        // Keep this field for future ETW/netstat integration.
        _ = connection;
        return 0;
    }

    private static List<WindowInfo> ReadTopLevelWindows(List<ProcessInfo> processes)
    {
        var pids = processes.Select(process => process.ProcessId).ToHashSet();
        var windows = new List<WindowInfo>();

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (!pids.Contains((int)pid) || !IsWindowVisible(window) || !GetWindowRect(window, out var rect))
            {
                return true;
            }

            var title = GetWindowTitle(window);
            var bounds = rect.ToRectangle();
            if (bounds.Width > 80 && bounds.Height > 80)
            {
                windows.Add(new WindowInfo((int)pid, title, bounds.Left, bounds.Top, bounds.Width, bounds.Height));
            }

            return true;
        }, IntPtr.Zero);

        return windows.OrderBy(window => window.ProcessId).ThenBy(window => window.Title).ToList();
    }

    private static IEnumerable<string> EnumerateSmallFiles(string root, long maxBytes)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch
            {
                continue;
            }

            if (info.Exists && info.Length <= maxBytes)
            {
                yield return file;
            }
        }
    }

    private static string? TryReadText(string file)
    {
        var bytes = TryReadBytes(file);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static byte[]? TryReadBytes(string file)
    {
        try
        {
            return File.ReadAllBytes(file);
        }
        catch
        {
            try
            {
                var copy = Path.Combine(Path.GetTempPath(), $"shiftai-probe-{Guid.NewGuid():N}");
                File.Copy(file, copy, overwrite: true);
                var bytes = File.ReadAllBytes(copy);
                File.Delete(copy);
                return bytes;
            }
            catch
            {
                return null;
            }
        }
    }

    private static string Shorten(string root, string file)
    {
        return file.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? file[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : file;
    }

    private static string TrimUrl(string url)
    {
        return url.TrimEnd('.', ',', ';', ')', ']', '}', '\u0002', '\uFFFD');
    }

    private static string RedactLongPayload(string url)
    {
        const string marker = "payload=";
        var index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return url.Length > 500 ? $"{url[..500]}..." : url;
        }

        var start = index + marker.Length;
        var keep = Math.Min(64, url.Length - start);
        return $"{url[..start]}{url.Substring(start, keep)}...[payload-truncated]";
    }

    private static bool IsInteresting(string text)
    {
        return InterestingTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractSnippet(string text, string term)
    {
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "";
        }

        var start = Math.Max(0, index - 80);
        var length = Math.Min(220, text.Length - start);
        return text.Substring(start, length).Replace('\0', ' ').ReplaceLineEndings(" ");
    }

    private static int ToInt(object? value)
    {
        return value is null ? 0 : Convert.ToInt32(value);
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var builder = new StringBuilder(512);
        GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
}

public sealed record GetoProbeReport(
    DateTimeOffset Timestamp,
    string MachineName,
    string UserName,
    List<ProcessInfo> Processes,
    List<WindowInfo> Windows,
    List<TcpConnectionInfo> TcpConnections,
    List<NativeSurfaceInfo> NativeSurfaces,
    List<WebViewRoot> WebViewRoots,
    List<CandidateUrl> CandidateUrls,
    List<FileHit> FileHits,
    List<BinaryStringHit> BinaryStrings);

public sealed record ProcessInfo(int ProcessId, int ParentProcessId, string Name, string Path, string CommandLine);
public sealed record WindowInfo(int ProcessId, string Title, int X, int Y, int Width, int Height);
public sealed record TcpConnectionInfo(string State, string Local, string Remote, int OwningProcess);
public sealed record NativeSurfaceInfo(
    int ProcessId,
    string ProcessName,
    string Path,
    List<WindowInfo> Windows,
    List<ProcessInfo> ChildProcesses,
    List<WebViewRoot> WebViewRoots,
    string Classification);
public sealed record WebViewRoot(string Owner, int ProcessId, string Path, bool Exists, bool HasDefaultProfile, bool HasGuestProfile);
public sealed record CandidateUrl(string Owner, string SourceFile, string Url);
public sealed record FileHit(string Owner, string Path, long Length, string Term, string Snippet);
public sealed record BinaryStringHit(string File, string Value);

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

public readonly record struct Rectangle(int Left, int Top, int Width, int Height)
{
    public static Rectangle FromLTRB(int left, int top, int right, int bottom)
    {
        return new Rectangle(left, top, right - left, bottom - top);
    }
}
