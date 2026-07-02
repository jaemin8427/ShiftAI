using System.Diagnostics;
using System.IO;
using ShiftAI.Core;

namespace ShiftAI.App;

public sealed class MockPcCafeAdapter : IPcCafeAdapter
{
    private readonly IGetoOrderAdapter[] _getoAdapters =
    [
        new GetoNativeOrderAdapter(),
        new GetoUiAutomationOrderAdapter(),
        new GetoVisionMouseFallbackOrderAdapter()
    ];

    private readonly GetoMockBackgroundClient _backgroundClient = new();
    private readonly GetoMockAutomation _getoMockAutomation = new();

    public async Task<ToolResult> OrderFoodAsync(int seatNumber, CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        var attempts = new List<object>();
        GetoAutomationResult? getoResult = null;
        var mode = "none";

        foreach (var adapter in _getoAdapters)
        {
            getoResult = await adapter.OrderAsync(cart, cancellationToken);
            attempts.Add(new
            {
                adapter = adapter.Name,
                getoResult.Success,
                getoResult.Message
            });

            mode = adapter.Name;
            if (getoResult.Success)
            {
                break;
            }
        }

        if (getoResult is null || !getoResult.Success)
        {
            getoResult = await _backgroundClient.AddCartItemsAsync(cart, cancellationToken);
            mode = "mock-background";
            attempts.Add(new
            {
                adapter = mode,
                getoResult.Success,
                getoResult.Message
            });
        }

        if (!getoResult.Success)
        {
            getoResult = await _getoMockAutomation.AddCartItemsAsync(cart, cancellationToken);
            mode = "mock-vision-fallback";
            attempts.Add(new
            {
                adapter = mode,
                getoResult.Success,
                getoResult.Message
            });
        }

        var payload = new
        {
            seatNumber,
            items = cart.Lines.Select(line => new
            {
                name = line.Item.Name,
                quantity = line.Quantity,
                price = line.Item.Price,
                total = line.Total
            }),
            totalAmount = cart.Total,
            simulated = mode.StartsWith("mock-", StringComparison.OrdinalIgnoreCase),
            mode,
            paymentMethod = "pay-at-seat-card",
            attempts,
            getoMock = getoResult
        };

        var message = getoResult.Success
            ? mode == "geto-native-adapter"
                ? $"\uC88C\uC11D {seatNumber}\uBC88 Geto Native Adapter\uC73C\uB85C \uD604\uC7A5 \uCE74\uB4DC \uACB0\uC81C \uC8FC\uBB38\uC744 \uC804\uC1A1\uD588\uC2B5\uB2C8\uB2E4."
                : mode == "windows-ui-automation"
                ? $"\uC88C\uC11D {seatNumber}\uBC88 Geto UI Automation\uC73C\uB85C \uC8FC\uBB38\uC744 \uC804\uC1A1\uD588\uC2B5\uB2C8\uB2E4."
                : mode == "vision-mouse-fallback"
                ? $"\uC88C\uC11D {seatNumber}\uBC88 Geto Vision fallback\uC774 \uC8FC\uBB38 \uD654\uBA74\uC744 \uC900\uBE44\uD588\uC2B5\uB2C8\uB2E4."
                : mode == "mock-background"
                ? $"\uC88C\uC11D {seatNumber}\uBC88 Geto Mock\uC5D0 \uBC31\uADF8\uB77C\uC6B4\uB4DC\uB85C \uB2F4\uC558\uC2B5\uB2C8\uB2E4. \uCD1D\uC561 {cart.Total:N0}\uC6D0\uC785\uB2C8\uB2E4."
                : $"\uC88C\uC11D {seatNumber}\uBC88 Geto Mock \uC7A5\uBC14\uAD6C\uB2C8\uC5D0 \uC190\uC73C\uB85C \uB2F4\uC558\uC2B5\uB2C8\uB2E4. \uCD1D\uC561 {cart.Total:N0}\uC6D0\uC785\uB2C8\uB2E4."
            : $"\uC8FC\uBB38\uC740 \uD655\uC778\uD588\uC9C0\uB9CC Geto \uD654\uBA74 \uC81C\uC5B4\uB294 \uC2E4\uD328\uD588\uC2B5\uB2C8\uB2E4. {getoResult.Message}";

        return new ToolResult(
            "orderFood",
            getoResult.Success ? AgentStatus.Completed : AgentStatus.NeedsClarification,
            message,
            payload);
    }

