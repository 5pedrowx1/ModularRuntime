namespace ModularRuntime.Submissions;

/// <summary>
/// <c>submission.json</c>, written by <c>dotnet modularruntime prepare-submission</c>. Everything in it
/// is declared by the author: the validator checks it against the delivered files and plugin.json,
/// and nothing in it is trusted as evidence of safety.
/// </summary>
/// <remarks>Schema 2 (v0.6) adds license, source repository, source commit, and lock files.</remarks>
public sealed record SubmissionManifest
{
    public const int CurrentSchemaVersion = 2;

    public required int SchemaVersion { get; init; }

    public required string PluginId { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string ApiVersion { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    /// <summary>SPDX license expression (for example <c>MIT</c>) or <c>LicenseRef-…</c>.</summary>
    public string? License { get; init; }

    /// <summary>Path of the license text inside <c>source/</c>, when present.</summary>
    public string? LicenseFile { get; init; }

    /// <summary>https URL of the public repository of the source.</summary>
    public string? SourceRepository { get; init; }

    /// <summary>Commit (or immutable snapshot ID) the delivered source corresponds to.</summary>
    public string? SourceCommit { get; init; }

    /// <summary>The plugin project, relative to <c>source/</c>.</summary>
    public required string Project { get; init; }

    public IReadOnlyList<string> Solutions { get; init; } = [];

    /// <summary>NuGet lock files (<c>packages.lock.json</c>), relative to <c>source/</c>.</summary>
    public IReadOnlyList<string> LockFiles { get; init; } = [];

    public string? TargetFramework { get; init; }

    public IReadOnlyList<SubmissionDependency> Dependencies { get; init; } = [];

    public SubmissionBuildInfo? Build { get; init; }

    /// <summary>Optional author-built package(s), relative to the submission root (<c>artifacts/…</c>). Used only for comparison.</summary>
    public IReadOnlyList<string> Artifacts { get; init; } = [];

    public IReadOnlyList<SubmissionExclusion> Excluded { get; init; } = [];

    public string? CreatedAtUtc { get; init; }

    public string? Notice { get; init; }
}

public sealed record SubmissionDependency(string Kind, string Name, string? Version);

public sealed record SubmissionBuildInfo
{
    public string? Instructions { get; init; }

    public string? DotnetSdkVersion { get; init; }

    public string? DevToolsVersion { get; init; }

    public string? PluginApi { get; init; }
}

public sealed record SubmissionExclusion(string Path, string Reason);

/// <summary><c>source-integrity.json</c>: size and SHA-256 of every delivered file, as declared by the author.</summary>
public sealed record SourceIntegrityDocument
{
    public required int SchemaVersion { get; init; }

    public required string HashAlgorithm { get; init; }

    public required IReadOnlyList<SourceIntegrityFile> Files { get; init; }
}

public sealed record SourceIntegrityFile(string Path, long Size, string Sha256);
