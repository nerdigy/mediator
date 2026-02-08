namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Combines request sending and notification publishing capabilities.
/// </summary>
public interface IMediator : ISender, IPublisher;