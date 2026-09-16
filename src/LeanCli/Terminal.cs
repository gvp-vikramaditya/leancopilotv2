static class Terminal
{
    static readonly bool Interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
    static readonly List<string> History = [];
    static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    static string Status = "";
    static string? LocalActivity;
    static string? RemoteActivity;

    public static void Initialize()
    {
        if (!Interactive)
            return;

        Console.Write("\x1b[?1049h\x1b[2J\x1b[H");
        Render("", "");
    }

    public static void WriteLine(string text)
    {
        if (!Interactive)
        {
            Console.WriteLine(text);
            return;
        }

        History.AddRange(text.Replace("\r", "").Split('\n'));
        if (History.Count > 2000)
            History.RemoveRange(0, History.Count - 2000);
        Render("", "");
    }

    public static void SetStatus(string status)
    {
        Status = status;
        if (Interactive)
            Render("", "");
    }

    public static void SetActivity(bool local, string model, TimeSpan elapsed)
    {
        var frame = Spinner[(int)(elapsed.TotalMilliseconds / 125) % Spinner.Length];
        var activity = $"{frame} {(local ? "Local" : "Remote")} {model} · {elapsed.TotalSeconds:F1}s";
        if (local)
            LocalActivity = activity;
        else
            RemoteActivity = activity;

        if (Interactive)
            Render("", "");
    }

    public static void SetTaskActivity(string? local, string? coordinator, TimeSpan elapsed)
    {
        var frame = Spinner[(int)(elapsed.TotalMilliseconds / 125) % Spinner.Length];
        LocalActivity = local is null ? null : $"{frame} Local: {local}";
        RemoteActivity = coordinator is null ? null :
            $"{(local is null ? frame + " " : "")}Remote: {coordinator}";
        if (Interactive)
            Render("", "");
    }

    public static void ClearActivities()
    {
        LocalActivity = null;
        RemoteActivity = null;
        if (Interactive)
            Render("", "");
    }

    public static void ClearActivity(bool local)
    {
        if (local)
            LocalActivity = null;
        else
            RemoteActivity = null;

        if (Interactive)
            Render("", "");
    }

    public static string? ReadInput(string prompt)
    {
        if (!Interactive)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"\n{prompt}");
            Console.ResetColor();
            return Console.ReadLine();
        }

        var input = "";
        while (true)
        {
            Render(prompt, input);
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                return input;
            if (key.Key == ConsoleKey.Escape)
            {
                input = "";
                continue;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0)
                    input = input[..^1];
                continue;
            }
            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                return null;
            if (!char.IsControl(key.KeyChar))
                input += key.KeyChar;
        }
    }

    public static void Restore()
    {
        if (Interactive)
            Console.Write("\x1b[?1049l");
    }

    static void Render(string prompt, string input)
    {
        var width = Math.Max(10, Console.WindowWidth - 1);
        var transcriptHeight = Math.Max(1, Console.WindowHeight - 5);
        var wrapped = History.SelectMany(line => Wrap(line, width)).TakeLast(transcriptHeight).ToArray();
        var topRow = transcriptHeight + 1;
        var inputRow = topRow + 1;
        var bottomRow = inputRow + 1;
        var activityRow = bottomRow + 1;
        var statusRow = activityRow + 1;

        Console.Write("\x1b[?25l");
        for (var i = 0; i < transcriptHeight; i++)
        {
            var line = i < wrapped.Length ? wrapped[i] : "";
            Console.Write($"\x1b[{i + 1};1H{line.PadRight(width)}");
        }

        Console.Write($"\x1b[{topRow};1H┌{new string('─', width - 2)}┐");
        var value = $"{prompt}{input}";
        if (value.Length > width - 4)
            value = value[^Math.Max(1, width - 4)..];
        Console.Write($"\x1b[{inputRow};1H│ {value.PadRight(width - 4)} │");
        Console.Write($"\x1b[{bottomRow};1H└{new string('─', width - 2)}┘");
        var activity = string.Join(" | ", new[] { LocalActivity, RemoteActivity }
            .Where(value => value is not null));
        var active = activity.Length >= width ? activity[..(width - 1)] : activity;
        Console.Write($"\x1b[{activityRow};1H\x1b[2m{active.PadRight(width)}\x1b[0m");
        var status = Status.Length >= width ? Status[..(width - 1)] : Status;
        Console.Write($"\x1b[{statusRow};1H\x1b[2m{status.PadRight(width)}\x1b[0m");
        Console.Write($"\x1b[{inputRow};{Math.Min(width - 2, value.Length + 3)}H\x1b[?25h");
    }

    static IEnumerable<string> Wrap(string line, int width)
    {
        if (line.Length == 0)
            return [""];

        return Enumerable.Range(0, (line.Length + width - 1) / width)
            .Select(index => line.Substring(index * width, Math.Min(width, line.Length - index * width)));
    }
}
