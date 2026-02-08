using Nerdigy.Mediator.Abstractions;
using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies request send behavior for the mediator runtime.
/// </summary>
public sealed class MediatorSendTests
{
    /// <summary>
    /// Verifies that sending a request returns the response from its handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenHandlerRegistered_ReturnsResponse()
    {
        var handler = new PingRequestHandler();
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<PingRequest, string>), handler));
        var mediator = new MediatorRuntime(provider);

        var response = await mediator.Send(new PingRequest("hello"), CancellationToken.None);

        Assert.Equal("PONG: hello", response);
    }

    /// <summary>
    /// Verifies that sending a request without a registered handler throws.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenNoHandlerRegistered_ThrowsInvalidOperationException()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(new PingRequest("hello"), CancellationToken.None));

        Assert.Contains("No request handler is registered", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that void-style requests are dispatched through <see cref="IRequestHandler{TRequest}"/>.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task SendVoid_WhenHandlerRegistered_InvokesHandler()
    {
        var handler = new DeleteCommandHandler();
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<DeleteCommand>), handler));
        var mediator = new MediatorRuntime(provider);

        await mediator.Send((IRequest)new DeleteCommand("42"), CancellationToken.None);

        Assert.True(handler.WasCalled);
        Assert.Equal("42", handler.ReceivedId);
    }

    /// <summary>
    /// Verifies that sending a null request throws.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenRequestIsNull_ThrowsArgumentNullException()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => mediator.Send<string>(null!, CancellationToken.None));
    }

    /// <summary>
    /// Represents a sample request for send tests.
    /// </summary>
    /// <param name="Message">The request payload.</param>
    private sealed record PingRequest(string Message) : IRequest<string>;

    /// <summary>
    /// Handles <see cref="PingRequest"/> requests.
    /// </summary>
    private sealed class PingRequestHandler : IRequestHandler<PingRequest, string>
    {
        /// <summary>
        /// Handles the request and returns a derived response payload.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public Task<string> Handle(PingRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult($"PONG: {request.Message}");
        }
    }

    /// <summary>
    /// Represents a sample void-style request for send tests.
    /// </summary>
    /// <param name="Id">The entity identifier.</param>
    private sealed record DeleteCommand(string Id) : IRequest;

    /// <summary>
    /// Handles <see cref="DeleteCommand"/> requests.
    /// </summary>
    private sealed class DeleteCommandHandler : IRequestHandler<DeleteCommand>
    {
        /// <summary>
        /// Gets a value indicating whether the handler ran.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Gets the request identifier captured by the handler.
        /// </summary>
        public string? ReceivedId { get; private set; }

        /// <summary>
        /// Handles a delete command request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(DeleteCommand request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            ReceivedId = request.Id;

            return Task.CompletedTask;
        }
    }
}
