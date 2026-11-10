using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace ModularRuntime.Submissions;

/// <summary>
/// Classifies submitted files without executing anything: files that must never be submitted
/// (keys, credentials) and files that require attention from a reviewer (binaries, build scripts,
/// MSBuild customizations, code patterns such as P/Invoke or process execution).
/// </summary>
/// <remarks>
/// Used by <c>dotnet modularruntime prepare-submission</c> to leave secrets out, and by the official
/// submission validator to report them. Pattern matches are hints for the review: they neither
/// prove nor rule out malicious behavior.
/// </remarks>
public static partial class SourceClassifier
{
    public const long MaxScannedTextBytes = 4 * 1024 * 1024;

    private static readonly string[] SecretExtensions = [".pfx", ".p12", ".pem", ".key", ".snk", ".jks", ".keystore", ".publishsettings", ".kdbx", ".ppk"];

    private static readonly string[] SecretFileNames = ["secrets.json", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "credentials", ".npmrc", ".pypirc", "nuget.config.user", ".git-credentials", ".netrc"];

    private static readonly string[] IdeFileExtensions = [".user", ".suo", ".cache", ".swp"];

    private static readonly string[] BinaryExtensions = [".dll", ".exe", ".so", ".dylib", ".nupkg", ".snupkg", ".zip", ".7z", ".tar", ".gz", ".jar", ".msi", ".bin", ".obj", ".lib", ".a", ".winmd"];

    private static readonly string[] ScriptExtensions = [".ps1", ".psm1", ".psd1", ".sh", ".bash", ".cmd", ".bat", ".py", ".js", ".mjs", ".vbs", ".rb", ".pl"];

    private static readonly string[] MsBuildExtensions = [".props", ".targets", ".tasks"];

