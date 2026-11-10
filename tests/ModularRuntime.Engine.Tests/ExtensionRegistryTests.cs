using ModularRuntime.Contracts;
using static ModularRuntime.Engine.Tests.PlatformTestHelpers;

namespace ModularRuntime.Engine.Tests;

public sealed class ExtensionRegistryTests
{
    private const string PointId = "test.exporters";

    private static ExtensionPointDefinition Point(string id = PointId, string version = "1.1", params string[] operations) =>
        new(id, $"Point {id}", Version.Parse(version)) { Operations = operations };

    private static ExtensionContributionDefinition Contribution(string id, string point = PointId, string version = "1.0") =>
        new(id, point, Version.Parse(version));

    private static PluginInvocationHandler Returns(string value) =>
        (_, _) => ValueTask.FromResult(PluginResponse.Success(JsonPayload.FromString(value)));

    [Fact]
    public async Task HostDeclaresPoint_PluginsContribute_HostDiscoversAndInvokes()
    {
        await using var engine = new PluginEngine();
        engine.Host.Extensions.DeclareExtensionPoint(Point(operations: "export"));
        await StartPluginAsync(engine, "com.test.txt", c => { c.Extensions.Contribute(Contribution("txt") with { DisplayName = "Text" }, Returns("txt")); return Task.CompletedTask; });
        await StartPluginAsync(engine, "com.test.json", c => { c.Extensions.Contribute(Contribution("json"), Returns("json")); return Task.CompletedTask; });

        var point = engine.Host.Extensions.GetExtensionPoint(PointId)!;
        var contributions = engine.Host.Extensions.GetContributions(PointId);
        var all = await engine.Host.Extensions.InvokeAllAsync(PointId, "export", Json("{}"));
        var single = await engine.Host.Extensions.InvokeAsync(PointId, "json", "export");

        Assert.True(point.Owner.IsHost);
        Assert.Equal(2, point.ContributionCount);
        Assert.Equal(["txt", "json"], contributions.Select(c => c.ContributionId));
        Assert.Equal("Text", contributions[0].DisplayName);
        Assert.Equal(PluginId.Parse("com.test.txt"), contributions[0].Owner.PluginId);
        Assert.True(all.IsSuccess);
        Assert.Equal(["\"txt\"", "\"json\""], all.Results.Select(r => r.Payload!.Json));
        Assert.Equal("\"json\"", single.Payload!.Json);
        Assert.Equal($"{PointId}/json", single.TargetId);
    }

