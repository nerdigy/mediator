using BenchmarkDotNet.Running;

namespace Nerdigy.Mediator.Benchmarks;

/// <summary>
/// Runs benchmark suites for Nerdigy.Mediator.
/// </summary>
public static class Program
{
    /// <summary>
    /// Application entry point. Forwards command-line arguments, such as <c>--filter</c> and <c>--job</c>, to BenchmarkDotNet.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    public static void Main(string[] args)
    {
        _ = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}