    /// <summary>Why a file must be left out of a submission (a secret or private state), or <see langword="null"/>.</summary>
    public static string? ExclusionReason(string relativePath, string? text)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var name = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(name);
        if (SecretExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) || name.EndsWith(".pubxml.user", StringComparison.OrdinalIgnoreCase))
        {
            return "key, certificate, or publishing credential file";
        }

        if (SecretFileNames.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase))
        {
            return "secret or credential file";
        }

        if (IdeFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return "IDE or user state";
        }

        if (text is not null)
        {
            if (PrivateKeyPattern().IsMatch(text))
            {
                return "contains a private key";
            }

            if (name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase) && text.Contains("packageSourceCredentials", StringComparison.OrdinalIgnoreCase))
            {
                return "NuGet configuration with credentials";
            }
        }

        return null;
    }

    /// <summary>Review findings for one file (severity <see cref="FindingSeverity.Review"/> unless noted).</summary>
    public static IEnumerable<SubmissionFinding> Classify(string relativePath, byte[]? content, string? text)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var name = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(name);

        if (BinaryExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            if (content is not null && IsNativePortableExecutable(content))
            {
                yield return Review("native_binary", relativePath, "A native (unmanaged) binary is part of the source. Native code is outside the .NET review: its origin, purpose, and build must be documented, and it must be declared.");
            }
            else
            {
                yield return Review("binary_in_source", relativePath, "A prebuilt binary is part of the source. The team builds from source only: binaries are not published and must be justified or rebuilt from source.");
            }
        }

        if (ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            yield return Review("build_script", relativePath, "A script is part of the source. Check whether the build invokes it and what it does.");
        }

        if (MsBuildExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) || name.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase))
        {
            yield return Review("msbuild_customization", relativePath, "MSBuild logic outside the project file: it runs during restore and build. Review every target and task.");
        }

        if (name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase))
        {
            yield return Review("nuget_config", relativePath, "A NuGet configuration is part of the source. The controlled build ignores it and restores only from the team's allowed sources; extra package sources must be justified.");
        }

        if (name.Equals("global.json", StringComparison.OrdinalIgnoreCase))
        {
            yield return Info("sdk_pinned", relativePath, "global.json pins the .NET SDK; the controlled build records the SDK it actually used.");
        }

        if (text is null)
        {
            yield break;
        }

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) || MsBuildExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            if (MsBuildExecutionPattern().IsMatch(text))
            {
                yield return Review("msbuild_execution", relativePath, "The project defines targets, tasks, Exec steps, or imports that run code during restore or build. Review them before any build.");
            }

            if (AnalyzerPattern().IsMatch(text))
            {
                yield return Review("analyzer_or_generator", relativePath, "An analyzer or source generator is referenced: it runs inside the compiler during the build.");
            }

            if (text.Contains("AllowUnsafeBlocks", StringComparison.OrdinalIgnoreCase))
            {
                yield return Review("unsafe_code", relativePath, "Unsafe code is enabled.");
            }
        }

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (code, pattern, message) in CodeHints)
            {
                if (pattern.IsMatch(text))
                {
                    yield return Review(code, relativePath, message);
                }
            }
        }

        if (PossibleTokenPattern().IsMatch(text))
        {
            yield return Review("possible_secret", relativePath, "Text that looks like an access token or password. If it is a real secret, treat it as compromised and rotate it.");
        }
    }

    public static bool LooksLikeText(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var length = Math.Min(content.Length, 8000);
        for (var i = 0; i < length; i++)
        {
            if (content[i] == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The first bytes of a large file: enough for the PE headers, without reading the whole file.</summary>
    public static byte[] ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[(int)Math.Min(stream.Length, 64 * 1024)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static bool IsNativePortableExecutable(byte[] content)
    {
        try
        {
            using var reader = new PEReader(new MemoryStream(content, writable: false));
            return reader.PEHeaders.CorHeader is null;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static SubmissionFinding Review(string code, string path, string message) => new(code, FindingSeverity.Review, path, message);

    private static SubmissionFinding Info(string code, string path, string message) => new(code, FindingSeverity.Info, path, message);

    private static readonly (string Code, Regex Pattern, string Message)[] CodeHints =
    [
        ("pinvoke", PInvokePattern(), "P/Invoke or function pointers into native code."),
        ("process_execution", ProcessPattern(), "Starts processes."),
        ("network_access", NetworkPattern(), "Uses network APIs."),
        ("dynamic_loading", DynamicLoadingPattern(), "Loads assemblies or types dynamically (reflection, load contexts)."),
        ("system_changes", SystemChangesPattern(), "Touches the registry, services, scheduled tasks, or other system-wide state (possible persistence)."),
        ("file_system_access", FileSystemPattern(), "Accesses files or folders outside its own data (special folders, absolute paths)."),
        ("telemetry", TelemetryPattern(), "Mentions telemetry or analytics."),
    ];

    [GeneratedRegex(@"-----BEGIN ([A-Z]+ )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"<\s*(Target|UsingTask|Exec|Import)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MsBuildExecutionPattern();

    [GeneratedRegex(@"OutputItemType\s*=\s*""Analyzer""|<\s*Analyzer\s|Analyzers?\b[^""]*""\s*Version|SourceGenerator", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AnalyzerPattern();

    [GeneratedRegex(@"\b(DllImport|LibraryImport)\b|delegate\s+unmanaged|NativeLibrary\.", RegexOptions.CultureInvariant)]
    private static partial Regex PInvokePattern();

    [GeneratedRegex(@"\bProcess\.Start\b|\bProcessStartInfo\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessPattern();

    [GeneratedRegex(@"\b(HttpClient|WebClient|WebRequest|Socket|TcpClient|UdpClient|ClientWebSocket|Dns\.)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NetworkPattern();

    [GeneratedRegex(@"\b(Assembly\.(Load|LoadFrom|LoadFile|UnsafeLoadFrom)|AssemblyLoadContext|Activator\.CreateInstance|Type\.GetType|InvokeMember)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DynamicLoadingPattern();

    [GeneratedRegex(@"\b(Microsoft\.Win32\.Registry|RegistryKey|ServiceController|schtasks|TaskScheduler|Environment\.SetEnvironmentVariable)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SystemChangesPattern();

    [GeneratedRegex(@"\bEnvironment\.(GetFolderPath|SpecialFolder)\b|@""[A-Za-z]:\\|""[A-Za-z]:\\\\", RegexOptions.CultureInvariant)]
    private static partial Regex FileSystemPattern();

    [GeneratedRegex(@"\b(telemetry|analytics|ApplicationInsights)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TelemetryPattern();

    [GeneratedRegex(@"(ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{40,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,}|(password|passwd|secret|api[_-]?key)""?\s*[=:]\s*""[^""\s]{8,}"")", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PossibleTokenPattern();
}