    [Fact]
    public async Task PluginDeclaresPoint_OtherPluginAndHostContribute()
    {
        await using var engine = new PluginEngine();
        await StartPluginAsync(engine, "com.test.point-owner", c => { c.Extensions.DeclareExtensionPoint(Point()); return Task.CompletedTask; });
        await StartPluginAsync(engine, "com.test.contributor", c => { c.Extensions.Contribute(Contribution("plugin"), Returns("p")); return Task.CompletedTask; });
        engine.Host.Extensions.Contribute(Contribution("host"), Returns("h"));

        var point = engine.Host.Extensions.GetExtensionPoint(PointId)!;
        var result = await engine.Host.Extensions.InvokeAllAsync(PointId, "anything");

        Assert.Equal(PluginId.Parse("com.test.point-owner"), point.Owner.PluginId);
        Assert.Equal(2, result.Results.Count);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task MissingPoint_IsReportedOnContributeAndInvoke()
    {
        await using var engine = new PluginEngine();

        AssertRuntimeError(PluginErrorCodes.ExtensionPointNotFound, () => engine.Host.Extensions.Contribute(Contribution("x"), Returns("x")));
        var single = await engine.Host.Extensions.InvokeAsync(PointId, "x", "export");
        var all = await engine.Host.Extensions.InvokeAllAsync(PointId, "export");

        Assert.Equal(PluginErrorCodes.ExtensionPointNotFound, single.ErrorCode);
        Assert.Equal(PluginOperationStatus.Rejected, all.Status);
        Assert.Equal(PluginErrorCodes.ExtensionPointNotFound, all.ErrorCode);
        Assert.Empty(engine.Host.Extensions.GetContributions(PointId));
        Assert.Null(engine.Host.Extensions.GetExtensionPoint(PointId));
    }

    [Fact]
    public async Task DuplicatePointsAndContributions_AreRejected()
    {
        await using var engine = new PluginEngine();
        engine.Host.Extensions.DeclareExtensionPoint(Point());
        engine.Host.Extensions.Contribute(Contribution("one"), Returns("1"));

        AssertRuntimeError(PluginErrorCodes.DuplicateExtensionPoint, () => engine.Host.Extensions.DeclareExtensionPoint(Point()));
        AssertRuntimeError(PluginErrorCodes.DuplicateContribution, () => engine.Host.Extensions.Contribute(Contribution("one"), Returns("2")));
        Assert.Single(engine.Host.Extensions.GetContributions(PointId));
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("1.2")]
    [InlineData("0.9")]
    public async Task IncompatibleContractVersion_IsRejected(string contributionVersion)
    {
        await using var engine = new PluginEngine();
        engine.Host.Extensions.DeclareExtensionPoint(Point(version: "1.1"));

        AssertRuntimeError(
            PluginErrorCodes.ExtensionVersionIncompatible,
            () => engine.Host.Extensions.Contribute(Contribution("x", version: contributionVersion), Returns("x")));
    }

    [Fact]
    public async Task UndefinedOperation_IsRejectedForRestrictedPoints()
    {
        await using var engine = new PluginEngine();
        engine.Host.Extensions.DeclareExtensionPoint(Point(operations: "export"));
        engine.Host.Extensions.Contribute(Contribution("one"), Returns("1"));

        var result = await engine.Host.Extensions.InvokeAllAsync(PointId, "import");

        Assert.Equal(PluginErrorCodes.OperationNotSupported, result.ErrorCode);
    }

    [Fact]
    public async Task FailingContribution_DoesNotPreventOthers()
    {
        await using var engine = new PluginEngine();
        engine.Host.Extensions.DeclareExtensionPoint(Point());
        engine.Host.Extensions.Contribute(Contribution("bad"), (_, _) => throw new InvalidOperationException("contribution boom"));
        engine.Host.Extensions.Contribute(Contribution("good"), Returns("ok"));

        var result = await engine.Host.Extensions.InvokeAllAsync(PointId, "run");

        Assert.Equal(PluginOperationStatus.Failed, result.Status);
        Assert.Equal(PluginErrorCodes.HandlerFailed, result.Results[0].ErrorCode);
        Assert.True(result.Results[1].IsSuccess);
    }

    [Fact]
    public async Task ExplicitRemoval_OfContributionAndPoint()
    {
        await using var engine = new PluginEngine();
        var point = engine.Host.Extensions.DeclareExtensionPoint(Point());
        var first = engine.Host.Extensions.Contribute(Contribution("first"), Returns("1"));
        var second = engine.Host.Extensions.Contribute(Contribution("second"), Returns("2"));

        await first.DisposeAsync();
        Assert.Equal(["second"], engine.Host.Extensions.GetContributions(PointId).Select(c => c.ContributionId));
        Assert.Equal(PluginErrorCodes.ContributionNotFound, (await engine.Host.Extensions.InvokeAsync(PointId, "first", "run")).ErrorCode);

        await point.DisposeAsync();
        Assert.False(point.IsActive);
        Assert.False(second.IsActive);
        Assert.Null(engine.Host.Extensions.GetExtensionPoint(PointId));
        Assert.Empty(engine.Host.Resources.GetResources());
    }

    [Fact]
    public async Task ContributorStopping_RemovesOnlyItsContribution()
    {
        await using var engine = new PluginEngine();
        engine.Host.Extensions.DeclareExtensionPoint(Point());
        await StartPluginAsync(engine, "com.test.leaving", c => { c.Extensions.Contribute(Contribution("leaving"), Returns("l")); return Task.CompletedTask; });
        await StartPluginAsync(engine, "com.test.staying", c => { c.Extensions.Contribute(Contribution("staying"), Returns("s")); return Task.CompletedTask; });

        await engine.StopAsync(PluginId.Parse("com.test.leaving"));
        var result = await engine.Host.Extensions.InvokeAllAsync(PointId, "run");

        Assert.Equal(["staying"], engine.Host.Extensions.GetContributions(PointId).Select(c => c.ContributionId));
        Assert.True(result.IsSuccess);
        Assert.Single(result.Results);
        Assert.NotNull(engine.Host.Extensions.GetExtensionPoint(PointId));
    }

    [Fact]
    public async Task OwnerStopping_InvalidatesThePointAndAllContributions()
    {
        await using var engine = new PluginEngine();
        await StartPluginAsync(engine, "com.test.owner", c => { c.Extensions.DeclareExtensionPoint(Point()); return Task.CompletedTask; });
        IExtensionContribution? pluginContribution = null;
        await StartPluginAsync(engine, "com.test.contributor", c => { pluginContribution = c.Extensions.Contribute(Contribution("plugin"), Returns("p")); return Task.CompletedTask; });
        var hostContribution = engine.Host.Extensions.Contribute(Contribution("host"), Returns("h"));

        await engine.StopAsync(PluginId.Parse("com.test.owner"));

        Assert.Null(engine.Host.Extensions.GetExtensionPoint(PointId));
        Assert.False(pluginContribution!.IsActive);
        Assert.False(hostContribution.IsActive);
        Assert.Empty(engine.Host.Resources.GetResources());
        Assert.Empty(engine.GetResources(PluginId.Parse("com.test.contributor")));
        Assert.Equal(PluginErrorCodes.ExtensionPointNotFound, (await engine.Host.Extensions.InvokeAllAsync(PointId, "run")).ErrorCode);
    }

    [Fact]
    public async Task ConcurrentContributions_AreAllRegisteredOnce()
    {
        await using var engine = new PluginEngine();
        engine.Host.Extensions.DeclareExtensionPoint(Point());

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            try
            {
                engine.Host.Extensions.Contribute(Contribution($"c{i % 32}"), Returns("x"));
                return true;
            }
            catch (PluginRuntimeException exception) when (exception.ErrorCode == PluginErrorCodes.DuplicateContribution)
            {
                return false;
            }
        }))).WaitAsync(TestTimeout);

        Assert.Equal(32, outcomes.Count(success => success));
        Assert.Equal(32, engine.Host.Extensions.GetContributions(PointId).Count);
        Assert.Equal(32, engine.Host.Extensions.GetExtensionPoint(PointId)!.ContributionCount);
    }

    [Fact]
    public async Task Metadata_IsAnImmutableSnapshot()
    {
        await using var engine = new PluginEngine();
        var source = new Dictionary<string, string> { ["format"] = "txt" };
        engine.Host.Extensions.DeclareExtensionPoint(Point() with { Metadata = source });
        engine.Host.Extensions.Contribute(Contribution("one") with { Metadata = source }, Returns("1"));

        source["format"] = "changed";
        source["extra"] = "added";
        var point = engine.Host.Extensions.GetExtensionPoint(PointId)!;
        var contribution = Assert.Single(engine.Host.Extensions.GetContributions(PointId));

        Assert.Equal("txt", point.Metadata["format"]);
        Assert.False(point.Metadata.ContainsKey("extra"));
        Assert.Equal("txt", contribution.Metadata["format"]);
        var asDictionary = Assert.IsAssignableFrom<IDictionary<string, string>>(point.Metadata);
        Assert.True(asDictionary.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asDictionary["format"] = "x");
    }

    [Fact]
    public async Task TwoRuntimes_DoNotShareExtensions()
    {
        await using var first = new PluginEngine();
        await using var second = new PluginEngine();
        first.Host.Extensions.DeclareExtensionPoint(Point());

        Assert.Empty(second.Host.Extensions.GetExtensionPoints());
        AssertRuntimeError(PluginErrorCodes.ExtensionPointNotFound, () => second.Host.Extensions.Contribute(Contribution("x"), Returns("x")));
        second.Host.Extensions.DeclareExtensionPoint(Point());
    }
}
