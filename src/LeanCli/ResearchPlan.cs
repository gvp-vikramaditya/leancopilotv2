using System.Text.Json;
using System.Text.RegularExpressions;

sealed record PlanTarget(string Path, int Start, int End);
sealed record PlanStep(string Id, string Objective, string Criterion, string Mode, PlanTarget[] Targets);

sealed partial class Research
{
    sealed class Work(PlanTarget target)
    {
        public PlanTarget Target { get; } = target;
        public int Attempts { get; set; }
        public bool Read { get; set; }
        public bool Analyzed { get; set; }
        public string Report { get; set; } = "";
    }

    sealed class Step(PlanStep definition, Work[] work)
    {
        public PlanStep Definition { get; } = definition;
        public Work[] Work { get; } = work;
        public string Assessment { get; set; } = "";
        public string Failure { get; set; } = "";
        public bool Done => Assessment.Length > 0 && Work.All(w => w.Analyzed);
    }

    readonly List<Step> Steps = [];
    string? PlanId;
    int PlanSnapshot;
    int Revision;
    int ProgressSequence;
    int ControlCalls;
    string? LastResume;
    string LastResult = "";
    bool Stale;

    public string PlanCall(string intent, string? planId, PlanStep[]? steps = null,
        string? resume = null, string? assessment = null, string? stepId = null)
    {
        lock (StateLock)
        {
            if (FileJobRunning)
                return $"status: error\nsensitivity: {sensitivity}\nerrors: file worker active; retrieve file-status before advanced plan work";
            return PlanCallCore(intent, planId, steps, resume, assessment, stepId);
        }
    }

