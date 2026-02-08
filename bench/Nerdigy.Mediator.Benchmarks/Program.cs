using BenchmarkDotNet.Running;

namespace Nerdigy.Mediator.Benchmarks;

/// <summary>
/// Runs benchmark suites for Nerdigy.Mediator.
/// </summary>
public static class Program
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    public static void Main(string[] args)
    {
        _ = args;

        _ = BenchmarkRunner.Run<MediatorBenchmarks>();
    }
}
