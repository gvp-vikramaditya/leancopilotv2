using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;

// A request-scoped snapshot. Only selected ranges go to Ollama; no model text is executed.
sealed class LocalRepository(string root)
{
    const int MaxEntries = 2000;
    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "build", "venv", "__pycache__", "secrets.json"
    };
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".fs", ".fsproj",
        ".py", ".js", ".jsx", ".ts", ".tsx", ".json", ".md", ".txt", ".yaml", ".yml",
        ".toml", ".xml", ".go", ".mod", ".rs", ".java", ".c", ".h", ".cpp", ".hpp", ".sh", ".ps1"
    };
    readonly string Root = Path.GetFullPath(root);
    readonly Dictionary<string, string[]> Snapshots = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> Fingerprints = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> DiscoveryGaps = [];
    string[]? Paths;
    int SnapshotChars;
    public int DiskReads { get; private set; }
    public int CacheHits { get; private set; }
    public int ExcludedEntries { get; private set; }
    public IReadOnlyList<string> Gaps => DiscoveryGaps;

    public string Resolve(string path)
    {
        var full = Path.GetFullPath(path, Root);
        var relative = Path.GetRelativePath(Root, full);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.Contains(':'))
            throw new ArgumentException("Path must stay inside the repository.");
        var current = Root;
        CheckPath(current);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part == ".")
                continue;
            if (part.StartsWith('.') || Excluded.Contains(part))
                throw new ArgumentException("Hidden/generated/private paths are excluded.");
            current = Path.Combine(current, part);
            CheckPath(current);
        }
        return full;
    }

    static void CheckPath(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Linked paths are excluded.");
    }

    public string[] Discover()
    {
        if (Paths is not null)
            return Paths;
        var paths = new List<string>();
        var timer = Stopwatch.StartNew();
        var entries = 0;
        void Walk(string directory, int depth)
        {
            if (depth > 32 || entries >= MaxEntries || timer.Elapsed.TotalSeconds > 5)
            {
                DiscoveryGaps.Add("discovery limit reached; inventory is incomplete");
                return;
            }
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(Resolve(directory)))
                {
                    if (++entries > MaxEntries || timer.Elapsed.TotalSeconds > 5)
                    {
                        DiscoveryGaps.Add("discovery limit reached; inventory is incomplete");
                        break;
                    }
                    var name = Path.GetFileName(path);
                    if (name.StartsWith('.') || Excluded.Contains(name) ||
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        ExcludedEntries++;
                        continue;
                    }
                    if (Directory.Exists(path))
                        Walk(path, depth + 1);
                    else if (Extensions.Contains(Path.GetExtension(path)) && !name.Any(char.IsControl))
                        paths.Add(Path.GetRelativePath(Root, path));
                    else
                        ExcludedEntries++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                DiscoveryGaps.Add($"discovery failed for {Path.GetRelativePath(Root, directory)}: {ex.Message}");
                Logger.Error($"Local discovery: {ex.Message}");
            }
        }
        Walk(Root, 0);
        Paths = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return Paths;
    }

    public string CanonicalPath(string path)
    {
        var full = Resolve(path);
        var relative = Path.GetRelativePath(Root, full);
        return Discover().FirstOrDefault(p => p.Equals(relative, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Path was not discovered as supported text; use overview or a filename query first.");
    }

    public string[] SelectFiles(string objective) => Discover()
        .OrderBy(p => objective.Contains(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(Priority).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    public LocalEvidence FileEvidence(string path, bool overview = false)
    {
        path = CanonicalPath(path);
        var lines = ReadSnapshot(path);
        var ranges = new List<EvidenceRange>();
        var gaps = new List<string>();
        var offset = 0;
        var inputLimit = overview ? 3000 : 4000;
        // Bound by characters, including line labels, rather than guessing language boundaries.
        while (offset < lines.Length)
        {
            var start = offset;
            var chars = 0;
            while (offset < lines.Length && (!overview || offset < 60) &&
                chars + lines[offset].Length + 16 <= inputLimit)
                chars += lines[offset++].Length + 16;
            if (start == offset)
            {
                gaps.Add($"line {offset + 1} exceeds {inputLimit}-character local input bound; remaining source not selected for analysis");
                break;
            }
            ranges.Add(new(path, start + 1, offset, lines.Length, lines[start..offset]));
            if (overview) break;
        }
        if (overview)
            gaps.Add($"overview only: first {offset}/{lines.Length} lines selected; no exhaustive source or method review; use focused deep-dive for details");
        if (!overview && offset < lines.Length)
            gaps.Add($"captured {offset}/{lines.Length} lines; remaining source cannot fit a bounded local input");
        if (ranges.Count > 1)
            gaps.Add("character-bounded chunks, not parsed methods; split definitions and method completeness remain unknown");
        if (lines.Length == 0)
            gaps.Add("empty file; no purpose or methods inferred");
        return new(ranges.ToArray(), gaps.ToArray(), 1, [], lines.Length);
    }

    public string Fingerprint(string path)
    {
        path = CanonicalPath(path);
        ReadSnapshot(path);
        return Fingerprints[path];
    }

    public string[] Changes(IReadOnlyDictionary<string, string> expected, string[]? inventory = null)
    {
        var current = new LocalRepository(Root);
        var changes = new List<string>();
        if (inventory is not null && !current.Discover().ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(inventory))
            changes.Add("eligible file inventory changed");
        foreach (var item in expected)
        {
            try
            {
                if (current.Fingerprint(item.Key) != item.Value)
                    changes.Add(item.Key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                changes.Add($"{item.Key}: {ex.Message}");
                Logger.Error($"Snapshot verification: {item.Key}: {ex.Message}");
            }
        }
        changes.AddRange(current.Gaps);
        return changes.ToArray();
    }

    string[] ReadSnapshot(string path)
    {
        // Check confinement even for cached files, in case a directory was replaced by a link.
        var full = Resolve(path);
        if (Snapshots.TryGetValue(path, out var cached))
        {
            CacheHits++;
            return cached;
        }
        if (new FileInfo(full).Length > 1_000_000)
            throw new ArgumentException("Only text files up to 1 MB may be read.");
        using var reader = new StreamReader(full, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[1_000_001];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > 1_000_000 || buffer.AsSpan(0, count).Contains('\0'))
            throw new ArgumentException("File exceeds text bounds or contains binary content.");
        if (SnapshotChars + count > 8_000_000)
            throw new ArgumentException("Request snapshot limit reached (8 million characters).");
        var source = new string(buffer, 0, count);
        var text = source.ReplaceLineEndings("\n");
        var lines = text.Length == 0 ? [] : (text.EndsWith('\n') ? text[..^1] : text).Split('\n');
        Snapshots.Add(path, lines);
        Fingerprints.Add(path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        SnapshotChars += count;
        DiskReads++;
        return lines;
    }

    public EvidenceRange Range(string path, int start, int end, int maxLines = 80, int maxChars = 8000)
    {
        if (start < 1 || end < start || end > 1_000_000 || end - start + 1 > maxLines)
            throw new ArgumentException($"Range must be 1-based, within 1..1000000 and at most {maxLines} lines.");
        path = CanonicalPath(path);
        var lines = ReadSnapshot(path);
        if (start > lines.Length)
            throw new ArgumentException($"Start is past EOF ({lines.Length} lines).");
        end = Math.Min(end, lines.Length);
        var selected = lines[(start - 1)..end];
        if (selected.Sum(line => line.Length + 16) > maxChars)
            throw new ArgumentException($"Excerpt exceeds {maxChars} characters; request a smaller range.");
        return new(path, start, end, lines.Length, selected);
    }

    public LocalEvidence Gather(string intent, string query, string? path)
    {
        var all = Discover();
        var gaps = new List<string>(Gaps);
        var ranges = new List<EvidenceRange>();
        var searched = new List<string>();
        string[] candidates;
        string? target = path is null ? null : CanonicalPath(path);
        if (intent == "overview")
        {
            var ordered = all.OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            candidates = ordered.Where(p => Priority(p) == 0).Take(2)
                .Concat(ordered.Where(p => Priority(p) == 1).Take(2))
                .Concat(ordered.Where(p => Priority(p) == 2).Take(1))
                .Concat(ordered.Where(p => Priority(p) == 3))
                .Concat(ordered).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        }
        else
        {
            candidates = all.OrderBy(p => p == target ? 0 :
                query.Contains(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(Priority).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
        }
        var tokens = System.Text.RegularExpressions.Regex.Matches(query, @"[A-Za-z_][A-Za-z_0-9.]{2,}")
            .Select(m => m.Value).Where(t => !new[] { "the", "this", "and", "for", "inspect", "trace", "callers" }.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        var symbol = tokens.FirstOrDefault(t => char.IsUpper(t[0]) && !t.Contains('.') &&
            !new[] { "Inspect", "Trace", "Map", "Return", "Find", "Explain", "Describe", "Show" }.Contains(t));
        foreach (var candidate in candidates)
        {
            try
            {
                var lines = ReadSnapshot(candidate);
                if (lines.Length == 0)
                {
                    gaps.Add($"{candidate}: empty file");
                    continue;
                }
                var start = 1;
                if (intent == "deep-dive")
                {
                    searched.Add(candidate);
                    var hit = -1;
                    var bestScore = 0;
                    for (var i = 0; i < Math.Min(10000, lines.Length); i++)
                    {
                        var score = symbol is not null ? (lines[i].Contains(symbol, StringComparison.Ordinal) ? 1 : 0) :
                            lines[i].Contains(query, StringComparison.OrdinalIgnoreCase) ? 1000 :
                                tokens.Sum(token => lines[i].Contains(token, StringComparison.Ordinal) ? 2 :
                                    lines[i].Contains(token, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                        if (score <= bestScore)
                            continue;
                        bestScore = score;
                        hit = i;
                    }
                    if (hit < 0 && candidate != target && !query.Contains(Path.GetFileName(candidate), StringComparison.OrdinalIgnoreCase))
                        continue;
                    start = Math.Max(1, hit + 1 - 8);
                }
                // Equal per-file allowance prevents the first large file exhausting the shared budget.
                var end = Math.Min(lines.Length, start + 29);
                while (end >= start && lines[(start - 1)..end].Sum(line => line.Length + 16) > 3000)
                    end--;
                if (end < start)
                {
                    gaps.Add($"{candidate}:{start}: line exceeds per-file character budget");
                    continue;
                }
                ranges.Add(new(candidate, start, end, lines.Length, lines[(start - 1)..end]));
                if (ranges.Count == 8)
                    break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                gaps.Add($"{candidate}: {ex.Message}");
                Logger.Error($"Local gathering: {candidate}: {ex.Message}");
            }
        }
        if (ranges.Count < all.Length || ranges.Any(r => r.Start != 1 || r.End != r.TotalLines))
            gaps.Add("sampled ranges only; unread files/ranges and unobserved callers remain unknown");
        if (intent == "deep-dive")
            gaps.Add($"literal search only, first 10000 lines in {searched.Count}/{all.Length} discovered files; not a complete call graph");
        gaps.Add("hidden/generated/linked paths and unsupported file types excluded; no absence inferred");
        return new(ranges.ToArray(), gaps.Distinct().ToArray(), all.Length, searched.ToArray());
    }

    static int Priority(string path) =>
        Path.GetExtension(path).EndsWith("proj", StringComparison.OrdinalIgnoreCase) ||
        new[] { "package.json", "pyproject.toml", "Cargo.toml", "go.mod" }.Contains(Path.GetFileName(path)) ? 0 :
        new[] { "Program.cs", "main.py", "__main__.py", "main.go", "index.ts", "main.rs" }.Contains(Path.GetFileName(path)) ? 1 :
        Path.GetFileName(path).StartsWith("README", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
}

sealed record EvidenceRange(string Path, int Start, int End, int TotalLines, string[] Lines)
{
    public string Citation => $"{Path}:{Start}-{End}";
    public string Text => $"file: {Path}\ncitation: [{Citation}]\n" +
        string.Join("\n", Lines.Select((line, i) => $"L{Start + i}: {line}"));
}

sealed record LocalEvidence(EvidenceRange[] Ranges, string[] Gaps, int Discovered, string[] Searched,
    int? TotalLines = null)
{
    public string Text => string.Join("\n\n", Ranges.Select(r => r.Text));
}
