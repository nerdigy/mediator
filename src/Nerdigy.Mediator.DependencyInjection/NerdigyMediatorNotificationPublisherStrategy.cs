namespace Nerdigy.Mediator.DependencyInjection;

/// <summary>
/// Represents built-in notification publisher strategies.
/// </summary>
public enum NerdigyMediatorNotificationPublisherStrategy
{
    /// <summary>
    /// Publishes notifications sequentially by awaiting each handler one-by-one.
    /// </summary>
    Sequential = 0,

    /// <summary>
    /// Publishes notifications concurrently using <see cref="Task.WhenAll(IEnumerable{Task})"/>.
    /// </summary>
    Parallel = 1
}
