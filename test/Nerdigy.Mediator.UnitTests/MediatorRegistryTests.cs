using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies the rules <see cref="MediatorRegistry"/> applies when registering request types.
/// </summary>
/// <remarks>
/// Registrations are process-wide and permanent, so every test uses request types no other test uses.
/// </remarks>
public sealed class MediatorRegistryTests
{
    /// <summary>
    /// Verifies a request that implements both <see cref="IRequest"/> and <see cref="IRequest{TResponse}"/> registers for
    /// its non-<see cref="Unit"/> response and reaches that handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task RegisterRequest_WhenRequestAlsoImplementsVoidRequest_RegistersAndDispatches()
    {
        MediatorRegistry.RegisterRequest<DualRegistryRequest, int>();
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<DualRegistryRequest, int>), new DualRegistryRequestHandler())));

        var response = await mediator.Send<int>(new DualRegistryRequest(), CancellationToken.None);

        Assert.Equal(42, response);
    }

    /// <summary>
    /// Verifies a void-style request cannot be registered as returning <see cref="Unit"/>.
    /// </summary>
    [Fact]
    public void RegisterRequest_WhenVoidRequestRegisteredWithUnit_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(MediatorRegistry.RegisterRequest<VoidRegistryCommand, Unit>);

        Assert.Equal("TRequest", exception.ParamName);
    }

    /// <summary>
    /// Verifies a request that implements only <see cref="IRequest{TResponse}"/> of <see cref="Unit"/> still registers.
    /// </summary>
    [Fact]
    public void RegisterRequest_WhenRequestOnlyImplementsGenericUnit_DoesNotThrow()
    {
        MediatorRegistry.RegisterRequest<GenericUnitRegistryRequest, Unit>();
    }

    /// <summary>
    /// Represents a request with both a void and a typed contract.
    /// </summary>
    private sealed class DualRegistryRequest : IRequest, IRequest<int>;

    /// <summary>
    /// Represents a void-style request.
    /// </summary>
    private sealed class VoidRegistryCommand : IRequest;

    /// <summary>
    /// Represents a request that returns <see cref="Unit"/> through the generic contract only.
    /// </summary>
    private sealed class GenericUnitRegistryRequest : IRequest<Unit>;

    /// <summary>
    /// Handles the typed contract of <see cref="DualRegistryRequest"/>.
    /// </summary>
    private sealed class DualRegistryRequestHandler : IRequestHandler<DualRegistryRequest, int>
    {
        /// <inheritdoc />
        public Task<int> Handle(DualRegistryRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(42);
        }
    }
}