    string PlanCallCore(string intent, string? planId, PlanStep[]? steps,
        string? resume, string? assessment, string? stepId)
    {
        try
        {
            if (++ControlCalls > 96)
                throw new ArgumentException("Plan control limit reached (96 calls); stop and report blocked.");
            if (string.IsNullOrWhiteSpace(planId) || !Regex.IsMatch(planId, @"^[A-Za-z0-9_-]{1,40}$"))
                throw new ArgumentException("planId must be 1..40 letters, digits, underscores or hyphens.");
            if (PlanId is not null && PlanId != planId)
                throw new ArgumentException("One plan per MCP session; reuse planId and append steps, not a new plan.");
            if (intent == "plan")
            {
                if (steps is null || steps.Length == 0 || steps.Length > 8)
                    throw new ArgumentException("plan requires 1..8 ordered steps with objective, criterion, mode and targets.");
                var additions = new List<Step>();
                foreach (var definition in steps)
                {
                    if (!Regex.IsMatch(definition.Id ?? "", @"^[A-Za-z0-9_-]{1,40}$") ||
                        !ValidText(definition.Objective, 400) || !ValidText(definition.Criterion, 400) ||
                        definition.Mode is not ("overview" or "ranges") || definition.Targets is null)
                        throw new ArgumentException("Each step needs an id, concrete objective and completion criterion (1..400 chars), mode overview/ranges, targets.");
                    var existing = Stale ? null : Steps.FirstOrDefault(s => s.Definition.Id == definition.Id);
                    if (existing is not null)
                    {
                        if (JsonSerializer.Serialize(existing.Definition) != JsonSerializer.Serialize(definition))
                            throw new ArgumentException("Existing steps are immutable; append narrower follow-ups without erasing evidence.");
                        continue;
                    }
                    if (additions.Any(s => s.Definition.Id == definition.Id))
                        throw new ArgumentException("Duplicate step id.");
                    PlanTarget[] targets;
                    if (definition.Mode == "overview")
                    {
                        if (definition.Targets.Length != 0)
                            throw new ArgumentException("overview targets must be empty; local discovery selects bounded samples.");
                        var sample = Repository.Gather("overview", definition.Objective, null);
                        targets = sample.Ranges.Select(r => new PlanTarget(r.Path, r.Start, r.End)).ToArray();
                    }
                    else
                    {
                        if (definition.Targets.Length is < 1 or > 16)
                            throw new ArgumentException("ranges requires 1..16 concrete discovered path/start/end targets.");
                        targets = definition.Targets.Select(t =>
                        {
                            if (t.Start < 1 || t.End < t.Start || t.End > 10000 || t.End - t.Start >= 240)
                                throw new ArgumentException("Each target must cover 1..240 lines within 1..10000.");
                            return t with { Path = Repository.CanonicalPath(t.Path) };
                        }).ToArray();
                    }
                    var work = targets.SelectMany(t => Enumerable.Range(0, (t.End - t.Start) / 30 + 1)
                        .Select(i => new Work(new(t.Path, t.Start + i * 30, Math.Min(t.End, t.Start + i * 30 + 29)))))
                        .DistinctBy(w => w.Target).ToArray();
                    if (work.Length == 0)
                        throw new ArgumentException("No readable evidence targets found; coverage blocked.");
                    additions.Add(new(definition, work));
                }
                if ((Stale ? 0 : Steps.Count) + additions.Count > 8 ||
                    (Stale ? 0 : Steps.Sum(s => s.Work.Length)) + additions.Sum(s => s.Work.Length) > 24)
                    throw new ArgumentException("Plan budget is 8 steps / 24 bounded units; narrow the plan.");
                if (Stale)
                {
                    WriteAtomic(Path.Combine(auditDirectory, $"history-{Session}-{Revision}.json"), PlanDocument());
                    Steps.Clear();
                    Stale = false;
                    LastResume = null;
                    LastResult = "";
                }
                PlanId = planId;
                PlanSnapshot = Generation;
                Steps.AddRange(additions);
                if (additions.Count > 0)
                {
                    LastResume = null;
                    LastResult = "";
                    Revision++;
                    SavePlan();
                    Publish($"Plan: {Steps.Count} steps, {Steps.Sum(s => s.Work.Length)} bounded units; ready");
                }
                return PlanStatus() + (additions.Count > 0 && additions.Any(s => s.Definition.Mode == "overview")
                    ? "\npaths: " + OneLine(string.Join("; ", Repository.Discover().Take(40))) +
                        (Repository.Discover().Length > 40 ? "; remaining paths omitted" : "") : "");
            }
            if (PlanId is null)
                throw new ArgumentException("Submit intent=plan first.");
            if (intent == "status")
                return PlanStatus() + (LastResult.Length > 0
                    ? "\nlast_result (retained for timeout recovery; use the current resume above):\n" + LastResult : "");
            if (Stale)
                return PlanStatus();
            // Replayed or parallel calls with the same cursor cannot advance a second unit.
            if (resume is not null && resume == LastResume)
                return LastResult + "\ncache: cursor replay; use the returned next resume, not refresh";
            if (resume != Cursor())
                throw new ArgumentException("Missing/stale resume; call status and use its exact cursor.");
            var step = stepId is null ? Steps.FirstOrDefault(s => !s.Done) :
                Steps.FirstOrDefault(s => s.Definition.Id == stepId)
                    ?? throw new ArgumentException("Unknown stepId.");
            if (step is null)
                return PlanStatus();
            if (step.Done)
                return PlanStatus();
            var currentReport = "";
            if (intent == "accept")
            {
                if (!step.Work.All(w => w.Analyzed) || !ValidText(assessment, 600))
                    throw new ArgumentException("accept requires all units analyzed and a cited assessment of the criterion (1..600 chars).");
                if (!step.Work.Any(w => assessment!.Contains(Citation(w), StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Assessment must cite captured evidence for the criterion; no unsupported completion.");
                step.Assessment = assessment!;
                step.Failure = "";
                Publish(StepLabel(step) + ": done; criterion reviewed by remote");
            }
            else if (intent == "continue")
            {
                var work = step.Work.FirstOrDefault(w => !w.Analyzed);
                if (work is null || work.Attempts >= 2 || ProbeCalls >= 32)
                    return PlanStatus();
                work.Attempts++;
                SavePlan();
                var t = work.Target;
                Publish(StepLabel(step) + $": reading {Citation(work)} (attempt {work.Attempts}/2)");
                work.Report = Call("deep-dive", step.Definition.Objective + "\nCompletion criterion: " +
                    step.Definition.Criterion, t.Path, t.Start, t.End, progress: phase =>
                    {
                        if (phase == "analyzing captured evidence locally")
                            Publish(StepLabel(step) + ": " + phase);
                    });
                currentReport = work.Report;
                work.Read = work.Report.Contains("\nreference: " + Citation(work) + ";", StringComparison.Ordinal);
                work.Analyzed = work.Read && work.Report.Contains("\nanalysis: valid\n", StringComparison.Ordinal);
                step.Failure = work.Analyzed ? "" :
                    work.Report.Split('\n').FirstOrDefault(l => l.StartsWith("analysis: "))?["analysis: ".Length..] ?? "coverage_limit";
                if (!work.Read && step.Failure is "valid" or "invalid_request")
                {
                    step.Failure = "coverage_limit";
                    work.Attempts = 2;
                }
                Publish(StepLabel(step) + $": read {step.Work.Count(w => w.Read)}/{step.Work.Length} ranges; " +
                    $"analyzed {step.Work.Count(w => w.Analyzed)}/{step.Work.Length}; " +
                    (work.Analyzed ? "partial; next review or range" : $"{step.Failure}; {(work.Attempts < 2 ? "retry available" : "blocked")}"));
            }
            else
                throw new ArgumentException("Use plan, continue, accept or status.");
            LastResume = resume;
            Revision++;
            SavePlan();
            // Only the current unit's compact report travels; earlier reports remain in the audit.
            LastResult = PlanStatus() + (currentReport.Length > 0 ? $"\nunit_step: {StepLabel(step)}\n" +
                string.Join("\n", currentReport.Split('\n').Where(line => Regex.IsMatch(line,
                    @"^(analysis|evidence|reference|answer|flow|constraints|errors|unknown|gaps):") ||
                    line.StartsWith("cache: report reused", StringComparison.Ordinal))) : "");
            return LastResult;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Plan {planId}: {ex.Message}");
            return $"status: error\nsensitivity: {sensitivity}\nfailure: " +
                $"{(ex is ArgumentException ? "invalid_plan" : "infrastructure_failure")}\nerrors: {OneLine(ex.Message)}\nnext: correct arguments or report blocked";
        }
    }

    static bool ValidText(string? text, int max) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= max && !text.Any(char.IsControl);
    string Cursor() => $"{Session}-{Revision}";
    static string Citation(Work work) => $"{work.Target.Path}:{work.Target.Start}-{work.Target.End}";
    string StepLabel(Step step) => $"Step {Steps.IndexOf(step) + 1}/{Steps.Count} ({step.Definition.Id})";

    string PlanStatus()
    {
        var step = Steps.FirstOrDefault(s => !s.Done);
        var work = step?.Work.FirstOrDefault(w => !w.Analyzed);
        var blocked = PlanState() == "blocked";
        return $"status: {PlanState()}\n" +
            $"sensitivity: {sensitivity}\nplan: {PlanId}; snapshot {PlanSnapshot}; current snapshot {Generation}\n" +
            $"coverage: {Steps.Count(s => s.Done)}/{Steps.Count} criteria reviewed; " +
            $"{Steps.Sum(s => s.Work.Count(w => w.Analyzed))}/{Steps.Sum(s => s.Work.Length)} ranges analyzed\n" +
            $"step: {(step is null ? "all" : StepLabel(step))}\n" +
            "steps: " + string.Join("; ", Steps.Select(s => $"{s.Definition.Id}=" +
                (s.Done ? "done" : s.Work.Any(w => !w.Analyzed && w.Attempts >= 2) ? "blocked" :
                    s.Work.All(w => w.Analyzed) ? "review" : "partial"))) + "\n" +
            $"failure: {(Stale ? "snapshot_changed" : blocked && ProbeCalls >= 32 ? "budget_limit" : step?.Failure is { Length: > 0 } f ? f : "none")}\n" +
            $"missing: {(step is null ? "none within accepted plan; task-wide sufficiency remains remote responsibility" : work is null ? "remote criterion review" : Citation(work))}\n" +
            $"next: {(Stale ? "submit updated plan with same planId for new snapshot; old evidence/approvals cannot be reused" :
                blocked ? "stop; report blocked with retained evidence" : step is null ? "append focused steps if request needs more evidence; otherwise finish" : work is null ? "accept with cited assessment, or append follow-up steps; do not claim completion" : "continue sequentially; do not parallelize local calls")}\n" +
            $"resume: {Cursor()}\n" +
            (work is null && step is not null ? "references: " + string.Join("; ", step.Work.Select(Citation)) + "\n" : "") +
            "gaps: only planned bounded ranges; overview is a sample, not full repository coverage; no AST; interpretations require remote verification";
    }

    void InvalidatePlan()
    {
        if (PlanId is null) return;
        Stale = true;
        Revision++;
        SavePlan();
        Publish("Plan blocked: snapshot changed; retained evidence is stale");
    }

    string PlanState() => Stale || Steps.Any(s => s.Work.Any(w => !w.Analyzed && (w.Attempts >= 2 || ProbeCalls >= 32)))
        ? "blocked" : Steps.All(s => s.Done) ? "done" : "partial";

    object PlanDocument() => new { PlanId, PlanSnapshot, Generation, Revision, Resume = Cursor(),
        Calls, ControlCalls, Status = PlanState(), Steps };

    void SavePlan() => WriteAtomic(Path.Combine(auditDirectory, $"plan-{Session}.json"), PlanDocument());

    void Publish(string message) => WriteAtomic(Path.Combine(auditDirectory, $"progress-{Session}-{++ProgressSequence:D4}.json"),
        new ResearchProgress(DateTimeOffset.Now, OneLine(message), model, ActiveLocalInferences, FileWorkers,
            FileJobRunning || LocalOperations > 0 || ActiveLocalInferences > 0));

    static void WriteAtomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

sealed record ResearchProgress(DateTimeOffset Timestamp, string Message, string? LocalModel = null,
    int? ActiveWorkers = null, int? WorkerLimit = null, bool? LocalWorkActive = null);
