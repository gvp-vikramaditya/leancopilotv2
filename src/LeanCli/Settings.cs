using System.Text.Json;

static class Settings
{
    static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LeanCli",
        "settings.json");

    public static string? LoadRemoteModel()
    {
        try
        {
            if (!File.Exists(PathName))
                return null;

            using var json = JsonDocument.Parse(File.ReadAllText(PathName));
            return json.RootElement.TryGetProperty("remoteModel", out var model)
                ? model.GetString()
                : null;
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not load settings: {ex.Message}");
            return null;
        }
    }

    public static void SaveRemoteModel(string? model)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(new { remoteModel = model }));
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not save settings: {ex.Message}");
        }
    }
}