    public Task<ToolResult> CallStaffAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult(
            "callStaff",
            AgentStatus.Completed,
            $"\uC88C\uC11D {seatNumber}\uBC88\uC73C\uB85C \uC9C1\uC6D0\uC744 \uD638\uCD9C\uD55C \uAC83\uC73C\uB85C \uC2DC\uBBAC\uB808\uC774\uC158\uD588\uC2B5\uB2C8\uB2E4.",
            new { seatNumber, simulated = true }));
    }

    public Task<ToolResult> TroubleshootAudioAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult(
            "troubleshootAudio",
            AgentStatus.Completed,
            "Windows \uAE30\uBCF8 \uCD9C\uB825 \uC7A5\uCE58, \uBCFC\uB968, \uAC8C\uC784 \uC0AC\uC6B4\uB4DC \uC124\uC815\uC744 \uC810\uAC80\uD55C \uAC83\uC73C\uB85C \uC2DC\uBBAC\uB808\uC774\uC158\uD588\uC2B5\uB2C8\uB2E4.",
            new { seatNumber, checks = new[] { "default_output_device", "master_volume", "game_audio" }, simulated = true }));
    }

    public Task<ToolResult> LaunchGameAsync(int seatNumber, string gameName, CancellationToken cancellationToken = default)
    {
        var launcher = FindLeagueLauncher();
        if (launcher is null)
        {
            return Task.FromResult(new ToolResult(
                "launchGame",
                AgentStatus.NeedsClarification,
                "League of Legends 실행 파일을 찾지 못했습니다. SHIFT_AI_LOL_PATH 환경변수에 LeagueClient.exe 또는 RiotClientServices.exe 경로를 지정해 주세요.",
                new { seatNumber, gameName, simulated = false, found = false }));
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = launcher.Path,
                Arguments = launcher.Arguments,
                WorkingDirectory = Path.GetDirectoryName(launcher.Path) ?? Environment.CurrentDirectory,
                UseShellExecute = true
            });

            return Task.FromResult(new ToolResult(
                "launchGame",
                AgentStatus.Completed,
                $"{gameName} 실행을 시작했습니다.",
                new { seatNumber, gameName, simulated = false, launcherPath = launcher.Path, launcher.Arguments }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult(
                "launchGame",
                AgentStatus.NeedsClarification,
                $"{gameName} 실행을 시도했지만 실패했습니다. {ex.Message}",
                new { seatNumber, gameName, simulated = false, launcherPath = launcher.Path, launcher.Arguments, error = ex.Message }));
        }
    }

    private static GameLauncher? FindLeagueLauncher()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SHIFT_AI_LOL_PATH");
        if (IsExecutableFile(configuredPath))
        {
            return ToLauncher(configuredPath!);
        }

        var leagueCandidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Riot Games",
                "League of Legends",
                "LeagueClient.exe"),
            @"C:\Riot Games\League of Legends\LeagueClient.exe",
            @"C:\Program Files\Riot Games\League of Legends\LeagueClient.exe",
            @"C:\Program Files (x86)\Riot Games\League of Legends\LeagueClient.exe"
        };

        foreach (var candidate in leagueCandidates)
        {
            if (IsExecutableFile(candidate))
            {
                return new GameLauncher(candidate, "");
            }
        }

        var riotClientCandidates = new[]
        {
            @"C:\Riot Games\Riot Client\RiotClientServices.exe",
            @"C:\Program Files\Riot Games\Riot Client\RiotClientServices.exe",
            @"C:\Program Files (x86)\Riot Games\Riot Client\RiotClientServices.exe"
        };

        foreach (var candidate in riotClientCandidates)
        {
            if (IsExecutableFile(candidate))
            {
                return new GameLauncher(candidate, "--launch-product=league_of_legends --launch-patchline=live");
            }
        }

        return null;
    }

    private static GameLauncher ToLauncher(string path)
    {
        return Path.GetFileName(path).Equals("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase)
            ? new GameLauncher(path, "--launch-product=league_of_legends --launch-patchline=live")
            : new GameLauncher(path, "");
    }

    private static bool IsExecutableFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && File.Exists(path)
            && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record GameLauncher(string Path, string Arguments);

    public Task<ToolResult> GetRemainingTimeAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult(
            "getRemainingTime",
            AgentStatus.Completed,
            $"\uC88C\uC11D {seatNumber}\uBC88\uC758 \uB0A8\uC740 \uC2DC\uAC04\uC740 1\uC2DC\uAC04 42\uBD84\uC73C\uB85C \uC2DC\uBBAC\uB808\uC774\uC158\uD588\uC2B5\uB2C8\uB2E4.",
            new { seatNumber, remainingMinutes = 102, simulated = true }));
    }

    public Task<ToolResult> CancelCurrentActionAsync(int seatNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ToolResult(
            "cancelCurrentAction",
            AgentStatus.Cancelled,
            "\uC9C4\uD589 \uC911\uC778 \uC791\uC5C5\uACFC \uC7A5\uBC14\uAD6C\uB2C8\uB97C \uCDE8\uC18C\uD588\uC2B5\uB2C8\uB2E4.",
            new { seatNumber, simulated = true }));
    }
}
