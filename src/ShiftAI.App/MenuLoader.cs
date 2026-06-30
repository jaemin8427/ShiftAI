using System.IO;
using System.Text.Json;
using ShiftAI.Core;

namespace ShiftAI.App;

public static class MenuLoader
{
    public static IReadOnlyList<MenuItem> Load(string path)
    {
        using var stream = File.OpenRead(path);
        var rows = JsonSerializer.Deserialize<List<MenuRow>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];

        return rows.Select(row => new MenuItem(row.Id, row.Name, row.Price)).ToList();
    }

    private sealed record MenuRow(string Id, string Name, int Price);
}
