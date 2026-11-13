using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModularRuntime.Contracts;

namespace ModularRuntime.Engine.Tests;

public sealed class JsonPayloadTests
{
    [Fact]
    public void Utf8AndText_AreEquivalentRepresentations()
    {
        var fromText = JsonPayload.Parse("""{"text":"olá","n":1}""");
        var fromBytes = JsonPayload.Parse("""{"text":"olá","n":1}"""u8);

        Assert.Equal(fromText, fromBytes);
        Assert.Equal(fromText.GetHashCode(), fromBytes.GetHashCode());
        Assert.Equal(fromText.Json, fromBytes.Json);
        Assert.Equal(Encoding.UTF8.GetByteCount(fromText.Json), fromText.ByteCount);
        Assert.Equal(fromText.ByteCount, fromBytes.Utf8Json.Length);
        Assert.Equal("olá", fromBytes.GetString("text"));
        Assert.Null(fromBytes.GetString("n"));
        Assert.Null(fromBytes.GetString("missing"));
        Assert.Null(JsonPayload.Parse("[1]").GetString("x"));
        Assert.Equal(1, fromBytes.ToDocument().RootElement.GetProperty("n").GetInt32());
        Assert.Equal("olá", fromBytes.ToNode()!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ParsedBytes_AreCopied_SoTheCallerCannotChangeThePayload()
    {
        var buffer = "\"a\""u8.ToArray();
        var payload = JsonPayload.Parse(buffer);
        buffer[1] = (byte)'b';

        Assert.Equal("\"a\"", payload.Json);
    }

    [Fact]
    public void Validation_RejectsInvalidText_TrailingContent_Depth_AndEmptyInput()
    {
        Assert.False(JsonPayload.TryParse("", out _, out var empty));
        Assert.StartsWith("Invalid JSON payload", empty, StringComparison.Ordinal);
        Assert.False(JsonPayload.TryParse("{} {}", out _, out _));
        Assert.False(JsonPayload.TryParse("{}x", out _, out _));
        Assert.False(JsonPayload.TryParse("  ", out _, out _));
        Assert.False(JsonPayload.TryParse("{\"a\":1,}", out _, out _));
        Assert.False(JsonPayload.TryParse("/* c */ 1", out _, out _));
        Assert.False(JsonPayload.TryParse("﻿{}", out _, out _));
        Assert.False(JsonPayload.TryParse((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}'], out _, out _));
        Assert.True(JsonPayload.TryParse(new string('[', JsonPayload.MaxDepth) + new string(']', JsonPayload.MaxDepth), out _, out _));
        Assert.False(JsonPayload.TryParse(new string('[', JsonPayload.MaxDepth + 1) + new string(']', JsonPayload.MaxDepth + 1), out _, out _));
        Assert.True(JsonPayload.TryParse(" 1 ", out var number, out _));
        Assert.Equal(" 1 ", number!.Json);
        Assert.Throws<FormatException>(() => JsonPayload.Parse("nope"u8));
        Assert.Throws<ArgumentNullException>(() => JsonPayload.Parse((string)null!));
    }

    [Fact]
    public void WriteTo_EmbedsTheRawValue_AndFromNode_RoundTrips()
    {
        var payload = JsonPayload.FromNode(new JsonObject { ["x"] = 1 });
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("inner");
            payload.WriteTo(writer);
            writer.WriteEndObject();
        }

        Assert.Equal("""{"inner":{"x":1}}""", Encoding.UTF8.GetString(buffer.WrittenSpan));
        Assert.Equal("q\"", JsonPayload.FromString("q\"").ToNode()!.GetValue<string>());
        Assert.Equal("\"plain\"", JsonPayload.FromString("plain").Json);
        Assert.Same(JsonPayload.Null, JsonPayload.FromNode(null));
        Assert.Equal("null", JsonPayload.Null.ToString());
        Assert.Equal("{}", JsonPayload.EmptyObject.Json);
    }
}
