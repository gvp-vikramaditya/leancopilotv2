using System.Text.Json;

static class PromptMode
{
    const string Help = """
Usage: lean --prompt "Request" --yes [options]
       lean --prompt - --yes [options] < prompt.txt
       lean                         Interactive mode

  -p, --prompt TEXT    Submit one request; '-' reads redirected stdin through EOF
  --yes               Approve remote context handoff and file edits for this request
  --model NAME        Remote Copilot model (default: saved /model selection)
  --local-model NAME  Installed Ollama model (default: first available)
  --sensitivity NAME  Public, Internal (default), Confidential, or Restricted
  --execution         Also permit remote shells and their raw output
  --json              Emit one JSON object: status, output, exitCode
  -h, --help          Show this help without starting models

Run from the repository directory to research. Ollama must already be running,
with a model installed, and Copilot must be authenticated. No settings are saved.
Only the final result goes to stdout; progress and diagnostics go to stderr.
Exit codes: 0 accepted completion, 1 blocked/runtime failure, 2 invalid arguments.
The same completion checks, two-worker limit and 15-minute remote deadline apply.
""";

    public sealed record Options(string Prompt, string? Model, string? LocalModel,
        string Sensitivity, bool Execution, bool Json);

    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i] == "-p" ? "--prompt" : args[i];
            var value = "";
            switch (key)
            {
                case "--prompt": case "--model": case "--local-model": case "--sensitivity":
                    if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--"))
                        throw new ArgumentException($"{key} requires a nonempty value.");
                    value = args[i];
                    break;
                case "--yes": case "--execution": case "--json":
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{key}'. Use --help.");
            }
            if (!values.TryAdd(key, value))
                throw new ArgumentException($"Duplicate argument '{key}'.");
        }
        if (!values.TryGetValue("--prompt", out var prompt))
            throw new ArgumentException("--prompt is required. Use --help.");
        if (!values.ContainsKey("--yes"))
            throw new ArgumentException("--yes is required to approve the remote handoff and file edits without a confirmation.");
        var sensitivity = new[] { "Public", "Internal", "Confidential", "Restricted" }
            .FirstOrDefault(s => s.Equals(values.GetValueOrDefault("--sensitivity", "Internal"), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Invalid --sensitivity. Use Public, Internal, Confidential, or Restricted.");
        return new(prompt, values.GetValueOrDefault("--model"), values.GetValueOrDefault("--local-model"),
            sensitivity, values.ContainsKey("--execution"), values.ContainsKey("--json"));
    }

    public static int Run(string[] args)
    {
        Terminal.BatchMode = true;
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine(Help);
            return 0;
        }
        try
        {
            var options = Parse(args);
            var prompt = options.Prompt;
            if (prompt == "-")
            {
                if (!Console.IsInputRedirected)
                    throw new ArgumentException("--prompt - requires redirected stdin.");
                prompt = Console.In.ReadToEnd();
                if (string.IsNullOrWhiteSpace(prompt))
                    throw new ArgumentException("The prompt from stdin is empty.");
            }
            var model = options.Model ?? Settings.LoadRemoteModel();
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("Specify --model or save a /model selection in interactive mode.");
            Logger.Info($"Lean {McpServer.BuildId} prompt mode in {Environment.CurrentDirectory}");
            Terminal.WriteLine($"Logs: {Logger.LogPath}");
            var models = Ollama.GetModels();
            if (models.Length == 0)
                throw new InvalidOperationException($"No local models available. Start Ollama and install a model; see {Logger.LogPath}.");
            var localModel = options.LocalModel ?? models[0];
            if (!models.Contains(localModel, StringComparer.Ordinal))
                throw new ArgumentException($"Local model '{localModel}' is not installed. Available: {string.Join(", ", models)}");
            if (!Ollama.Warm(localModel, ensureReady: false))
                throw new InvalidOperationException($"Could not warm local model '{localModel}'; see {Logger.LogPath}.");
            var result = Remote.Run(prompt, model, localModel, options.Sensitivity, options.Execution, _ => { });
            return WriteResult(result.Output, result.ExitCode == 0 ? 0 : 1, options.Json, Console.Out);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
        {
            Logger.Error($"Prompt mode failed: {ex.Message}");
            Console.Error.WriteLine(ex.Message);
            return WriteResult($"status: error\nerrors: {ex.Message}", ex is ArgumentException ? 2 : 1,
                args.Contains("--json"), Console.Out);
        }
    }

    public static int WriteResult(string result, int exitCode, bool json, TextWriter output)
    {
        var status = result.ReplaceLineEndings("\n").Split('\n')
            .FirstOrDefault(line => line.StartsWith("status:", StringComparison.OrdinalIgnoreCase))?["status:".Length..].Trim()
            ?? "error";
        output.WriteLine(json ? JsonSerializer.Serialize(new { status, output = result, exitCode }) : result.TrimEnd());
        return exitCode;
    }
}
