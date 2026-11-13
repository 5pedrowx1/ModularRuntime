using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using ModularRuntime.Contracts;
using ModularRuntime.Sdk;

namespace ModularRuntime.Benchmarks;

/// <summary>
/// JSON payload creation and reading. "Previous" variants reproduce the v0.6 implementation
/// (string storage, validation through a <see cref="JsonDocument"/>) for a like-for-like comparison.
/// </summary>
[MemoryDiagnoser]
public class JsonPayloadBenchmarks
{
    private static readonly JsonDocumentOptions PreviousOptions = new() { MaxDepth = 64, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    private string _smallText = string.Empty;
    private string _largeText = string.Empty;
    private byte[] _largeUtf8 = [];
    private JsonPayload _large = JsonPayload.Null;
    private JsonPayload _small = JsonPayload.Null;
    private Sample _sample = new("cpu", 0.5, 1);

    public sealed record Sample(string Metric, double Value, long Sequence);

    [GlobalSetup]
    public void Setup()
    {
        _smallText = """{"metric":"cpu","value":0.5,"sequence":1}""";
        var builder = new StringBuilder("[");
        for (var index = 0; index < 2000; index++)
        {
            builder.Append(index == 0 ? "" : ",").Append("""{"metric":"cpu","value":0.5,"sequence":""").Append(index).Append('}');
        }

        _largeText = builder.Append(']').ToString(); // ~80 KiB, close to real telemetry batches
        _largeUtf8 = Encoding.UTF8.GetBytes(_largeText);
        _large = JsonPayload.Parse(_largeText);
        _small = JsonPayload.Parse(_smallText);
        _sample = new Sample("cpu", 0.5, 1);
    }

    [Benchmark(Baseline = true, Description = "Parse small (v0.6 algorithm)")]
    public int ParseSmallPrevious() => PreviousParse(_smallText);

    [Benchmark(Description = "Parse small (string)")]
    public int ParseSmall() => JsonPayload.Parse(_smallText).ByteCount;

    [Benchmark(Description = "Parse large 80 KiB (v0.6 algorithm)")]
    public int ParseLargePrevious() => PreviousParse(_largeText);

    [Benchmark(Description = "Parse large 80 KiB (string)")]
    public int ParseLarge() => JsonPayload.Parse(_largeText).ByteCount;

    [Benchmark(Description = "Parse large 80 KiB (UTF-8 bytes)")]
    public int ParseLargeUtf8() => JsonPayload.Parse(_largeUtf8).ByteCount;

    [Benchmark(Description = "GetString on large (v0.6: full document)")]
    public string? GetStringPrevious()
    {
        using var document = JsonDocument.Parse(_large.Json, PreviousOptions);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("metric", out var property) ? property.GetString() : null;
    }

    [Benchmark(Description = "GetString on large (forward reader)")]
    public string? GetString() => _large.GetString("metric");

    [Benchmark(Description = "SDK ToPayload<T> (v0.6: serialize to string + parse)")]
    public int ToPayloadPrevious() => JsonPayload.Parse(JsonSerializer.Serialize(_sample, PluginJson.DefaultOptions)).ByteCount;

    [Benchmark(Description = "SDK ToPayload<T>")]
    public int ToPayload() => PluginJson.ToPayload(_sample).ByteCount;

    [Benchmark(Description = "SDK FromPayload<T> (v0.6: deserialize string)")]
    public Sample? FromPayloadPrevious() => JsonSerializer.Deserialize<Sample>(_smallText, PluginJson.DefaultOptions);

    [Benchmark(Description = "SDK FromPayload<T> (deserialize UTF-8)")]
    public Sample? FromPayload() => PluginJson.FromPayload<Sample>(_small);

    private static int PreviousParse(string json)
    {
        using (JsonDocument.Parse(json, PreviousOptions))
        {
        }

        return Encoding.UTF8.GetByteCount(json);
    }
}
