namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Represents mutable state for stream request exception handling.
/// </summary>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
public sealed class StreamRequestExceptionHandlerState<TResponse>
{
    /// <summary>
    /// Gets a value indicating whether the exception has been handled.
    /// </summary>
    public bool Handled { get; private set; }

    /// <summary>
    /// Gets the replacement stream set when the exception is marked as handled.
    /// </summary>
    public IAsyncEnumerable<TResponse>? ResponseStream { get; private set; }

    /// <summary>
    /// Marks the exception as handled and supplies a replacement stream.
    /// </summary>
    /// <param name="responseStream">The replacement stream to return for the request.</param>
    public void SetHandled(IAsyncEnumerable<TResponse> responseStream)
    {
        ArgumentNullException.ThrowIfNull(responseStream);
        Handled = true;
        ResponseStream = responseStream;
    }
}