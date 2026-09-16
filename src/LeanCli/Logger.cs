using System.Text.Json;

static class Logger
{
    static readonly object Gate = new();
    static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LeanCli",
        "logs");

    public static string LogPath { get; } = Path.Combine(DirectoryPath, "lean.log");

    static Logger()
    {
        Directory.CreateDirectory(DirectoryPath);
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message) => Write("ERROR", message);

    public static string CreateRequestDirectory()
    {
        var path = Path.Combine(DirectoryPath, $"request-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string SaveEvents(string location, string content)
    {
        var path = Path.Combine(
            DirectoryPath,
            $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{location}-events.jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    public static void LogEventErrors(string jsonLines)
    {
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("type", out var type) &&
                    type.GetString()?.Contains("error", StringComparison.OrdinalIgnoreCase) == true)
                    Error($"Copilot event: {line}");
            }
            catch (JsonException)
            {
            }
        }
    }

    static void Write(string level, string message)
    {
        lock (Gate)
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
    }
}
