using System.Text.RegularExpressions;

if (args.FirstOrDefault() == "--mcp-server")
{
    Environment.ExitCode = McpServer.Run(args);
    return;
}
if (args.Contains("--self-test"))
{
    SelfTests.Run();
    if (args.Contains("--overview-integration"))
        SelfTests.OverviewIntegration();
    if (args.Contains("--retry-integration"))
        SelfTests.RetryIntegration();
    if (args.Contains("--file-integration"))
        SelfTests.FileIntegration();
    if (args.Contains("--scope-integration"))
        SelfTests.ScopeIntegration();
    if (args.Contains("--local-integration"))
        SelfTests.LocalIntegration();
    return;
}

string? remoteModel = Settings.LoadRemoteModel();
string? localModel = null;
var sensitivity = "Internal";
var debug = false;
var execution = false;
var runs = new List<CopilotRun>();

Terminal.Initialize();
try
{
    Logger.Info($"Lean {McpServer.BuildId} starting in {Environment.CurrentDirectory}; executable={Environment.ProcessPath}");
    Terminal.WriteLine($"Lean CLI (build {McpServer.BuildId})\nLocal research, remote decisions: {Environment.CurrentDirectory}");
    Terminal.WriteLine($"Logs: {Logger.LogPath}\nType /help for commands.");
    if (Ollama.EnsureReady())
    {
        localModel = Ollama.GetModels().FirstOrDefault();
        if (localModel is not null)
            Ollama.Warm(localModel);
    }
    else
        Terminal.WriteLine("\nOllama is unavailable. See the log for startup details.");

    while (true)
    {
        Terminal.SetStatus(Status(localModel, remoteModel, runs));
        var input = Terminal.ReadInput("> ")?.Trim();
        if (input is null || input.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
            break;
        if (input.Length == 0)
            continue;
        switch (input.ToLowerInvariant())
        {
            case "/help":
            case "?":
                Terminal.WriteLine($"""

Commands
  /local-model   Select local Ollama researcher
  /model         Select authoritative OpenAI Copilot model
  /sensitivity   Label all remote context (current: {sensitivity})
  /execution     Toggle remote shell permission (current: {(execution ? "on" : "off")})
  /debug         Toggle run contexts, operations, timing and usage
  /logs          Show local audit log location
  /exit, /quit   Exit Lean CLI

One remote conversation delegates file objectives; local workers collect source and return file/method briefs.
Live read/analysis status is shown during inference. The head consumes briefs, then requests focused dives.
Whole-project review retains all eligible files across bounded batches; only assessed current scope is done.
Advanced criterion-reviewed plans remain optional. Local done is not proof that the whole task is complete.
Default handoff: compact findings, references and gaps; raw evidence stays in local request audits.
File editing remains available. Shells are off by default; /execution permits implementation/validation
but can also bypass local-only research and return raw command output. This is not a sandbox.
Remote access requires approval for every request. Snapshot caches live for that request; refresh after edits
invalidates existing plan evidence. Partial work is retained; retries and total work are bounded.
Diagnostics: --self-test; add --file-integration for focused localhost file/method checks,
--scope-integration for whole-scope batch/refresh fixture checks,
or --local-integration for legacy plan and sample checks.
""");
                continue;
            case "/model":
                remoteModel = Select("OpenAI Copilot models", Regex.Matches(Remote.Command("copilot", "help", "config"),
                    "\"(gpt-[^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().ToArray(), remoteModel);
                Settings.SaveRemoteModel(remoteModel);
                continue;
            case "/local-model":
                localModel = Select("Local Ollama models", Ollama.GetModels(), localModel);
                if (localModel is not null)
                    Ollama.Warm(localModel);
                continue;
            case "/sensitivity":
                sensitivity = Select("Sensitivity", ["Public", "Internal", "Confidential", "Restricted"], sensitivity) ?? sensitivity;
                continue;
            case "/execution":
                execution = !execution;
                Terminal.WriteLine($"\nRemote shell permission: {(execution ? "on; can bypass research restrictions and disclose raw command output" : "off")}");
                continue;
            case "/debug":
                debug = !debug;
                Terminal.WriteLine($"\nDebug mode: {(debug ? "on" : "off")}");
                if (debug)
                    foreach (var run in runs)
                        ShowRun(run);
                continue;
            case "/logs":
                Terminal.WriteLine($"\nLogs and request audits: {Logger.LogPath}");
                continue;
        }
        if (remoteModel is null || localModel is null)
        {
            Terminal.WriteLine("\nSelect both /model and /local-model before starting a request.");
            continue;
        }
        Terminal.WriteLine($"""

Remote access [{sensitivity}]
  Request, local findings, cited paths, coverage and gaps
  Explicit bounded source excerpts only when requested by the remote model
  File edits permitted; remote shells {(execution ? "enabled (raw output may leave this machine)" : "disabled")}
  Remote drives completion with up to 12 autonomous continuations within 15 minutes
""");
        if (!string.Equals(Terminal.ReadInput($"Send to '{remoteModel}' and permit these actions? [y/N] ")?.Trim(),
            "y", StringComparison.OrdinalIgnoreCase))
        {
            Terminal.WriteLine("\nRemote handoff cancelled.");
            continue;
        }
        void Record(CopilotRun run)
        {
            runs.Add(run);
            if (debug)
                ShowRun(run);
            Terminal.SetStatus(Status(localModel, remoteModel, runs));
        }
        var result = Remote.Run(input, remoteModel, localModel, sensitivity, execution, Record);
        Record(result);
        Terminal.WriteLine($"\n{result.Output.Trim()}");
    }
}
finally
{
    Logger.Info("Lean exiting");
    Terminal.Restore();
}

static string Status(string? local, string? remote, List<CopilotRun> runs)
{
    string Counts(string location) =>
        $"in={runs.Where(r => r.Location == location).Sum(r => r.Usage.Input):N0} " +
        $"out={runs.Where(r => r.Location == location).Sum(r => r.Usage.Output):N0}" +
        (runs.Any(r => r.Location == location && !r.Usage.Measured) ? " (incomplete)" : "");
    return $"Token totals | Local: {local ?? "none"} {Counts("local")} | Remote: {remote ?? "none"} {Counts("remote")}";
}

static void ShowRun(CopilotRun run) => Terminal.WriteLine($"""

location: {run.Location}; model: {run.Model}; exit: {run.ExitCode}
duration: {run.Duration.TotalSeconds:F2}s
usage: input={run.Usage.Input}; output={run.Usage.Output}; cache-read={run.Usage.CacheRead}; cache-write={run.Usage.CacheWrite}; measured={run.Usage.Measured}
operations: {string.Join("; ", run.Commands)}
context: {run.Context}
""");

static string? Select(string heading, string[] choices, string? current)
{
    if (choices.Length == 0)
    {
        Terminal.WriteLine($"\nNo choices found for {heading}.");
        return current;
    }
    Terminal.WriteLine($"\n{heading}");
    for (var i = 0; i < choices.Length; i++)
        Terminal.WriteLine($"  {i + 1}. {choices[i]}{(choices[i] == current ? " (current)" : "")}");
    return int.TryParse(Terminal.ReadInput("Select number: "), out var choice) &&
        choice > 0 && choice <= choices.Length ? choices[choice - 1] : current;
}
