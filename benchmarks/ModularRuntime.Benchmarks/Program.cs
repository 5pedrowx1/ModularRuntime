using BenchmarkDotNet.Running;

namespace ModularRuntime.Benchmarks;

public static class Program
{
    public static int Main(string[] args)
    {
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return summaries.Any(summary => summary.HasCriticalValidationErrors) ? 1 : 0;
    }
}
