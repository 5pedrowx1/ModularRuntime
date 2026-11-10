namespace ModularRuntime.Submissions;

/// <summary>How serious a finding about a submitted file is (shared by prepare-submission and the official validator).</summary>
public enum FindingSeverity
{
    /// <summary>Informational.</summary>
    Info,

    /// <summary>Needs a reviewer's attention; not a rejection by itself.</summary>
    Review,

    /// <summary>The submission cannot proceed until it is fixed.</summary>
    Error,
}

public sealed record SubmissionFinding(string Code, FindingSeverity Severity, string? Path, string Message);
