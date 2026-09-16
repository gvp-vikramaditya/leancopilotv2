sealed record CopilotRun(
    string Location,
    string Model,
    string WorkingDirectory,
    string Context,
    string Output,
    string[] Commands,
    TokenUsage Usage,
    TimeSpan Duration,
    int ExitCode);

sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheWrite, bool Measured = true);

sealed record ParsedEvents(string Response, string[] Commands);
