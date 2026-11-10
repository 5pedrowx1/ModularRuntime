using ModularRuntime.DevTools;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C: cancel cooperatively (stops a running build, removes partial output).
    e.Cancel = true;
    cancellation.Cancel();
};

return await DevToolsApp.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
