using ShiftAI.App;
using ShiftAI.Core;

var root = FindWorkspaceRoot();
var menu = MenuLoader.Load(Path.Combine(root, "data", "menu.sample.json"));
var matcher = new MenuMatcher(menu);
var router = new IntentRouter(matcher);
var log = new JsonlActionLog(Path.Combine(root, "logs", "smoke-actions.jsonl"));
var executor = new ActionExecutor(38, new Cart(), router, new MockPcCafeAdapter(), log);

var response = await executor.ExecuteAsync("\uCF5C\uB77C \uD558\uB098 \uCD94\uAC00\uD574");

Console.WriteLine($"Status={response.Status}");
Console.WriteLine(response.AssistantText);
Console.WriteLine(response.ToolResult?.Message);

if (response.Status != AgentStatus.Completed || response.AssistantText != "\uC54C\uACA0\uC5B4, \uC2DC\uD0AC\uAC8C!!")
{
    Environment.ExitCode = 1;
}

static string FindWorkspaceRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "data", "menu.sample.json")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}
