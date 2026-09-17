using System.Diagnostics;
using System.Text.Json;

static class SelfTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-test failed: {name}");
        Console.WriteLine($"pass: {name}");
    }

    public static void Run()
    {
        TestPromptMode();
        var root = Path.Combine(AppContext.BaseDirectory, $"self-test-{Guid.NewGuid():N}");
        var audit = Path.Combine(root, "audit");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllLines(Path.Combine(root, "Program.cs"), Enumerable.Range(1, 300)
                .Select(i => $"// private-source-marker-{i:D3} long raw source must not be in the default handoff"));
            File.WriteAllText(Path.Combine(root, "Project.csproj"), "<Project><TargetFramework>net10.0</TargetFramework></Project>");
            for (var i = 0; i < 6; i++)
                File.WriteAllLines(Path.Combine(root, $"Component{i}.cs"),
                    Enumerable.Range(1, 60).Select(line => $"// component {i} line {line}"));
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", "Ignored.cs"), "not inspected");
            File.WriteAllText(Path.Combine(root, ".env"), "not inspected");

            var repository = new LocalRepository(root);
            var evidence = repository.Gather("overview", "map project", null);
            Check(evidence.Ranges.Length == 8 && evidence.Ranges.Sum(r => r.Lines.Length) <= 240 &&
                evidence.Ranges.Any(r => r.Path == "Project.csproj") && evidence.Ranges.Any(r => r.Path == "Program.cs") &&
                evidence.Ranges.Count(r => r.Path.StartsWith("Component")) == 6 &&
                evidence.Ranges.All(r => r.Lines.Length <= 30),
                "overview distributes budget across manifest, entry and six components");
            Check(repository.Discover().Length == 8, "hidden and generated files excluded");
            var reads = repository.DiskReads;
            repository.Range("Program.cs", 1, 5);
            Check(repository.DiskReads == reads && repository.CacheHits > 0, "snapshot evidence reused");
            var gaps = new List<string>();
            var valid = Research.ValidateBrief("answer: C# entry point [Program.cs:1-2]", evidence, gaps);
            Check(valid.Contains("answer:") && gaps.Count == 0, "captured-range citation accepted");
            var invalid = Research.ValidateBrief("answer: invented [missing.py:1-2]\nflow: wrong [Program.cs:29-40]\nanswer: uncited",
                evidence, gaps);
            Check(invalid == "" && gaps.Count == 3, "missing, out-of-range and absent citations rejected");
            Check(Research.ValidateBrief("answer: // private-source-marker-001 long raw source must not be in the default handoff [Program.cs:1-1]",
                evidence, []).Length == 0, "verbatim source rejected from findings");
            Check(!Ollama.IsBrief("""{"name":"view","arguments":{"path":"guessed.py"}}""") &&
                !Ollama.IsBrief("```json\n{}\n```"), "textual pseudo-tools rejected");
            foreach (var tuple in new[] { ("Program.cs", 0, 2), ("Program.cs", 1, 81), ("Program.cs", 1, 10001),
                ("Program.cs", 400, 401), ("..\\outside.cs", 1, 2), (".env", 1, 1), ("bin\\Ignored.cs", 1, 1),
                ("missing.py", 1, 1) })
            {
                var rejected = false;
                try { repository.Range(tuple.Item1, tuple.Item2, tuple.Item3); }
                catch (Exception ex) when (ex is ArgumentException or IOException) { rejected = true; }
                Check(rejected, $"excerpt rejects {tuple}");
            }
            var exact = repository.Range("Program.cs", 150, 152);
            Check(exact.Lines.Length == 3 && exact.Text.Contains("L150:") && !exact.Text.Contains("L149:"),
                "explicit excerpts return exactly requested bounds");
            var calls = 0;
            var research = new Research(root, "stub", "Confidential", audit, (_, _) =>
            {
                calls++;
                return ("answer: C# project targeting net10.0 [Project.csproj:1-1]", new TokenUsage(100, 20, 0, 0));
            });
            var summary = research.Call("overview");
            Check(summary.StartsWith("status: partial") && summary.Contains("sensitivity: Confidential") &&
                summary.Contains("coverage:") && summary.Contains("gaps:") &&
                summary.Contains("not proof of semantic truth") && !summary.Contains("private-source-marker") &&
                !summary.Contains("<TargetFramework>") && !summary.Contains("L1:"),
                "default handoff contains compact findings and provenance, not raw source");
            research.Call("overview");
            Check(calls == 1, "identical research reuses report without inference");
            var raw = research.Call("excerpt", path: "Program.cs", start: 1, end: 2);
            Check(raw.Contains("private-source-marker-001") && raw.Contains("source: requested raw excerpt"),
                "only explicit excerpt exposes raw evidence");
            Check(Directory.EnumerateFiles(audit, "research-*.json")
                .Any(p => File.ReadAllText(p).Contains("private-source-marker")),
                "raw evidence persists in local audit");
            File.WriteAllText(Path.Combine(root, "Project.csproj"), "<Project>changed</Project>");
            Check(research.Call("excerpt", path: "Project.csproj", start: 1, end: 1).Contains("net10.0") &&
                research.Call("excerpt", path: "Project.csproj", start: 1, end: 1, refresh: true).Contains("changed"),
                "snapshot stable until explicit refresh");
            var ast = research.Call("deep-dive", "Return parser-generated AST", "Program.cs");
            Check(ast.Contains("AST unsupported") && calls == 1, "AST explicitly unsupported; no fabricated parsing");
            var deep = repository.Gather("deep-dive", "private-source-marker-250", "Program.cs");
            Check(deep.Ranges.Any(r => r.Path == "Program.cs" && r.Start <= 250 && r.End >= 250) &&
                deep.Searched.Contains("Program.cs"), "deep dive finds literal evidence beyond initial sample");
            File.WriteAllText(Path.Combine(root, "Large.txt"), new string('x', 9000));
            var chars = new Research(root, "stub", "Internal", audit).Call("excerpt", path: "Large.txt", start: 1, end: 1);
            Check(chars.StartsWith("status: error") && chars.Contains("8000"), "excerpt character limit enforced");

            var link = Path.Combine(root, "linked");
            try { Directory.CreateSymbolicLink(link, Path.Combine(root, "bin")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"skip: symlink creation unavailable: {ex.Message}");
            }
            if (Directory.Exists(link))
            {
                var rejected = false;
                try { repository.Range("linked\\Ignored.cs", 1, 1); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "linked paths rejected");
                Directory.Delete(link);
            }
            Check(Ollama.LocalUri("http://127.0.0.1:11434/v1").AbsoluteUri == "http://127.0.0.1:11434/",
                "loopback URL normalized");
            foreach (var url in new[] { "https://example.com", "file:///tmp", "http://localhost/?redirect=remote" })
            {
                var rejected = false;
                try { Ollama.LocalUri(url); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, $"nonlocal/invalid endpoint rejected: {url}");
            }
            TestLaunch(root, audit);
            TestActivity();
            TestPlan(root, audit);
            using (var native = new NativeClient(root, "unused", audit))
            {
                native.Initialize();
                var result = native.Call("excerpt", "", "Program.cs", 150, 152);
                Check(result.Contains("L150:") && !result.Contains("L149:") && result.Contains("citation: Program.cs:150-152"),
                    "native MCP initialize/list/call roundtrip produces real repository evidence");
                Check(native.Call("excerpt", "", "..\\outside.cs", 1, 2).StartsWith("status: error"),
                    "native MCP confinement error");
                var job = native.Files("Review excluded file", [".env"]);
                Check(job.StartsWith("status: error") && !job.Contains("jobId:") && job.Contains("Hidden/generated/private"),
                    "native MCP rejects excluded paths before creating a failed job or running inference");
            }
            var requests = """
{"jsonrpc":"2.0","id":1,"method":"tools/list"}
{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":3,"method":"unsupported"}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"local_research","arguments":{"intent":"excerpt","start":"bad"}}}
not json
""";
            using var writer = new StringWriter();
            McpServer.Serve(new StringReader(requests), writer, research);
            var codes = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            Check(codes.Length == 5 && codes[0].RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32002 &&
                codes[2].RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32601 &&
                codes[3].RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32602 &&
                codes[4].RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32700,
                "MCP lifecycle, notifications and JSON-RPC errors");
            foreach (var doc in codes) doc.Dispose();
            var parsed = Remote.ParseEvents("""
{"type":"tool.execution_start","data":{"toolName":"lean-local-local_research","arguments":{"intent":"overview"}}}
{"type":"assistant.message","data":{"content":"status: ok"}}
""");
            Check(parsed.Response == "status: ok" && parsed.Commands.Single().Contains("overview"),
                "remote native tool events parsed");
            TestCompletionOutput();
            var usageFile = Path.Combine(root, "usage.json");
            File.WriteAllText(usageFile, """{"modelMetrics":{"remote":{"usage":{"inputTokens":200,"outputTokens":30,"cacheReadTokens":100,"cacheWriteTokens":0}}}}""");
            Check(Remote.ReadUsage(usageFile) == new TokenUsage(200, 30, 100, 0) &&
                !Remote.ReadUsage(Path.Combine(root, "missing-usage.json")).Measured, "remote usage counts and explicit unknown");
            for (var i = 0; i < 10; i++)
                File.WriteAllText(Path.Combine(root, $"Manifest{i}.csproj"), "<Project />");
            var manyManifests = new LocalRepository(root).Gather("overview", "map project", null);
            Check(manyManifests.Ranges.Any(r => r.Path == "Program.cs") &&
                manyManifests.Ranges.Any(r => r.Path.StartsWith("Component")) &&
                manyManifests.Ranges.Count(r => r.Path.EndsWith(".csproj")) == 2,
                "many manifests cannot crowd out entry point and components");
            TestFiles(root, audit);
            Console.WriteLine("self-test: passed; no remote model invoked");
        }
        finally
        {
            // Only remove this test's exact, newly-created fixture files/directories.
            if (Directory.Exists(audit))
            {
                foreach (var file in Directory.EnumerateFiles(audit, "*", SearchOption.TopDirectoryOnly))
                    File.Delete(file);
                Directory.Delete(audit);
            }
            File.Delete(Path.Combine(root, "bin", "Ignored.cs"));
            Directory.Delete(Path.Combine(root, "bin"));
            foreach (var file in Directory.EnumerateFiles(root))
                File.Delete(file);
            Directory.Delete(root);
        }
    }

    static void TestLaunch(string root, string audit)
    {
        using var config = JsonDocument.Parse(Remote.McpConfig(root, "local", "Internal", audit));
        var server = config.RootElement.GetProperty("mcpServers").GetProperty(McpServer.Name);
        Check(server.GetProperty("type").GetString() == "local" &&
            server.GetProperty("tools")[0].GetString() == McpServer.Tool &&
            server.GetProperty("args").EnumerateArray().Any(a => a.GetString() == "--mcp-server"),
            "native MCP config uses same executable and real tool schema");
        var start = Remote.StartInfo("remote", "config.json", "usage.json", false);
        Check(start.ArgumentList.Contains("--additional-mcp-config") &&
            start.ArgumentList.Contains(McpServer.QualifiedTool) && start.ArgumentList.Contains("write") &&
            start.ArgumentList.Contains("--deny-tool") && !start.ArgumentList.Contains("--allow-all") &&
            !start.ArgumentList.Contains("--allow-all-tools") && !start.ArgumentList.Contains("--allow-all-paths") &&
            !start.ArgumentList.Contains("powershell") && !start.ArgumentList.Contains("view") &&
            !start.ArgumentList.Contains("rg") && !start.ArgumentList.Contains("--resume"),
            "one conversation launch: allowlisted research and edits, no blanket privileges or direct reads");
        Check(start.ArgumentList.Contains("--autopilot") && start.ArgumentList.Contains("--max-autopilot-continues") &&
            start.ArgumentList.Contains("12"), "remote completion loop uses bounded native autopilot in the same conversation");
        Check(start.ArgumentList.Count(a => a == "task_complete") == 2 &&
            start.ArgumentList.SkipWhile(a => a != "--available-tools").Contains("task_complete") &&
            start.ArgumentList.Contains("--plugin-dir"),
            "autopilot completion tool is both available and explicitly permitted");
        Check(Remote.StartInfo("remote", "config.json", "usage.json", true).ArgumentList.Contains("powershell"),
            "explicit execution mode preserves shell implementation capability");
        Check(Ollama.FileConcurrency(null, 1) == 1 && Ollama.FileConcurrency(null, 2) == 2 &&
            Ollama.FileConcurrency(null, 3) == 2 && Ollama.FileConcurrency(null, 64) == 2 &&
            Ollama.FileConcurrency("1", 64) == 1 && Ollama.FileConcurrency("99", 2) == 2 &&
            Ollama.FileConcurrency("0", 64) == 1 && Ollama.FileConcurrency("-2", 64) == 1 &&
            Ollama.FileConcurrency(null, 0) == 1,
            "worker default is processor count capped at two; overrides cannot exceed two");
        var serverStart = Ollama.ServerStartInfo(name => name switch
        {
            "LEAN_LOCAL_FILE_CONCURRENCY" => "99",
            "OLLAMA_NUM_PARALLEL" => "2",
            "OLLAMA_CONTEXT_LENGTH" => "32768",
            "OLLAMA_IGPU_ENABLE" => "1",
            _ => null
        });
        Check(serverStart.Environment["OLLAMA_NUM_PARALLEL"] == Math.Clamp(Environment.ProcessorCount, 1, 2).ToString() &&
            serverStart.Environment["OLLAMA_CONTEXT_LENGTH"] == "32768" &&
            serverStart.Environment["OLLAMA_IGPU_ENABLE"] == "1",
            "new Ollama launch aligns bounded worker capacity despite stale server settings, without starting a server");
        Check(Ollama.ServerStartInfo(_ => null).Environment["OLLAMA_NUM_PARALLEL"] ==
                Math.Clamp(Environment.ProcessorCount, 1, 2).ToString() &&
            Ollama.ServerStartInfo(name => name == "LEAN_LOCAL_FILE_CONCURRENCY" ? "1" : null)
                .Environment["OLLAMA_NUM_PARALLEL"] == "1",
            "auto-start server matches core-based default and explicit serial override");
        var invalidCapacity = false;
        try { Ollama.FileConcurrency("invalid", 4); }
        catch (ArgumentException) { invalidCapacity = true; }
        Check(invalidCapacity, "nonnumeric capacity rejected explicitly");
        Check(!Remote.Prompt("test", "Restricted", false).Contains("LEAN_RESULT: LOCAL_PROBE") &&
            Remote.Prompt("test", "Restricted", false).Contains("Sensitivity: Restricted"),
            "remote prompt uses native delegation, sensitivity and no text-protocol loops");
        Check(Remote.Prompt("test", "Internal", false).Contains($"Research root: {Environment.CurrentDirectory}") &&
            !Remote.Prompt("test", "Internal", false).Contains("src\\\\LeanCli"),
            "remote receives actual root, not a hardcoded project-relative example");
        foreach (var execution in new[] { false, true })
        {
            var prompt = Remote.Prompt("Explain or update the project", "Internal", execution);
            Check(prompt.Contains("ALWAYS PLAN BEFORE DELEGATING") &&
                prompt.Contains("owner (local/remote), dependencies and completion checks") &&
                prompt.Contains("ONE concrete question") && prompt.Contains("assess local feasibility") &&
                prompt.Contains("Pass the slice objective and acceptance check in query") &&
                prompt.Contains("REMOTE FIT: cross-file synthesis") &&
                prompt.Contains("replan with a narrower question") &&
                prompt.Contains("do not repeat an unchanged failed objective") &&
                prompt.Contains("one files job applies the SAME small purpose-summary slice to every file"),
                $"remote planning covers feasibility, discrete slices, replanning and worker-owned batching (execution={execution})");
            Check(prompt.Contains("TWO DISTINCT completion steps") &&
                prompt.Contains("ONE line, 1..600") && prompt.Contains("correct the named argument and retry the SAME job") &&
                prompt.Contains("It does NOT approve MCP jobs"),
                "remote distinguishes MCP sign-off from autopilot completion and repairs the same job");
            Check(prompt.Contains("summary MUST contain the actual final answer") &&
                prompt.Contains("progress messages are not the answer"),
                "remote completion instructions require the explanation itself, not an activity recap");
            Check(prompt.Contains("BE SKEPTICAL") && prompt.Contains("Ask for proof") &&
                prompt.Contains("not independent proof") && prompt.Contains("actual permitted execution results") &&
                prompt.Contains("Preserve counterexamples") && prompt.Contains("proportional to the user's goal") &&
                prompt.Contains("do not upload the repository or raw local audit files"),
                "remote asks for bounded proof, preserves uncertainty and does not equate summaries with verification");
        }
        const string slice = "Identify the timeout value; acceptance check: cite the configured duration.";
        Check(Research.Prompt("deep-dive", slice).Contains(slice) &&
            Research.OverviewFilePrompt(slice).Contains(slice) && Research.FilePrompt(slice, true).Contains(slice),
            "head slice objective and acceptance check reach all local summary prompts");
    }

    static string Resume(string report) => report.Split('\n').First(l => l.StartsWith("resume: "))["resume: ".Length..];

    static void TestCompletionOutput()
    {
        const string start = """
{"type":"assistant.message","data":{"content":"Finalizing: report the synthesized review and mark the autopilot task complete."}}
{"type":"tool.execution_start","data":{"toolName":"task_complete","toolCallId":"finish-1","arguments":{"summary":"status: done\nanswer: Lean combines remote decisions with local repository research."}}}
""";
        const string success = """
{"type":"tool.execution_complete","data":{"toolCallId":"finish-1","success":true,"result":{"content":"Task completed."}}}
{"type":"result","exitCode":0}
""";
        var completed = Remote.ParseEvents(start + "\n" + success);
        Check(completed.Response == "status: done\nanswer: Lean combines remote decisions with local repository research." &&
            completed.Commands.Single().StartsWith("task_complete:"),
            "successful completion displays its final answer instead of the last progress message");
        Check(Remote.ParseEvents(start).Response.StartsWith("status: error") &&
            Remote.ParseEvents(start + "\n" + success.Replace("\"success\":true", "\"success\":false"))
                .Response.StartsWith("status: error") &&
            Remote.ParseEvents(start + "\n" + success.Replace("finish-1", "unrelated"))
                .Response.StartsWith("status: error"),
            "pending, failed and unrelated tool results cannot present an unconfirmed completion summary");
        const string noSummary = """
{"type":"tool.execution_start","data":{"toolName":"task_complete","toolCallId":"finish-1","arguments":{}}}
""";
        Check(Remote.ParseEvents(noSummary + "\n" + success).Response == "Task completed." &&
            Remote.ParseEvents(noSummary + "\n" + success.Replace("\"content\":\"Task completed.\"", "\"content\":\"\""))
                .Response.StartsWith("status: error"),
            "completion can use returned text but explicitly reports a missing final response");
        Check(Remote.ParseEvents(start + "\n" + success + "\n" + start.Replace("finish-1", "finish-2"))
            .Response.StartsWith("status: error"), "a later unconfirmed completion cannot reuse an earlier success");
    }

    static void TestActivity()
    {
        var activity = new Remote.ActivityState("remote-model", "local-model");
        Check(activity.Labels().Local is null && activity.Labels().Coordinator!.Contains("provider state unknown"),
            "coordinator activity never implies provider inference merely because the task is running");
        activity.Observe(new ResearchProgress(DateTimeOffset.Now, "old unstructured progress"));
        Check(activity.Labels().Local is null, "unstructured progress is not guessed to mean local inference");
        activity.Observe(new ResearchProgress(DateTimeOffset.Now, "reading", "local-model", 0, 4, true));
        Check(activity.Labels().Local!.Contains("0/4") && activity.Labels().Coordinator!.Contains("waiting for local"),
            "local source collection is distinguished from active model requests");
        activity.Observe(new ResearchProgress(DateTimeOffset.Now, "request start", "local-model", 2, 4, true));
        Check(activity.Labels().Local == "local-model 2/4 workers" &&
            activity.Labels().Coordinator == "remote-model waiting for local",
            "two actual local requests display the correct model/count and coordinator dependency");
        activity.Observe(new ResearchProgress(DateTimeOffset.Now, "request finish", "local-model", 1, 4, true));
        Check(activity.Labels().Local!.Contains("1/4"), "out-of-order worker completion updates active count");
        activity.Observe(new ResearchProgress(DateTimeOffset.Now, "batch finished", "local-model", 0, 4, false));
        Check(activity.Labels().Local is null && activity.Labels().Coordinator!.Contains("coordinator active"),
            "finished local work returns activity to coordination, not claimed remote inference");
        activity.Finish();
        activity.Observe(new ResearchProgress(DateTimeOffset.Now, "late event", "local-model", 3, 4, true));
        Check(activity.Labels() == (null, null), "task completion/error clears both activities and ignores late worker events");
    }
    static string Field(string report, string name) => report.Split('\n')
        .First(l => l.StartsWith(name + ": ", StringComparison.Ordinal))[(name.Length + 2)..];

    static string FileJson(string name = "Load", int start = 1, int end = 4, string unknown = "") =>
        JsonSerializer.Serialize(new
        {
            purpose = new { text = "Persists the selected setting.", start, end },
            methods = new[] { new { name, text = "Returns the configured setting.", start, end, coverage = "complete" } },
            unknown
        });

    static string FinishFiles(Research research, string started)
    {
        var id = Field(started, "jobId");
        var cursor = Resume(started);
        var reports = new List<string>();
        for (var i = 0; i < 128; i++)
        {
            var result = research.FileStatus(id, cursor, 1);
            reports.Add(result);
            cursor = Resume(result);
            if (result.StartsWith("status: review"))
            {
                var citation = reports.SelectMany(r => r.Split('\n')).FirstOrDefault(l => l.StartsWith("reference: "))?["reference: ".Length..];
                var done = research.FileComplete(id, cursor, $"Reviewed bounded source and semantic gaps: {citation ?? "empty scope"}");
                reports.Add(done);
                return string.Join("\n", reports);
            }
            if (!result.StartsWith("status: running") && !result.StartsWith("status: pending"))
                return string.Join("\n", reports);
        }
        throw new InvalidOperationException("Offline file worker did not finish.");
    }

    static void TestFiles(string root, string audit)
    {
        File.WriteAllLines(Path.Combine(root, "Methods.cs"),
            ["static class Methods", "{", "    static string Load() => \"private-fixture-setting\";", "}"]);
        File.WriteAllLines(Path.Combine(root, "Other.cs"),
            ["static class Other", "{", "    static string Save() => \"private-fixture-value\";", "}"]);
        var repository = new LocalRepository(root);
        var range = repository.FileEvidence("Methods.cs").Ranges.Single();
        var gaps = new List<string>();
        var validated = Research.ValidateFileBrief(FileJson(), range, gaps);
        Check(validated.Contains("purpose:") && validated.Contains("method: Load (complete):") &&
            validated.Contains("[Methods.cs:1-4]") && gaps.Count == 0 && !validated.Contains("private-fixture"),
            "file/method schema yields concise named findings with actual validated references, not source");
        gaps.Clear();
        Check(!Research.ValidateFileBrief(FileJson("Imagined"), range, gaps).Contains("method:") && gaps.Count == 1,
            "method names absent from cited captured source are discarded");
        gaps.Clear();
        Check(Research.ValidateFileBrief(FileJson(start: 1, end: 5), range, gaps).Length == 0 && gaps.Count == 2,
            "file and method out-of-range citations rejected");
        File.WriteAllLines(Path.Combine(root, "LongLines.cs"), Enumerable.Repeat("// bounded line", 10020));
        Check(new LocalRepository(root).Range("LongLines.cs", 10010, 10012).Text.Contains("L10010:"),
            "explicit bounded verification excerpts can reach evidence beyond the old 10000-line absolute cap");
        var copies = FileJson().Replace("Returns the configured setting.",
            "static string Load() => \\\"private-fixture-setting\\\";");
        Check(!Research.ValidateFileBrief(copies, range, []).Contains("method:"), "file method source-copy rejected");
        File.WriteAllLines(Path.Combine(root, "Chunked.cs"), Enumerable.Range(0, 800)
            .Select(i => $"// bounded source {i:D4} " + new string('x', 100)));
        var chunked = new LocalRepository(root).FileEvidence("Chunked.cs");
        Check(chunked.Ranges.Length > 4 && chunked.Ranges.All(r => r.Lines.Sum(l => l.Length + 16) <= 4000) &&
            chunked.Ranges.Sum(r => r.Lines.Length) == 800 && chunked.Gaps.Any(g => g.Contains("not parsed")),
            "worker captures every bounded chunk, not a four-chunk sample, without invented method boundaries");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var researcher = new Research(root, "stub", "Restricted", audit, (prompt, source) =>
        {
            Interlocked.Increment(ref calls);
            Check(prompt.Contains("complete bounded file") && source.Contains("L4: }"),
                "local model receives full bounded file rather than first 30-line samples");
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Fixture release timeout");
            return (FileJson(), new TokenUsage(100, 40, 0, 0));
        });
        var watch = Stopwatch.StartNew();
        var started = researcher.Files("Summarize file and methods", ["Methods.cs"]);
        Check(watch.Elapsed < TimeSpan.FromSeconds(2) && started.StartsWith("status: running"),
            "delegated objective returns bounded job handle without waiting for inference");
        var id = Field(started, "jobId");
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "file worker independently begins local inference");
            var progress = new List<string>();
            var activity = new Remote.ActivityState("remote-model", "stub");
            Remote.ReadProgress(audit, [], progress.Add, activity.Observe);
            Check(progress.Any(p => p.Contains("analyzing Methods.cs:1-4")) &&
                progress.All(p => !p.Contains("private-fixture")) &&
                researcher.FileStatus(id, Resume(started), 0).StartsWith("status: running"),
                "actual file progress and bounded status delivered before long inference finishes");
            Check(activity.Labels().Local == $"stub 1/{Ollama.ConfiguredFileConcurrency()} workers" &&
                activity.Labels().Coordinator!.Contains("waiting for local"),
                "real published worker-start event changes activity before inference completes");
            Check(researcher.Files("Summarize file and methods", ["Methods.cs"]).Contains("existing worklist") && calls == 1,
                "repeated objective while active does not queue duplicate inference");
            Check(researcher.Call("excerpt", path: "Methods.cs", start: 1, end: 1, refresh: true).StartsWith("status: error"),
                "active worker prevents concurrent snapshot mutation");
        }
        finally { release.Set(); }
        var completed = FinishFiles(researcher, started);
        Check(completed.Contains("status: done") && completed.Contains("coverage: 4/4 lines captured") &&
            completed.Contains("method: Load") && !completed.Contains("private-fixture"),
            "completed file brief reports complete captured input but not an exhaustive method inventory");
        var replay = researcher.FileStatus(id, Resume(started), 0);
        Check(replay.Contains("delivery replay") && calls == 1 && researcher.DiskReads == 1,
            "file cursor replay and cache avoid duplicate reads/inference");
        File.AppendAllText(Path.Combine(root, "Methods.cs"), "\n");
        var refreshed = researcher.Files("Summarize file and methods", ["Methods.cs"], refresh: true);
        FinishFiles(researcher, refreshed);
        Check(calls == 2 && researcher.FileStatus(id, null, 0).Contains("snapshot changed"),
            "refresh creates new source snapshot and rejects stale file deliveries");

        if (Environment.ProcessorCount >= 2)
        {
            using var firstEntered = new ManualResetEventSlim();
            using var secondFinished = new ManualResetEventSlim();
            using var unblock = new ManualResetEventSlim();
            var parallel = new Research(root, "stub", "Internal", audit, (_, source) =>
            {
                if (source.StartsWith("file: Methods.cs"))
                {
                    firstEntered.Set();
                    if (!unblock.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Parallel release timeout");
                    return (FileJson(), new TokenUsage(10, 5, 0, 0));
                }
                secondFinished.Set();
                return (FileJson("Save"), new TokenUsage(10, 5, 0, 0));
            }, fileConcurrency: 2);
            var parallelStart = parallel.Files("Review both files", ["Methods.cs", "Other.cs"]);
            try
            {
                Check(firstEntered.Wait(TimeSpan.FromSeconds(5)) && secondFinished.Wait(TimeSpan.FromSeconds(5)),
                    "explicit capacity two allows independent inference while first file is blocked");
                Check(!parallel.FileStatus(Field(parallelStart, "jobId"), Resume(parallelStart), 0).Contains("\nfile:"),
                    "out-of-order completion does not reorder delivered file briefs");
            }
            finally { unblock.Set(); }
            var parallelResult = FinishFiles(parallel, parallelStart);
            Check(parallelResult.IndexOf("\nfile: Methods.cs", StringComparison.Ordinal) <
                parallelResult.IndexOf("\nfile: Other.cs", StringComparison.Ordinal),
                "parallel completion commits file briefs deterministically in requested order");
        }
        TestFileCapacity(root, audit);
        var lateLines = Enumerable.Repeat("", 90).ToArray();
        lateLines[0] = "static class LateMethods {";
        lateLines[74] = "static int Load() => 42;";
        lateLines[89] = "}";
        File.WriteAllLines(Path.Combine(root, "LateMethods.cs"), lateLines);
        var late = new Research(root, "stub", "Internal", audit, (_, source) =>
        {
            Check(source.Contains("L75: static int Load()") && source.Contains("L90: }"),
                "normal file worker supplies named method after line 30 and actual EOF in a single input");
            return (FileJson(start: 75, end: 75), new TokenUsage(10, 5, 0, 0));
        });
        var lateReport = FinishFiles(late, late.Files("Explain late methods", ["LateMethods.cs"]));
        Check(lateReport.Contains("90/90 lines captured") && lateReport.Contains("[LateMethods.cs:75-75]"),
            "full-file coverage and precise late method citation survive compact handoff");
        var chunkCalls = 0;
        var automatic = new Research(root, "stub", "Internal", audit, (_, source) =>
        {
            Interlocked.Increment(ref chunkCalls);
            var citation = System.Text.RegularExpressions.Regex.Match(source, @"citation: \[.*:(\d+)-(\d+)\]");
            var start = int.Parse(citation.Groups[1].Value);
            var end = int.Parse(citation.Groups[2].Value);
            return (JsonSerializer.Serialize(new { purpose = new { text = "Contains fixture comments.", start, end },
                methods = Array.Empty<object>(), unknown = "" }), new TokenUsage(10, 5, 0, 0));
        });
        var automaticReport = FinishFiles(automatic, automatic.Files("Review bounded large file", ["Chunked.cs"]));
        Check(chunkCalls == chunked.Ranges.Length && automaticReport.Contains($"{chunkCalls}/{chunkCalls} inputs analyzed") &&
            automaticReport.Contains("file_status: partial") && automaticReport.Contains("800/800 lines captured"),
            "worker advances all file chunks without head range requests and preserves unknown method completeness");
        File.WriteAllLines(Path.Combine(root, "Interrupted.cs"),
            ["static int Load() {", new string('x', 16001), "return 42; }"]);
        var interrupted = new Research(root, "stub", "Internal", audit, (prompt, _) =>
        {
            Check(!prompt.Contains("complete bounded file") && prompt.Contains("portion of a larger file"),
                "single captured range before an oversized line is not mislabeled as complete file input");
            return (FileJson(start: 1, end: 1), new TokenUsage(10, 5, 0, 0));
        });
        var interruptedResult = FinishFiles(interrupted, interrupted.Files("Review split source", ["Interrupted.cs"]));
        Check(interruptedResult.Contains("1/3 lines captured") && interruptedResult.Contains("file_status: blocked") &&
            interruptedResult.Contains("method: Load (unknown)"),
            "oversized middle line preserves partial coverage and cannot claim a complete split method");
        File.WriteAllLines(Path.Combine(root, "Oversized.cs"), [new string('x', 16001), "// remaining"]);
        var oversized = new Research(root, "stub", "Internal", audit, (_, _) =>
            throw new InvalidOperationException("Inference must not run without captured source"));
        var oversizedResult = FinishFiles(oversized, oversized.Files("Review oversized line", ["Oversized.cs"]));
        Check(oversizedResult.Contains("0/2 lines captured") && oversizedResult.Contains("0/0 inputs analyzed"),
            "zero captured ranges still report actual file length rather than zero-line file");
        var malformed = new Research(root, "stub", "Internal", audit, (_, _) =>
            (FileJson().Replace("\"start\":1", "\"start\":99999999999999999999"), new TokenUsage(10, 5, 0, 0)));
        Check(FinishFiles(malformed, malformed.Files("Review malformed response", ["Methods.cs"]))
            .Contains("local analysis failed"), "malformed model numbers terminate with explicit failure, not a stuck running job");
        var active = 0;
        var maximum = 0;
        var serial = new Research(root, "stub", "Internal", audit, (_, source) =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            Thread.Sleep(20);
            Interlocked.Decrement(ref active);
            return (FileJson(source.StartsWith("file: Other.cs") ? "Save" : "Load"), new TokenUsage(10, 5, 0, 0));
        }, fileConcurrency: 1);
        FinishFiles(serial, serial.Files("Review serial defaults", ["Methods.cs", "Other.cs"]));
        Check(maximum == 1, "explicit serial override never overlaps local inferences");
        var failed = new Research(root, "stub", "Internal", audit, (_, _) =>
            throw new HttpRequestException("fixture unavailable"));
        var failedResult = FinishFiles(failed, failed.Files("Review file", ["Methods.cs"]));
        Check(failedResult.Contains("status: blocked") && failedResult.Contains("fixture unavailable") &&
            failedResult.Contains("0/1 inputs analyzed"), "inference failure preserves true captured versus analyzed coverage");
        var failedActivity = new Remote.ActivityState("remote-model", "stub");
        Remote.ReadProgress(audit, [], _ => { }, failedActivity.Observe);
        Check(failedActivity.Labels().Local is null && failedActivity.Labels().Coordinator!.Contains("provider state unknown"),
            "real failure/finally events clear local request activity instead of leaving a stuck local spinner");
        Check(failed.Files("Review file", ["Methods.cs"]).Contains("existing worklist"),
            "failed job replay is bounded, not an implicit inference retry loop");
        Check(Remote.GuardCompletion(audit, "status: done\nanswer: finished").StartsWith("status: blocked") &&
            !Remote.GuardCompletion(audit, "status: done\nanswer: finished").Contains("answer: finished"),
            "partial persisted file work prevents success-shaped final response");
        TestWholeScope(root, audit);
        TestFileRetryQueue(root, audit);
        TestOverviewFiles(root, audit);
        TestFileCompletion(root, audit);
        TestAllFileEvidence(root, audit);
    }

    static void TestAllFileEvidence(string root, string audit)
    {
        var folder = Path.Combine(root, "all-evidence");
        var logs = Path.Combine(audit, "all-evidence");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(logs);
        void Fixture(string name, int count) => File.WriteAllLines(Path.Combine(folder, name),
            Enumerable.Range(0, count).Select(i =>
                $"static int M{i:D3}() => {i}; // private-evidence-marker ".PadRight(2000, 'x')));
        string Brief(string source, bool reject = false)
        {
            var match = System.Text.RegularExpressions.Regex.Match(source, @"(?m)^L(\d+): static int (M\d+)");
            var line = int.Parse(match.Groups[1].Value);
            return JsonSerializer.Serialize(new
            {
                purpose = new { text = $"Defines fixture operation at line {line}.", start = line, end = line },
                methods = new[] { new { name = reject ? "UnsupportedInventedName" : match.Groups[2].Value,
                    text = "Returns a fixture value.", start = line, end = line, coverage = "unknown" } },
                unknown = ""
            });
        }
        try
        {
            Fixture("Evidence.cs", 20);
            var calls = 0;
            var worker = new Research(folder, "stub", "Internal", logs, (_, source) =>
                (Brief(source, ++calls == 1), new TokenUsage(10, 5, 0, 0)));
            var report = FinishFiles(worker, worker.Files("Review all collected findings", ["Evidence.cs"], detail: "full"));
            var evidence = new LocalRepository(folder).FileEvidence("Evidence.cs");
            Check(calls == 21 && Enumerable.Range(0, 20).All(i => report.Contains($"method: M{i:D3} ")) &&
                Enumerable.Range(1, 20).All(i => report.Contains($"purpose: Defines fixture operation at line {i}.")) &&
                evidence.Ranges.All(r => report.Contains($"reference: {r.Citation}")),
                "completed reports preserve all twenty methods, purposes and captured references beyond the former caps");
            Check(report.Contains("findings: local model interpretation; unverified") &&
                report.Contains("attempt_failure:") && report.Contains("analysis rejected; rejected output is not evidence") &&
                !report.Contains("UnsupportedInventedName") && !report.Contains("private-evidence-marker"),
                "recovered failures remain visible as metadata without forwarding rejected claims or raw source");
            var refreshed = FinishFiles(worker, worker.Files("Refresh evidence", ["Evidence.cs"], refresh: true, detail: "full"));
            Check(calls == 21 && refreshed.Contains("attempt_failure:") && refreshed.Contains("method: M019 "),
                "fingerprint-verified cache reuse preserves all findings and their failure history");
            Fixture("Budget.cs", 260);
            var budgetCalls = 0;
            var limited = new Research(folder, "stub", "Internal", logs, (_, source) =>
            {
                budgetCalls++;
                return (Brief(source), new TokenUsage(10, 5, 0, 0));
            });
            var partial = FinishFiles(limited, limited.Files("Review bounded budget", ["Budget.cs"], detail: "full"));
            Check(budgetCalls == 256 && partial.Contains("status: blocked") && partial.Contains("file_status: partial") &&
                partial.Contains("256/260 inputs analyzed") && partial.Contains("method: M255 ") &&
                !partial.Contains("method: M256 "),
                "budget stop delivers accepted partial findings without implying unfinished ranges were analyzed");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
            foreach (var file in Directory.EnumerateFiles(logs)) File.Delete(file);
            Directory.Delete(logs);
        }
    }

    static void TestPromptMode()
    {
        var request = "Explain \"the project\".\nKeep the workflow clear.";
        var options = PromptMode.Parse(["-p", request, "--yes", "--json", "--model", "head",
            "--local-model", "local:7b", "--sensitivity", "public", "--execution"]);
        Check(options.Prompt == request && options.Model == "head" && options.LocalModel == "local:7b" &&
            options.Json && options.Execution && options.Sensitivity == "Public",
            "prompt mode preserves request text and explicit model/permission options");
        var defaults = PromptMode.Parse(["--prompt", "Explain", "--yes"]);
        Check(!defaults.Execution && !defaults.Json && defaults.Model is null &&
            defaults.LocalModel is null && defaults.Sensitivity == "Internal",
            "prompt mode defaults keep shells disabled and do not change saved model settings");
        foreach (var invalid in new string[][] { ["--prompt", "Explain"], ["--yes"], ["--prompt"],
            ["--prompt", " ", "--yes"], ["--prompt", "Explain", "--yes", "--model"],
            ["--prompt", "Explain", "--yes", "-p", "Again"], ["--prompt", "Explain", "--yes", "--unknown"],
            ["--prompt", "Explain", "--yes", "--sensitivity", "Secret"] })
        {
            var rejected = false;
            try { PromptMode.Parse(invalid); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid automation arguments fail before any model call: " + string.Join(" ", invalid));
        }
        foreach (var (result, exit) in new[] { ("status: done\nanswer: A terminal assistant.", 0),
            ("status: blocked\nerrors: missing proof", 1), ("status: error\nerrors: bad arguments", 2) })
        {
            using var output = new StringWriter();
            Check(PromptMode.WriteResult(result, exit, true, output) == exit, "automation preserves exit classification");
            using var json = JsonDocument.Parse(output.ToString());
            Check(json.RootElement.GetProperty("output").GetString() == result &&
                json.RootElement.GetProperty("exitCode").GetInt32() == exit &&
                json.RootElement.GetProperty("status").GetString() == result.Split('\n')[0]["status: ".Length..],
                "automation JSON contains exactly one parseable result with status and exit code");
        }
        using var plain = new StringWriter();
        PromptMode.WriteResult("status: done\nanswer: Result", 0, false, plain);
        Check(plain.ToString().TrimEnd() == "status: done\nanswer: Result", "plain automation output is only the final result");
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        var originalBatch = Terminal.BatchMode;
        using var capturedOutput = new StringWriter();
        using var capturedError = new StringWriter();
        try
        {
            Console.SetOut(capturedOutput);
            Console.SetError(capturedError);
            Terminal.BatchMode = true;
            Terminal.Initialize();
            Terminal.WriteLine("progress fixture");
            Terminal.SetTaskActivity("local", "remote", TimeSpan.Zero);
            Terminal.Restore();
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Terminal.BatchMode = originalBatch;
        }
        Check(capturedOutput.ToString() == "" && capturedError.ToString().Trim() == "progress fixture",
            "batch progress goes only to stderr without terminal escape sequences");
        foreach (var arguments in new string[][] { ["--help"], ["--prompt", "-", "--yes", "--json"],
            ["--prompt", "Explain", "--json"], ["--prompt", "-", "--yes", "--json", "--model", "stub"] })
        {
            var start = Remote.SelfStart(arguments);
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.Environment["LEAN_LOCAL_BASE_URL"] = "http://not-loopback.invalid";
            using var process = Process.Start(start)!;
            process.StandardInput.Write(arguments.Contains("--model") ? "Explain \"this project\".\nDescribe its workflow." : " \n");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("Prompt mode validation subprocess timed out.");
            }
            var text = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();
            if (arguments[0] == "--help")
                Check(process.ExitCode == 0 && text.StartsWith("Usage: lean --prompt") && error == "",
                    "real CLI help exits without starting the REPL or contacting models");
            else
            {
                using var json = JsonDocument.Parse(text);
                var runtimeFailure = arguments.Contains("--model");
                var expectedExit = runtimeFailure ? 1 : 2;
                var expectedError = runtimeFailure ? "No local models available" :
                    arguments[1] == "-" ? "stdin is empty" : "--yes is required";
                Check(process.ExitCode == expectedExit && json.RootElement.GetProperty("exitCode").GetInt32() == expectedExit &&
                    error.Contains(expectedError) && !text.Contains('\x1b'),
                    "real CLI separates stdin/approval errors from provider failure with clean JSON and no remote calls");
            }
        }
    }

    static void TestFileCompletion(string root, string audit)
    {
        var folder = Path.Combine(audit, "completion");
        Directory.CreateDirectory(folder);
        try
        {
            var calls = 0;
            var worker = new Research(root, "stub", "Internal", folder, (_, _) =>
            {
                calls++;
                return (FileJson(), new TokenUsage(10, 5, 0, 0));
            });
            foreach (var invalid in new[] { "src\\LeanCli", "LeanCli", "." })
                Check(worker.Files("Explain project", [invalid]).StartsWith("status: error"),
                    "guessed directories are rejected before registering research scope");
            Check(Directory.GetFiles(folder, "job-*.json").Length == 0 && calls == 0,
                "invalid paths do not poison later valid jobs or spend local inferences");
            var started = worker.Files("Review settings helper", ["Methods.cs"]);
            var id = Field(started, "jobId");
            var ready = worker.FileStatus(id, Resume(started), 20);
            Check(ready.StartsWith("status: review"), "completion fixture reaches assessment without approving itself");
            using var suggestion = JsonDocument.Parse(Field(ready, "complete_with"));
            var fields = suggestion.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value.GetString());
            var citation = Field(ready, "reference");
            Check(fields.Count == 3 && !fields.ContainsKey("assessment") && ready.Contains("ADD your own assessment"),
                "completion hints supply exact routing and citation, never an automatic remote assessment");
            string Send(Dictionary<string, object?> arguments)
            {
                var initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""";
                var initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
                var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call",
                    @params = new { name = McpServer.Tool, arguments } });
                using var writer = new StringWriter();
                McpServer.Serve(new StringReader($"{initialize}\n{initialized}\n{request}\n"), writer, worker);
                using var response = JsonDocument.Parse(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
                if (response.RootElement.TryGetProperty("error", out var error))
                {
                    Check(error.GetProperty("code").GetInt32() == -32602, "completion schema failure uses invalid-params, not missing endpoint");
                    return error.GetProperty("message").GetString()!;
                }
                var result = response.RootElement.GetProperty("result");
                var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
                Check(result.GetProperty("isError").GetBoolean() == text.StartsWith("status: error"),
                    "completion MCP error flag preserves rejected sign-off");
                return text;
            }
            string Assess(string text) => Send(new(fields) { ["assessment"] = text });
            var missing = Assess("The helper persists settings; bounded overview reviewed.");
            Check(missing.Contains("file-complete is available") && missing.Contains("ONE exact captured citation") &&
                missing.Contains(citation) && missing.Contains("complete_with:") && missing.Contains("same job"),
                "missing citation returns an exact reference and actionable same-job recovery");
            foreach (var size in new[] { 601, 966, 1192, 1218 })
                Check(Assess((citation + " Reviewed bounded findings and gaps.").PadRight(size, 'x'))
                    .Contains($"has {size} characters; maximum is 600"),
                    $"oversized assessment reports actual length {size}, not a generic citation error");
            Check(Assess(citation + "\nReviewed.").Contains("replace line breaks/tabs with spaces") &&
                Assess("").Contains("assessment is required") &&
                Assess(citation + "0 Reviewed.").Contains("ONE exact captured citation"),
                "control characters, empty text and citation-prefix near misses cannot approve a job");
            foreach (var extra in new[] { "paths", "citations" })
                Check(Send(new(fields) { ["assessment"] = citation + " Reviewed.", [extra] = new[] { "Methods.cs" } })
                    .Contains($"remove unsupported arguments: {extra}"),
                    $"unsupported completion field {extra} is named with a correction");
            Check(Send(new(fields) { ["assessment"] = 42 }).Contains("'assessment' must be a string") &&
                Send(new(fields)).Contains("'assessment' must be a string"),
                "wrong-type and missing completion fields report the exact required argument");
            Check(worker.FileStatus(id, waitSeconds: 0).StartsWith("status: review") &&
                Remote.GuardCompletion(folder, "status: done").StartsWith("status: blocked") &&
                Remote.CompletionIssue(folder, "status: done")!.Contains(id) && calls == 1,
                "rejected sign-offs retain reviewed evidence and cannot be bypassed by an early final completion");
            var accepted = Assess((citation + " Reviewed bounded findings; semantic gaps remain.").PadRight(600, 'x'));
            Check(accepted.StartsWith("status: done") && calls == 1 &&
                Remote.GuardCompletion(folder, "status: done") == "status: done",
                "corrected 600-character cited sign-off completes through MCP without repeating inference");
            var final = "status: done\nanswer: The helper returns a configured setting.\nreferences: Methods.cs:1-4";
            string Hook(string tool, object arguments)
            {
                using var output = new StringWriter();
                var code = Remote.CompletionHook(folder, new StringReader(JsonSerializer.Serialize(new
                    { toolName = tool, toolArgs = arguments })), output);
                Check(code == 0, "completion hook processes valid input");
                return output.ToString().Trim();
            }
            Check(Hook("task_complete", new { summary = final }).Contains("\"permissionDecision\":\"deny\"") &&
                Remote.CompletionIssue(folder, final)!.Contains("bounded excerpt"),
                "autopilot is denied before finalization when approved overviews still lack direct source support");
            worker.Call("excerpt", path: "Methods.cs", start: 1, end: 4);
            Check(Hook("task_complete", new { summary = final }) == "{}" &&
                Hook("task_complete", JsonSerializer.Serialize(new { summary = final })) == "{}" &&
                Hook("lean-local-local_research", new { intent = "file-status" }) == "{}",
                "cited current source permits completion; string payloads work and unrelated tools keep normal permissions");
            var partial = final.Replace("status: done", "status: partial") + "\ngaps: startup behavior still unverified";
            Check(Hook("task_complete", new { summary = partial }).Contains("\"permissionDecision\":\"deny\"") &&
                Remote.GuardCompletion(folder, partial).StartsWith("status: blocked") &&
                !Remote.GuardCompletion(folder, partial).Contains("\nanswer:") &&
                Remote.GuardCompletion(folder, partial).Contains("gaps: startup behavior still unverified"),
                "partial prose is denied and withheld rather than delivered as an accepted answer");
            Check(Remote.GuardCompletion(folder, "status: error\nerrors: Ollama connection refused")
                .Contains("errors: Ollama connection refused"), "withheld answers retain infrastructure failure diagnostics");
            Remote.CreateCompletionPlugin(folder);
            var hookPath = Path.Combine(folder, "completion-guard", "com.github.copilot", "hooks", "hooks.json");
            using var hooks = JsonDocument.Parse(File.ReadAllText(hookPath));
            var entry = hooks.RootElement.GetProperty("hooks").GetProperty("preToolUse")[0];
            Check(entry.GetProperty("exec").GetString() == Environment.ProcessPath &&
                entry.GetProperty("args").EnumerateArray().Any(a => a.GetString() == "--completion-hook"),
                "request-scoped plugin invokes the current executable without shell quoting or global hook changes");
            var hookStart = Remote.SelfStart("--completion-hook", folder);
            hookStart.RedirectStandardInput = hookStart.RedirectStandardOutput = hookStart.RedirectStandardError = true;
            using var process = Process.Start(hookStart)!;
            process.StandardInput.WriteLine(JsonSerializer.Serialize(new { toolName = "task_complete", toolArgs = new { summary = partial } }));
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("Completion hook process timed out.");
            }
            Check(process.ExitCode == 0 && stdout.GetAwaiter().GetResult().Contains("\"permissionDecision\":\"deny\"") &&
                string.IsNullOrWhiteSpace(stderr.GetAwaiter().GetResult()),
                "real hook subprocess denies incomplete output through its JSON protocol without model calls");
            var planPath = Path.Combine(folder, "plan-proof.json");
            File.WriteAllText(planPath, JsonSerializer.Serialize(new { Status = "done", Generation = 2 }));
            Check(Remote.CompletionIssue(folder, final)!.Contains("bounded excerpt"),
                "newer plan snapshots invalidate older source proof");
            File.Delete(planPath);
            var jobPath = Directory.GetFiles(folder, "job-*.json").Single();
            var excerptPath = Directory.GetFiles(folder, "research-*.json").Single(path =>
                JsonSerializer.Deserialize<CopilotRun>(File.ReadAllText(path))!.Commands.Any(c => c.StartsWith("excerpt: ")));
            File.Move(jobPath, jobPath + ".saved");
            File.Move(excerptPath, excerptPath + ".saved");
            Check(Remote.CompletionIssue(folder, final)!.Contains("bounded excerpt"),
                "legacy research without file jobs still requires source proof");
            File.Move(excerptPath + ".saved", excerptPath);
            Check(Remote.CompletionIssue(folder, final) is null,
                "legacy research can complete with current cited source proof");
            File.Move(jobPath + ".saved", jobPath);
        }
        finally
        {
            var plugin = Path.Combine(folder, "completion-guard");
            if (Directory.Exists(plugin))
            {
                var hooks = Path.Combine(plugin, "com.github.copilot", "hooks");
                File.Delete(Path.Combine(hooks, "hooks.json"));
                Directory.Delete(hooks);
                Directory.Delete(Path.Combine(plugin, "com.github.copilot"));
                File.Delete(Path.Combine(plugin, "plugin.json"));
                Directory.Delete(plugin);
            }
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    static void TestOverviewFiles(string root, string audit)
    {
        var folder = Path.Combine(root, "overview-scope");
        var logs = Path.Combine(audit, "overview-scope");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(logs);
        try
        {
            for (var i = 0; i < 9; i++)
                File.WriteAllLines(Path.Combine(folder, $"File{i}.cs"), Enumerable.Range(1, 140)
                    .Select(n => n > 60 ? "// unseen-tail-marker" : $"// sample line {n}"));
            var calls = 0;
            var worker = new Research(folder, "stub", "Internal", logs, (prompt, source) =>
            {
                Interlocked.Increment(ref calls);
                var range = System.Text.RegularExpressions.Regex.Match(source, @"citation: \[.*:(\d+)-(\d+)\]");
                var start = int.Parse(range.Groups[1].Value);
                var end = int.Parse(range.Groups[2].Value);
                if (prompt.Contains("quick overview"))
                    Check(end <= 60 && !source.Contains("unseen-tail-marker"),
                        "overview sends bounded source, not every source chunk");
                return (JsonSerializer.Serialize(new { purpose = new { text = "Contains fixture comments.", start, end },
                    methods = Array.Empty<object>(), unknown = "" }), new TokenUsage(10, 5, 0, 0));
            });
            var evidence = new LocalRepository(folder).FileEvidence("File0.cs", overview: true);
            Check(evidence.Ranges.Length == 1 && evidence.Ranges[0].Lines.Length <= 60 &&
                evidence.Ranges[0].Lines.Sum(l => l.Length + 16) <= 3000 &&
                evidence.Gaps.Any(g => g.Contains("overview only")),
                "overview sample has explicit character/line limits and coverage gap");
            var started = worker.Files("Explain project", detail: "overview");
            var id = Field(started, "jobId");
            var first = worker.FileStatus(id, Resume(started), 20);
            Check(first.StartsWith("status: pending") && first.Contains("8/9 files reviewed") &&
                worker.FileComplete(id, Resume(first), "File0.cs:1-60").StartsWith("status: error"),
                "overview cannot complete after sampling only the first batch of files");
            var done = FinishFiles(worker, first);
            Check(calls == 9 && done.Contains("9/9 files reviewed") && done.Contains("60/140 lines captured") &&
                done.Contains("overview only") && !done.Contains("method:") && !done.Contains("unseen-tail-marker"),
                "every eligible file gets one purpose-only overview; unsampled lines stay explicit and private");
            worker.Files("Explain project", detail: "overview");
            Check(calls == 9, "identical overview reuses its job without inference");
            File.AppendAllText(Path.Combine(folder, "File0.cs"), "\n// changed unseen tail");
            var current = worker.FileStatus(id, waitSeconds: 0);
            Check(worker.FileComplete(id, Resume(current), "File0.cs:1-60").StartsWith("status: stale"),
                "overview fingerprints detect edits even outside sampled lines");
            FinishFiles(worker, worker.Files("Refresh overview", ["File0.cs"], refresh: true, detail: "overview"));
            Check(calls == 10, "overview refresh preserves all files and reuses eight unchanged samples");
            var before = calls;
            var full = FinishFiles(worker, worker.Files("Refresh overview", ["File0.cs"], detail: "full"));
            var fullLines = File.ReadAllLines(Path.Combine(folder, "File0.cs")).Length;
            Check(calls > before && full.Contains("detail: full") && full.Contains($"{fullLines}/{fullLines} lines captured"),
                "full review cannot reuse a cached overview as exhaustive analysis");
            Check(worker.Files("Invalid mode", detail: "guess").StartsWith("status: error"),
                "unknown overview depth fails explicitly");
            var rpc = """
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"local_research","arguments":{"intent":"files","query":"MCP default overview","paths":["File0.cs"]}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"local_research","arguments":{"intent":"deep-dive","detail":"overview","query":"Explain File0"}}}
""";
            using var writer = new StringWriter();
            McpServer.Serve(new StringReader(rpc), writer, worker);
            var replies = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            using var response = JsonDocument.Parse(replies[1]);
            var report = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            Check(report.Contains("detail: overview") && replies[2].Contains("\"error\""),
                "MCP defaults files to overview and rejects detail on other intents");
            FinishFiles(worker, report);
            Check(Remote.Prompt("explain this codebase", "Internal", false).Contains("detail=overview") &&
                Remote.Prompt("explain this codebase", "Internal", false).Contains("Use detail=full ONLY"),
                "remote explanation instructions select overview before targeted dives");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
            foreach (var file in Directory.EnumerateFiles(logs)) File.Delete(file);
            Directory.Delete(logs);
        }
    }

    static void TestFileRetryQueue(string root, string audit)
    {
        var retryAudit = Path.Combine(audit, "retry-queue");
        Directory.CreateDirectory(retryAudit);
        File.WriteAllLines(Path.Combine(root, "Retry.cs"),
            Enumerable.Range(1, 20).Select(i => $"// queue fixture {i}"));
        string Brief(string source)
        {
            var citation = System.Text.RegularExpressions.Regex.Match(source, @"citation: \[.*:(\d+)-(\d+)\]");
            return JsonSerializer.Serialize(new
            {
                purpose = new { text = "Contains bounded fixture source.",
                    start = int.Parse(citation.Groups[1].Value), end = int.Parse(citation.Groups[2].Value) },
                methods = Array.Empty<object>(), unknown = ""
            });
        }
        try
        {
            var order = new List<string>();
            var watch = Stopwatch.StartNew();
            var retry = new Research(root, "stub", "Internal", retryAudit, (_, source) =>
            {
                order.Add(source);
                if (order.Count == 1) throw new TaskCanceledException("fixture inference timeout");
                return (Brief(source), new TokenUsage(10, 5, 0, 0));
            }, fileConcurrency: 1);
            var result = FinishFiles(retry, retry.Files("Review both fixtures", ["Retry.cs", "Other.cs"]));
            Check(order.Count == 4 && order[1].Contains("file: Other.cs") &&
                order[2].Contains("Retry.cs:1-10") && order[3].Contains("Retry.cs:11-20") &&
                result.Contains("status: done") && result.Contains("20/20 lines captured"),
                "timeout splits input; unrelated queued file runs before retry; all original lines analyzed");
            Check(watch.Elapsed >= TimeSpan.FromSeconds(2), "retry uses backoff rather than immediate resubmission");
            Check(Remote.StopReason(retryAudit) is null, "successful adaptive recovery does not stop the remote");

            var corrections = 0;
            var corrected = new Research(root, "stub", "Internal", retryAudit, (prompt, _) =>
            {
                corrections++;
                if (corrections == 2)
                    Check(prompt.Contains("Previous attempt was rejected") &&
                        prompt.Contains("Imagined: method name absent from cited lines"),
                        "validation retry receives specific corrective feedback");
                return (FileJson(corrections == 1 ? "Imagined" : "Save"), new TokenUsage(10, 5, 0, 0));
            }, fileConcurrency: 1);
            Check(FinishFiles(corrected, corrected.Files("Review definitions", ["Other.cs"])).Contains("status: done") &&
                corrections == 2, "corrected validation retry completes without weakening citation checks");

            var attempts = 0;
            var down = new Research(root, "stub", "Internal", retryAudit, (_, _) =>
            {
                attempts++;
                throw new HttpRequestException("fixture server refused connection");
            }, fileConcurrency: 1);
            result = FinishFiles(down, down.Files("Review unavailable fixtures", ["Retry.cs", "Other.cs"]));
            Check(attempts == 1 && result.Contains("status: blocked") && result.Contains("remaining 2") &&
                Remote.StopReason(retryAudit)!.Contains("Ollama unavailable"),
                "server loss pauses the queue after one failure and signals remote stop, preserving pending files");
            Check(down.Call("excerpt", path: "Other.cs", start: 1, end: 1).StartsWith("status: blocked"),
                "terminal service failure cannot turn into remote excerpt spam");
            File.Delete(Path.Combine(retryAudit, "stop-request.json"));
            var badPaths = new Research(root, "stub", "Internal", retryAudit);
            for (var i = 0; i < 3; i++)
                badPaths.Call("excerpt", path: "missing.cs", start: 1, end: 1);
            Check(Remote.StopReason(retryAudit)!.Contains("Repeated research failure") &&
                badPaths.Call("excerpt", path: "missing.cs", start: 1, end: 1).StartsWith("status: blocked"),
                "three identical invalid requests signal host-enforced no-progress stop");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(retryAudit)) File.Delete(file);
            Directory.Delete(retryAudit);
        }
    }

    static void TestFileCapacity(string root, string audit)
    {
        var capacity = Ollama.ConfiguredFileConcurrency();
        var paths = Enumerable.Range(0, capacity + 1).Select(i => $"Worker{i}.cs").ToArray();
        foreach (var path in paths)
            File.WriteAllLines(Path.Combine(root, path),
                ["static class Worker", "{", "static int Load() => 42;", "}"]);
        using var entered = new CountdownEvent(capacity);
        using var release = new ManualResetEventSlim();
        var gate = new object();
        var active = 0;
        var maximum = 0;
        var calls = 0;
        var worker = new Research(root, "stub", "Internal", audit, (_, _) =>
        {
            lock (gate)
            {
                maximum = Math.Max(maximum, ++active);
                if (++calls <= capacity) entered.Signal();
            }
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Capacity release timeout");
                return (FileJson(), new TokenUsage(10, 5, 0, 0));
            }
            finally { lock (gate) active--; }
        });
        var started = worker.Files("Review files using the bounded default", paths);
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), $"default worker actually starts {capacity} concurrent offline inferences");
            lock (gate)
                Check(calls == capacity && maximum == capacity && capacity <= Math.Clamp(Environment.ProcessorCount, 1, 2),
                    "worker does not launch the extra file beyond its core-based capacity");
        }
        finally { release.Set(); }
        var result = FinishFiles(worker, started);
        Check(calls == paths.Length && maximum == capacity &&
            paths.Select(path => result.IndexOf("\nfile: " + path, StringComparison.Ordinal)).SequenceEqual(
                paths.Select(path => result.IndexOf("\nfile: " + path, StringComparison.Ordinal)).Order()),
            "bounded waves finish all files in deterministic order with serialized audit writes");
    }

    static void TestWholeScope(string root, string audit)
    {
        var scopeRoot = Path.Combine(root, "whole-scope");
        var scopeAudit = Path.Combine(audit, "whole-scope");
        Directory.CreateDirectory(scopeRoot);
        Directory.CreateDirectory(scopeAudit);
        void Fixture(string name) => File.WriteAllLines(Path.Combine(scopeRoot, name),
            ["static class Fixture", "{", "static int Load() => 42;", "}"]);
        try
        {
            for (var i = 0; i < 40; i++) Fixture($"File{i:D3}.cs");
            File.WriteAllText(Path.Combine(scopeRoot, ".private"), "excluded");
            File.WriteAllText(Path.Combine(scopeRoot, "image.dat"), "unsupported");
            var calls = 0;
            var research = new Research(scopeRoot, "stub", "Internal", scopeAudit, (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return (FileJson(), new TokenUsage(10, 5, 0, 0));
            });
            var started = research.Files("Review the whole project");
            var id = Field(started, "jobId");
            var first = research.FileStatus(id, Resume(started), 20);
            Check(first.StartsWith("status: pending") && first.Contains("8/40 files reviewed") &&
                first.Contains("remaining 32") && first.Contains("excluded entries/subtrees 2"),
                "whole-project scope snapshots all eligible files; first bounded batch exposes exact remaining/excluded counts");
            Check(research.FileComplete(id, Resume(first), "Everything is complete").StartsWith("status: error") &&
                Remote.GuardCompletion(scopeAudit, "status: complete").StartsWith("status: blocked"),
                "completion gate rejects stopping after the first batch");
            var done = FinishFiles(research, first);
            Check(calls == 40 && done.Contains("status: done") && done.Contains("40/40 files reviewed"),
                "remote-driven cursors progress beyond old eight-file and 32-analysis limits without duplicate work");
            Check(research.FileStatus(id, Resume(started), 0).Contains("delivery replay") && calls == 40,
                "old batch replay cannot rerun or skip files");
            Check(research.Call("excerpt", path: "File039.cs", start: 1, end: 4).Contains("L4:"),
                "file-analysis budget does not consume separate focused probe/excerpt allowance");
            Check(Remote.GuardCompletion(scopeAudit, "status: complete") == "status: complete",
                "only assessed full current scope can pass final completion gate");
            File.AppendAllText(Path.Combine(scopeRoot, "File000.cs"), "\n");
            Fixture("NewFile.cs");
            Check(Remote.GuardCompletion(scopeAudit, "status: complete").Contains("changed:"),
                "final guard detects edits and added files even without a requested refresh");
            var current = research.FileStatus(id, waitSeconds: 0);
            Check(research.FileComplete(id, Resume(current), "File000.cs:1-4 reviewed").StartsWith("status: stale"),
                "completion checks current fingerprints and inventory, not just old captured ranges");
            Check(Remote.GuardCompletion(scopeAudit, "status: complete").StartsWith("status: blocked"),
                "stale current scope cannot pass the final guard while awaiting refreshed work");
            var renewed = research.Files("Validate edits across the requested project", ["File000.cs"], refresh: true);
            var revised = FinishFiles(research, renewed);
            Check(revised.Contains("41/41 files reviewed") && revised.Contains("status: done") && calls == 42,
                "refresh preserves whole-project scope, includes new files and only reinfers modified/new source");
            Check(Remote.GuardCompletion(scopeAudit, "status: complete") == "status: complete",
                "revalidated edited snapshot passes while old approvals remain stale");
            research.Call("excerpt", path: "File039.cs", start: 1, end: 4, refresh: true);
            Check(Remote.GuardCompletion(scopeAudit, "status: complete").StartsWith("status: blocked"),
                "refresh alone does not count as revalidation or completion");
            var afterExcerpt = FinishFiles(research, research.Files("Revalidate after explicit excerpt refresh", ["File039.cs"]));
            Check(afterExcerpt.Contains("41/41 files reviewed") && afterExcerpt.Contains("status: done") && calls == 42,
                "excerpt refresh cannot erase whole-project scope or reinfer unchanged files");

            for (var i = 40; i < 260; i++) Fixture($"File{i:D3}.cs");
            var budgetCalls = 0;
            var limited = new Research(scopeRoot, "stub", "Internal", scopeAudit, (_, _) =>
            {
                Interlocked.Increment(ref budgetCalls);
                return (FileJson(), new TokenUsage(10, 5, 0, 0));
            });
            var exhausted = FinishFiles(limited, limited.Files("Review all eligible files within task budget"));
            Check(budgetCalls == 256 && exhausted.Contains("status: blocked") &&
                exhausted.Contains("task analysis budget 256 exhausted") && exhausted.Contains("remaining 5"),
                "finite task budget blocks explicitly with retained remaining work instead of silently sampling or reporting done");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(scopeRoot)) File.Delete(file);
            Directory.Delete(scopeRoot);
            foreach (var file in Directory.EnumerateFiles(scopeAudit)) File.Delete(file);
            Directory.Delete(scopeAudit);
        }
    }

    static PlanStep RangeStep(string id, string path, int start, int end) =>
        new(id, "Identify the behavior supported by this range",
            "Cite observed behavior and explicitly identify missing context", "ranges", [new(path, start, end)]);

    static void TestPlan(string root, string audit)
    {
        var inferences = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var research = new Research(root, "stub", "Restricted", audit, (prompt, evidence) =>
        {
            inferences++;
            Check(prompt.Contains("Completion criterion:"), "remote objective and criterion reach local analysis");
            if (inferences == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Test release timed out.");
            }
            var citation = System.Text.RegularExpressions.Regex.Match(evidence, @"citation: \[([^\]]+)\]").Groups[1].Value;
            return ($"answer: Observed fixture behavior [{citation}]", new TokenUsage(10, 5, 0, 0));
        });
        PlanStep[] steps = [RangeStep("entry", "Program.cs", 1, 60),
            RangeStep("component", "Component0.cs", 1, 30), RangeStep("manifest", "Project.csproj", 1, 1)];
        var planned = research.PlanCall("plan", "task", steps);
        Check(planned.StartsWith("status: partial") && inferences == 0, "three-step plan registered once without inference");
        var firstCursor = Resume(planned);
        var pending = Task.Run(() => research.PlanCall("continue", "task", resume: firstCursor));
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "bounded analysis started");
            var progress = new List<string>();
            Remote.ReadProgress(audit, [], progress.Add);
            Check(!pending.IsCompleted && progress.Any(p => p.Contains("Step 1/3") && p.Contains("analyzing captured")),
                "actual progress delivered through UI event reader BEFORE inference completes");
            Check(progress.All(p => !p.Contains("private-source-marker") && !p.Contains("Observed fixture")),
                "live progress contains no source or model output");
        }
        finally { release.Set(); }
        var first = pending.GetAwaiter().GetResult();
        Check(first.StartsWith("status: partial") && first.Contains("Program.cs:31-60") &&
            first.Contains("1/4 ranges analyzed"), "first chunk reports actual coverage and next unread range");
        var replay = research.PlanCall("continue", "task", resume: firstCursor);
        Check(replay.Contains("cursor replay") && inferences == 1, "same/parallel cursor cannot repeat inference or advance");
        var recovered = research.PlanCall("status", "task");
        Check(Resume(recovered) == Resume(first) && recovered.Contains("Observed fixture behavior"),
            "timeout status recovers current cursor and last compact findings without inference");
        var second = research.PlanCall("continue", "task", resume: Resume(first));
        Check(inferences == 2 && second.Contains("missing: remote criterion review"), "continuation reads second range, not cached first chunk");
        Check(research.PlanCall("accept", "task", resume: Resume(second), assessment: "Everything is fine").StartsWith("status: error"),
            "uncited completion rejected");
        var accepted = research.PlanCall("accept", "task", resume: Resume(second),
            assessment: "Observed behavior in Program.cs:1-30 and Program.cs:31-60; context gaps noted.");
        Check(accepted.Contains("entry=done") && accepted.Contains("Step 2/3"), "criterion review advances ordered step");
        Check(research.PlanCall("continue", "task", resume: firstCursor).StartsWith("status: error"),
            "older stale cursor rejected without work");
        var third = research.PlanCall("continue", "task", resume: Resume(accepted));
        accepted = research.PlanCall("accept", "task", resume: Resume(third), assessment: "Observed Component0.cs:1-30; context remains bounded.");
        var fourth = research.PlanCall("continue", "task", resume: Resume(accepted));
        Check(fourth.StartsWith("status: partial"), "all evidence read is not completion before remote review");
        var done = research.PlanCall("accept", "task", resume: Resume(fourth), assessment: "Project.csproj:1-1 supports the observed project metadata.");
        Check(done.StartsWith("status: done") && done.Contains("3/3 criteria reviewed") && inferences == 4,
            "plan done only after all four units analyzed and all three criteria reviewed");
        Check(research.PlanCall("plan", "task", steps).StartsWith("status: done") && inferences == 4,
            "repeated plan does not reset completed work");
        Check(Directory.EnumerateFiles(audit, "plan-*.json").Any(p => File.ReadAllText(p).Contains("Assessment")),
            "plan, attempts, evidence and criterion assessments persisted locally");
        var appended = research.PlanCall("plan", "task", [RangeStep("followup", "Component1.cs", 1, 30)]);
        Check(appended.Contains("entry=done") && appended.Contains("followup=partial"), "remote appends focused follow-up without restarting");
        appended = research.PlanCall("plan", "task", [RangeStep("later", "Component2.cs", 1, 30)]);
        var targeted = research.PlanCall("continue", "task", resume: Resume(appended), stepId: "later");
        Check(targeted.Contains("unit_step: Step 5/5 (later)") && targeted.Contains("Component2.cs:1-30") &&
            targeted.Contains("followup=partial"), "remote selects a later concrete dive while earlier work remains pending");
        Check(Remote.GuardCompletion(audit, "status: complete\nanswer: all done").StartsWith("status: blocked"),
            "unfinished persisted plan prevents success-shaped final status");
        research.Call("excerpt", path: "Program.cs", start: 1, end: 1, refresh: true);
        Check(research.PlanCall("status", "task").Contains("failure: snapshot_changed"),
            "refresh invalidates plan evidence rather than accepting stale coverage");
        var updated = research.PlanCall("plan", "task", [RangeStep("postedit", "Project.csproj", 1, 1)]);
        Check(updated.StartsWith("status: partial") && updated.Contains("snapshot 2") &&
            !updated.Contains("entry=done") && Directory.EnumerateFiles(audit, "history-*.json").Any(),
            "post-edit plan stays in same conversation with old evidence archived and approvals invalidated");
        Check(research.PlanCall("continue", "task", resume: Resume(done)).StartsWith("status: error"),
            "previous snapshot cursor cannot replay stale acceptance after replanning");

        var attempts = 0;
        var failing = new Research(root, "stub", "Internal", audit, (_, evidence) =>
        {
            if (++attempts == 1) throw new HttpRequestException("fixture connection refused");
            var citation = System.Text.RegularExpressions.Regex.Match(evidence, @"citation: \[([^\]]+)\]").Groups[1].Value;
            return ($"answer: Observed fixture [{citation}]", new TokenUsage(10, 5, 0, 0));
        });
        var result = failing.PlanCall("plan", "retry", [RangeStep("one", "Component1.cs", 1, 30)]);
        result = failing.PlanCall("continue", "retry", resume: Resume(result));
        Check(result.Contains("infrastructure_failure") && result.StartsWith("status: partial"),
            "infrastructure failure distinct from coverage with explicit retry");
        result = failing.PlanCall("continue", "retry", resume: Resume(result));
        Check(attempts == 2 && result.Contains("1/1 ranges analyzed"), "transient failure not cached; next cursor makes progress");
        var broken = new Research(root, "stub", "Internal", audit, (_, _) =>
            ("unknown: Unable to interpret the evidence", new TokenUsage(10, 5, 0, 0)));
        result = broken.PlanCall("plan", "broken", [RangeStep("one", "Component1.cs", 1, 30)]);
        result = broken.PlanCall("continue", "broken", resume: Resume(result));
        Check(result.Contains("model_failure") && result.Contains("0/1 ranges analyzed"), "unknown-only output is not substantive completion");
        result = broken.PlanCall("continue", "broken", resume: Resume(result));
        Check(result.StartsWith("status: blocked") && result.Contains("model_failure"), "two failed attempts terminate bounded work");
        var reads = broken.DiskReads;
        var stopped = broken.PlanCall("continue", "broken", resume: Resume(result));
        Check(stopped.StartsWith("status: blocked") && broken.DiskReads == reads, "blocked continuation cannot infinitely retry");
        Check(broken.PlanCall("accept", "broken", resume: Resume(result), assessment: "Component1.cs:1-30").StartsWith("status: error"),
            "cannot accept model failure");
        var limit = new Research(root, "stub", "Internal", audit, (_, _) =>
            ("answer: Short source [Project.csproj:1-1]", new TokenUsage(10, 5, 0, 0)));
        result = limit.PlanCall("plan", "bounds", [RangeStep("beyond", "Project.csproj", 1, 30)]);
        result = limit.PlanCall("continue", "bounds", resume: Resume(result));
        Check(result.StartsWith("status: blocked") && result.Contains("failure: coverage_limit"),
            "EOF mismatch is a coverage limit, never a completed target or model failure");
        var budget = new Research(root, "stub", "Internal", audit);
        Check(budget.PlanCall("plan", "budget", Enumerable.Range(1, 4)
            .Select(i => RangeStep($"large{i}", "Program.cs", 1, 240)).ToArray()).StartsWith("status: error"),
            "plan rejects more than 24 units before inference");
        result = budget.PlanCall("plan", "budget", [RangeStep("one", "Component1.cs", 1, 30)]);
        for (var i = 0; i < 32; i++) budget.Call("excerpt", path: "Program.cs", start: 1, end: 1);
        result = budget.PlanCall("continue", "budget", resume: Resume(result));
        Check(result.StartsWith("status: blocked") && result.Contains("budget_limit"),
            "cached calls still count toward request budget; no model invoked after exhaustion");
    }

    public static void LocalIntegration()
    {
        var models = Ollama.GetModels();
        var model = models.FirstOrDefault(m => m == "qwen2.5-coder:7b") ?? models.FirstOrDefault()
            ?? throw new InvalidOperationException("Local integration needs an already-running Ollama with an installed model.");
        var directory = Logger.CreateRequestDirectory();
        var root = Environment.CurrentDirectory;
        using var native = new NativeClient(root, model, directory);
        native.Initialize();
        var timer = Stopwatch.StartNew();
        CheckLocalPlan(native, directory);
        var overview = native.Call("overview", "Map purpose, components and target framework.");
        Console.WriteLine(overview);
        Check(overview.StartsWith("status: partial") && overview.Contains("answer:") &&
            (overview.Contains("net10.0") || overview.Contains(".NET 10")) &&
            !overview.Contains("<TargetFramework>") && !overview.Contains("\nL1:"),
            "localhost Ollama overview returns cited project facts, not captured source");
        var repeated = native.Call("overview", "Map purpose, components and target framework.");
        Check(repeated.Contains("cache: report reused"), "native MCP request cache avoids repeat inference");
        var deep = native.Call("deep-dive", "Inspect Summarize timeout handling and trace Summarize callers.", "src\\LeanCli\\Ollama.cs");
        Check((deep.Contains("answer:") || deep.Contains("flow:")) &&
            (deep.Contains("60") || deep.Contains("one minute") || deep.Contains("1 minute")) &&
            deep.Contains("Research.cs"), "localhost deep dive identifies Summarize timeout and caller evidence");
        var excerpt = native.Call("excerpt", "", "src\\LeanCli\\LeanCli.csproj", 1, 10);
        Check(excerpt.Contains("<TargetFramework>net10.0</TargetFramework>"), "native MCP explicit source excerpt");
        timer.Stop();
        var runs = Directory.EnumerateFiles(directory, "research-*.json")
            .Select(p => JsonSerializer.Deserialize<CopilotRun>(File.ReadAllText(p))!).ToArray();
        Check(runs.Length == 6 && runs.Count(r => r.Usage.Output > 0) == 4 &&
            runs.All(r => r.Location == "local"), "separate local usage; four inferences for six research units/probes");
        Console.WriteLine($"local-integration: passed; model={model}; input={runs.Sum(r => r.Usage.Input)}; " +
            $"output={runs.Sum(r => r.Usage.Output)}; wall={timer.Elapsed.TotalSeconds:F2}s; audit={directory}");
        Console.WriteLine(deep);
    }

    public static void OverviewIntegration()
    {
        const string model = "qwen2.5-coder:7b";
        Check(Ollama.GetModels().Contains(model), "overview integration requires the running local model");
        var root = Environment.CurrentDirectory;
        var expected = new LocalRepository(root).Discover();
        var audit = Logger.CreateRequestDirectory();
        using var native = new NativeClient(root, model, audit);
        native.Initialize();
        var timer = Stopwatch.StartNew();
        var started = native.Files("Explain this project's purpose and main components", [], detail: null);
        Check(started.Contains("detail: overview"), "native MCP defaults to overview when detail is omitted");
        var id = Field(started, "jobId");
        var cursor = Resume(started);
        var reports = new List<string>();
        Console.WriteLine($"overview-integration: files={expected.Length}; audit={audit}");
        for (var i = 0; i < 16; i++)
        {
            var result = native.FileStatus(id, cursor);
            reports.Add(result);
            Console.WriteLine(string.Join("\n", result.Split('\n').Where(l =>
                l.StartsWith("status:") || l.StartsWith("coverage:") || l.StartsWith("blockers:"))));
            cursor = Resume(result);
            if (!result.StartsWith("status: running") && !result.StartsWith("status: pending")) break;
        }
        var all = string.Join("\n", reports);
        Check(reports[^1].StartsWith("status: review") &&
            expected.All(p => all.Contains($"\nfile: {p}\n")) && all.Contains("overview only") &&
            !all.Contains("method:") && !all.Contains("\nL1:"),
            "real whole-project overview includes every eligible file with no raw handoff or invented method inventory");
        var reference = Field(reports[^1], "reference");
        Check(native.Complete(id, cursor, $"{reference} supports the bounded file-purpose overview; deeper behavior remains unverified.")
            .StartsWith("status: done"), "overview supports cited assessment without claiming exhaustive review");
        var runs = Directory.EnumerateFiles(audit, "research-*.json")
            .Select(p => JsonSerializer.Deserialize<CopilotRun>(File.ReadAllText(p))!).ToArray();
        Check(runs.Length <= expected.Length * 2 && runs.All(r => r.Location == "local" && r.Usage.Output <= 192),
            "whole-project overview stays within two attempts per file and short local outputs");
        Console.WriteLine($"overview-integration: passed; files={expected.Length}; calls={runs.Length}; " +
            $"output={runs.Sum(r => r.Usage.Output)}; wall={timer.Elapsed.TotalSeconds:F1}s");
    }

    public static void RetryIntegration()
    {
        const string model = "qwen2.5-coder:7b";
        Check(Ollama.GetModels().Contains(model), "retry integration uses already-running local model");
        var audit = Logger.CreateRequestDirectory();
        using var native = new NativeClient(Environment.CurrentDirectory, model, audit);
        native.Initialize();
        var watch = Stopwatch.StartNew();
        var started = native.Files("Summarize each file's purpose and explicitly defined methods with concise primary operations",
            ["src\\LeanCli\\Program.cs", "src\\LeanCli\\Logger.cs"]);
        var id = Field(started, "jobId");
        var cursor = Resume(started);
        var reports = new List<string>();
        for (var i = 0; i < 16; i++)
        {
            var result = native.FileStatus(id, cursor);
            reports.Add(result);
            Console.WriteLine(string.Join("\n", result.Split('\n').Where(l => l.StartsWith("status:") ||
                l.StartsWith("coverage:") || l.StartsWith("blockers:"))));
            cursor = Resume(result);
            if (!result.StartsWith("status: running") && !result.StartsWith("status: pending")) break;
        }
        var report = string.Join("\n", reports);
        Check(reports[^1].StartsWith("status: review") && reports[^1].Contains("2/2 files reviewed") &&
            report.Contains("method: Status ") && report.Contains("method: LogEventErrors ") &&
            !report.Contains("\nL1:"),
            "previously timed-out Program and Logger files analyzed with named method briefs and no raw handoff");
        var runs = Directory.EnumerateFiles(audit, "research-*.json")
            .Select(p => JsonSerializer.Deserialize<CopilotRun>(File.ReadAllText(p))!).ToArray();
        Check(runs.Length > 2 && runs.All(r => r.Location == "local"),
            "real multi-input file review traverses all chunks locally");
        Console.WriteLine($"retry-integration: passed; wall={watch.Elapsed.TotalSeconds:F1}s; " +
            $"input={runs.Sum(r => r.Usage.Input)}; output={runs.Sum(r => r.Usage.Output)}; audit={audit}");
    }

    public static void FileIntegration()
    {
        const string model = "qwen2.5-coder:7b";
        Check(Ollama.GetModels().Contains(model), "focused integration uses already-running localhost qwen2.5-coder:7b");
        var directory = Logger.CreateRequestDirectory();
        using var native = new NativeClient(Environment.CurrentDirectory, model, directory);
        native.Initialize();
        Console.WriteLine($"file-integration: model={model}; workers={Ollama.ConfiguredFileConcurrency()}; audit={directory}");
        var started = native.Files("Summarize each file's purpose and name every explicitly defined method with its primary operation",
            ["src\\LeanCli\\Settings.cs"]);
        var id = Field(started, "jobId");
        var cursor = Resume(started);
        var reports = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var result = native.FileStatus(id, cursor);
            reports.Add(result);
            Console.WriteLine(result);
            cursor = Resume(result);
            if (!result.StartsWith("status: running") && !result.StartsWith("status: pending")) break;
        }

        var report = string.Join("\n", reports);
        Check(reports[^1].StartsWith("status: review") && report.Contains("method: LoadRemoteModel (complete)") &&
            report.Contains("method: SaveRemoteModel (complete)") &&
            report.Contains("file", StringComparison.OrdinalIgnoreCase) &&
            !report.Contains("File.WriteAllText(") && !report.Contains("L1:"),
            "real localhost full-file brief describes both settings methods without raw source");
        var methodLines = report.Split('\n').Where(l => l.StartsWith("method:")).ToArray();
        var saveLine = methodLines.Single(l => l.StartsWith("method: SaveRemoteModel "));
        var loadLine = methodLines.Single(l => l.StartsWith("method: LoadRemoteModel "));
        Check(methodLines.Length == 2 &&
            (loadLine.Contains(": Loads", StringComparison.OrdinalIgnoreCase) || loadLine.Contains(": Reads", StringComparison.OrdinalIgnoreCase)) &&
            (saveLine.Contains(": Saves", StringComparison.OrdinalIgnoreCase) || saveLine.Contains(": Writes", StringComparison.OrdinalIgnoreCase)) &&
            !saveLine.Contains("does nothing", StringComparison.OrdinalIgnoreCase) &&
            (!saveLine.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                saveLine.Contains("log", StringComparison.OrdinalIgnoreCase)),
            "real method inventory has no duplicates and does not misreport SaveRemoteModel's error logging");
        var completed = native.Complete(id, cursor,
            "src\\LeanCli\\Settings.cs:1-41 supports the JSON load/save primary operations; bounded interpretation and semantic gaps reviewed.");
        Check(completed.StartsWith("status: done"), "real remote-style scope assessment completes the current full-file snapshot");
        var replay = native.FileStatus(id, Resume(started));
        Check(replay.Contains("delivery replay"), "real MCP file delivery replay reuses completed work");
        var dive = native.Call("deep-dive", "Describe TokenUsage Input and Output fields as token counts.",
            "src\\LeanCli\\Models.cs", 1, 14);
        Console.WriteLine(dive);
        Check(dive.Contains("analysis: valid") && dive.Contains("Input", StringComparison.OrdinalIgnoreCase) &&
            dive.Contains("Output", StringComparison.OrdinalIgnoreCase) && dive.Contains("token", StringComparison.OrdinalIgnoreCase),
            "after consuming file briefs, a focused local dive supplies concrete token-field details");
        var runs = Directory.EnumerateFiles(directory, "research-*.json")
            .Select(p => JsonSerializer.Deserialize<CopilotRun>(File.ReadAllText(p))!).ToArray();
        Check(runs.Length == 2 && runs.All(r => r.Usage.Measured && r.Usage.Input > 0 && r.Usage.Output > 0) &&
            runs.Any(r => r.Context.Contains("L38:")), "real full-file input and focused record-field dive retained locally with measured usage");
        Console.WriteLine($"file-integration: passed; input={runs.Sum(r => r.Usage.Input)}; " +
            $"output={runs.Sum(r => r.Usage.Output)}; audit={directory}");
    }

    public static void ScopeIntegration()
    {
        const string model = "qwen2.5-coder:7b";
        Check(Ollama.GetModels().Contains(model), "scope integration uses already-running localhost model");
        var root = Path.Combine(AppContext.BaseDirectory, $"scope-integration-{Guid.NewGuid():N}");
        var audit = Logger.CreateRequestDirectory();
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 9; i++)
                File.WriteAllLines(Path.Combine(root, $"Unit{i:D3}.cs"),
                    [$"static class Unit{i:D3}", "{", "public static int ReadValue() => 42;", "}"]);
            using var native = new NativeClient(root, model, audit);
            native.Initialize();
            Console.WriteLine($"scope-integration: workers={Ollama.ConfiguredFileConcurrency()}; audit={audit}");
            (string Last, string Reports) Drain(string started)
            {
                var jobId = Field(started, "jobId");
                var cursor = Resume(started);
                var reports = new List<string>();
                for (var i = 0; i < 20; i++)
                {
                    var response = native.FileStatus(jobId, cursor);
                    reports.Add(response);
                    Console.WriteLine(string.Join("\n", response.Split('\n').Where(l => l.StartsWith("status:") ||
                        l.StartsWith("coverage:") || l.StartsWith("budget:") || l.StartsWith("blockers:"))));
                    cursor = Resume(response);
                    if (!response.StartsWith("status: running") && !response.StartsWith("status: pending"))
                        return (response, string.Join("\n", reports));
                }
                throw new InvalidOperationException("Scope integration exceeded its bounded polling window.");
            }
            var start = native.Files("Review every project file and summarize each explicitly defined method's primary operation", []);
            var result = Drain(start);
            Check(result.Last.StartsWith("status: review") && result.Reports.Contains("status: pending") &&
                result.Last.Contains("9/9 files reviewed") &&
                result.Reports.Split('\n').Count(l => l.StartsWith("method: ReadValue ")) == 9,
                "real native whole-scope job crosses batch boundary and returns all nine named-method file briefs");
            var id = Field(start, "jobId");
            var done = native.Complete(id, Resume(result.Last),
                "Unit000.cs:1-4 and all nine file briefs support constant-return primary operations; bounded semantic gaps reviewed.");
            Check(done.StartsWith("status: done"), "real whole-scope completion requires current cited remote assessment");
            File.AppendAllText(Path.Combine(root, "Unit008.cs"), "// changed fixture\n");
            Check(native.Complete(id, Resume(done), "Unit000.cs:1-4 reviewed").StartsWith("status: stale"),
                "real native completion rejects a changed file before refresh");
            var refresh = native.Files("Validate changed files within the original project scope", ["Unit008.cs"], refresh: true);
            var updated = Drain(refresh);
            Check(updated.Last.StartsWith("status: review") && updated.Last.Contains("9/9 files reviewed"),
                "real refresh retains original whole-project scope despite a narrower changed-file selection");
            Check(native.Complete(Field(refresh, "jobId"), Resume(updated.Last),
                "Unit008.cs:1-5 revalidated after edit; unchanged file fingerprints and prior semantic gaps reviewed.")
                .StartsWith("status: done"), "real edited scope completes only after revalidation");
            var runs = Directory.EnumerateFiles(audit, "research-*.json")
                .Select(p => JsonSerializer.Deserialize<CopilotRun>(File.ReadAllText(p))!).ToArray();
            Check(runs.Length == 10 && runs.All(r => r.Usage.Measured && r.ExitCode == 0),
                "real scope cache performs nine initial analyses plus one changed-file analysis, not a complete rerun");
            Console.WriteLine($"scope-integration: passed; input={runs.Sum(r => r.Usage.Input)}; " +
                $"output={runs.Sum(r => r.Usage.Output)}; audit={audit}");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    static void CheckLocalPlan(NativeClient native, string directory)
    {
        var plan = native.Plan("plan", steps:
        [
            new("framework", "Identify the project target framework",
                "A cited finding names net10.0", "ranges", [new("src\\LeanCli\\LeanCli.csproj", 1, 9)]),
            new("usage", "Identify how TokenUsage represents input and output token counts",
                "A cited finding identifies Input and Output token counts", "ranges", [new("src\\LeanCli\\Models.cs", 12, 12)])
        ]);
        plan = native.Plan("continue", resume: Resume(plan));
        Console.WriteLine(plan);
        Check(plan.Contains("analysis: valid") && plan.Contains("net10.0") &&
            plan.StartsWith("status: partial"), "real local plan substantiates framework but remains partial before review");
        plan = native.Plan("accept", resume: Resume(plan),
            assessment: "src\\LeanCli\\LeanCli.csproj:1-9 identifies net10.0 as the target framework.");
        plan = native.Plan("continue", resume: Resume(plan));
        Console.WriteLine(plan);
        Check(plan.Contains("analysis: valid") && plan.Contains("input", StringComparison.OrdinalIgnoreCase) &&
            plan.Contains("output", StringComparison.OrdinalIgnoreCase),
            "real local continuation substantiates token accounting fields");
        plan = native.Plan("accept", resume: Resume(plan),
            assessment: "src\\LeanCli\\Models.cs:12-12 defines Input and Output token counts.");
        Check(plan.StartsWith("status: done"), "real native MCP two-step plan reaches reviewed done");
        Console.WriteLine(plan);
        var progressEvents = new List<string>();
        Remote.ReadProgress(directory, [], progressEvents.Add);
        foreach (var progress in progressEvents.Where(p => p.Contains("Step "))) Console.WriteLine(progress);
        Console.WriteLine($"local-plan-integration: passed; audit={directory}");
    }

    sealed class NativeClient : IDisposable
    {
        readonly Process Process;
        readonly Task<string> Errors;
        int Id;
        public NativeClient(string root, string model, string audit)
        {
            var start = Remote.SelfStart("--mcp-server", root, model, "Internal", audit);
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            Process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Cannot start native MCP host.");
            Errors = Process.StandardError.ReadToEndAsync();
        }
        JsonElement Request(string method, object parameters)
        {
            Process.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++Id, method, @params = parameters }));
            Process.StandardInput.Flush();
            var line = Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("MCP stdout closed unexpectedly.");
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"MCP error: {error}");
            if (doc.RootElement.GetProperty("id").GetInt32() != Id)
                throw new InvalidOperationException("MCP response ID mismatch.");
            return doc.RootElement.GetProperty("result").Clone();
        }
        public void Initialize()
        {
            var init = Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "lean-self-test", version = "1" } });
            Check(init.GetProperty("capabilities").TryGetProperty("tools", out _), "native MCP initializes tool capability");
            Check(init.GetProperty("serverInfo").GetProperty("version").GetString()!.EndsWith(McpServer.BuildId),
                "native MCP reports exact current assembly build identity");
            Process.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            Process.StandardInput.Flush();
            var list = Request("tools/list", new { });
            Check(list.GetProperty("tools")[0].GetProperty("name").GetString() == "local_research",
                "native MCP lists local_research");
            var schema = list.GetProperty("tools")[0].GetProperty("inputSchema");
            Check(schema.GetProperty("properties").GetProperty("intent").GetProperty("enum")
                .EnumerateArray().Any(v => v.GetString() == "files") &&
                schema.GetProperty("properties").GetProperty("waitSeconds").GetProperty("maximum").GetInt32() == 20,
                "native MCP advertises file jobs and bounded status schema");
            Request("ping", new { });
        }
        public string Call(string intent, string query = "", string? path = null, int start = 0, int end = 0)
        {
            var arguments = new Dictionary<string, object> { ["intent"] = intent, ["query"] = query };
            if (path is not null) arguments["path"] = path;
            if (start != 0) arguments["start"] = start;
            if (end != 0) arguments["end"] = end;
            return ToolCall(arguments);
        }
        public string Plan(string intent, PlanStep[]? steps = null, string? resume = null, string? assessment = null)
        {
            var arguments = new Dictionary<string, object> { ["intent"] = intent, ["planId"] = "integration" };
            if (steps is not null)
                arguments["steps"] = steps.Select(s => new { id = s.Id, objective = s.Objective,
                    criterion = s.Criterion, mode = s.Mode,
                    targets = s.Targets.Select(t => new { path = t.Path, start = t.Start, end = t.End }) });
            if (resume is not null) arguments["resume"] = resume;
            if (assessment is not null) arguments["assessment"] = assessment;
            return ToolCall(arguments);
        }
        public string Files(string query, string[] paths, bool refresh = false, string? detail = "full")
        {
            var arguments = new Dictionary<string, object>
                { ["intent"] = "files", ["query"] = query, ["paths"] = paths, ["refresh"] = refresh };
            if (detail is not null) arguments["detail"] = detail;
            return ToolCall(arguments);
        }
        public string FileStatus(string id, string? resume) => ToolCall(new()
            { ["intent"] = "file-status", ["jobId"] = id, ["resume"] = resume!, ["waitSeconds"] = 20 });
        public string Complete(string id, string resume, string assessment) => ToolCall(new()
            { ["intent"] = "file-complete", ["jobId"] = id, ["resume"] = resume, ["assessment"] = assessment });
        string ToolCall(Dictionary<string, object> arguments)
        {
            var result = Request("tools/call", new { name = "local_research", arguments });
            var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            Check(result.GetProperty("isError").GetBoolean() == text.StartsWith("status: error"),
                "native MCP tool error flag matches status");
            return text;
        }
        public void Dispose()
        {
            Process.StandardInput.Close();
            if (!Process.WaitForExit(10000))
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit();
                throw new InvalidOperationException("Test MCP host failed to exit on EOF.");
            }
            var errors = Errors.GetAwaiter().GetResult();
            Check(Process.ExitCode == 0 && errors.Length == 0, "native MCP clean EOF shutdown; stdout protocol only");
            Process.Dispose();
        }
    }
}
