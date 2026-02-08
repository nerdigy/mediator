namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Represents mutable state for request exception handling.
/// </summary>
/// <typeparam name="TResponse">The response payload type.</typeparam>
public sealed class RequestExceptionHandlerState<TResponse>
{
    /// <summary>
    /// Gets a value indicating whether the exception has been handled.
    /// </summary>
    public bool Handled { get; private set; }

    /// <summary>
    /// Gets the response value set when the exception is marked as handled.
    /// </summary>
    public TResponse? Response { get; private set; }

    /// <summary>
    /// Marks the exception as handled and supplies a fallback response.
    /// </summary>
    /// <param name="response">The fallback response value.</param>
    public void SetHandled(TResponse response)
    {
        Handled = true;
        Response = response;
    }
}
