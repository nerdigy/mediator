using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using Nerdigy.Mediator.Contracts;
using Nerdigy.Mediator.DependencyInjection;
using Nerdigy.Mediator.IntegrationTests.Fixtures;

namespace Nerdigy.Mediator.IntegrationTests;

/// <summary>
/// Verifies that <c>AddMediator</c> rejects registrations that would silently misbehave, and that explicit registration
/// works without scanning.
/// </summary>
public sealed class MediatorRegistrationRulesTests
{
    /// <summary>
    /// Verifies components closed over an interface message type are rejected with the open generic shape that works.
    /// </summary>
    [Fact]
    public void AddMediator_WhenScannedComponentTargetsInterfaceMessage_ThrowsWithFix()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Scan(typeof(AuditBehavior), typeof(AuditPreProcessor), typeof(AllNotificationsHandler)));

        Assert.Contains("AuditBehavior implements IPipelineBehavior<IAuditable, Unit>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AuditBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IAuditable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AuditPreProcessor implements IRequestPreProcessor<IAuditable>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AllNotificationsHandler implements INotificationHandler<INotification>", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies explicitly registering a component closed over an interface message type is rejected.
    /// </summary>
    [Fact]
    public void AddBehavior_WhenTargetingInterfaceMessage_ThrowsArgumentException()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() =>
            services.AddMediator(options => options.AddBehavior<IAuditable, Unit, AuditBehavior>()));

        Assert.Contains("never runs", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a void request handled through <c>IRequestHandler&lt;TRequest, Unit&gt;</c> is rejected.
    /// </summary>
    [Fact]
    public void AddMediator_WhenVoidRequestHasUnitHandler_ThrowsWithFix()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Scan(typeof(VoidCommandUnitHandler)));

        Assert.Contains("Implement IRequestHandler<VoidCommand> instead", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies two scanned handlers for the same request are rejected instead of one silently winning.
    /// </summary>
    [Fact]
    public void AddMediator_WhenScanningDuplicateHandlers_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Scan(typeof(RuleQueryHandlerA), typeof(RuleQueryHandlerB)));

        Assert.Contains("IRequestHandler<RuleQuery, String> has more than one handler", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies duplicates are also found across separate <c>AddMediator</c> calls.
    /// </summary>
    [Fact]
    public void AddMediator_WhenSeparateCallsScanDuplicateHandlers_Throws()
    {
        var services = new ServiceCollection();
        Scan(services, typeof(RuleQueryHandlerA));

        var exception = Assert.Throws<InvalidOperationException>(() => Scan(services, typeof(RuleQueryHandlerB)));

        Assert.Contains("has more than one handler", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies two explicit handlers for the same request are rejected.
    /// </summary>
    [Fact]
    public void AddRequestHandler_WhenRegisteredTwiceWithDifferentHandlers_Throws()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(options => options
                .AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>()
                .AddRequestHandler<RuleQuery, string, RuleQueryHandlerB>()));

        Assert.Contains("Register only one of them", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies an explicitly registered handler replaces a scanned handler, in either order.
    /// </summary>
    /// <param name="scanFirst">Whether the scanning call comes first.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddRequestHandler_WhenScannedHandlerExists_Overrides(bool scanFirst)
    {
        var services = new ServiceCollection();

        if (scanFirst)
        {
            Scan(services, typeof(RuleQueryHandlerA));
            services.AddMediator(options => options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerB>());
        }
        else
        {
            services.AddMediator(options =>
            {
                options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerB>();
                options.RegisterServicesFromAssembly(new TypeListAssembly(typeof(RuleQueryHandlerA)));
            });
        }

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Equal("B", await provider.GetRequiredService<IMediator>().Send(new RuleQuery()));
    }

    /// <summary>
    /// Verifies a handler registered directly before <c>AddMediator</c> is kept.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenHandlerRegisteredBeforehand_KeepsIt()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<RuleQuery, string>, RuleQueryHandlerB>();
        Scan(services, typeof(RuleQueryHandlerA));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Equal("B", await provider.GetRequiredService<IMediator>().Send(new RuleQuery()));
    }

    /// <summary>
    /// Verifies scanned behaviors run in order of full type name, whatever order the assembly lists them in.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenScanningBehaviors_OrdersThemByFullTypeName()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RuleEventLog>();
        Scan(services, typeof(ZRuleQueryBehavior), typeof(RuleQueryHandlerA), typeof(ARuleQueryBehavior));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        await provider.GetRequiredService<IMediator>().Send(new RuleQuery());

        Assert.Equal(["A", "Z"], provider.GetRequiredService<RuleEventLog>().Events);
    }

    /// <summary>
    /// Verifies types that fail to load fail registration, unless the failure is explicitly ignored.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenTypesFailToLoad_ThrowsUnlessIgnored()
    {
        var assembly = new TypeListAssembly([typeof(RuleQueryHandlerA)], new FileNotFoundException("Could not load Missing.Dependency."));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddMediator(options => options.RegisterServicesFromAssembly(assembly)));
        Assert.Contains("Could not load Missing.Dependency.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("IgnoreTypeLoadFailures", exception.Message, StringComparison.Ordinal);

        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.IgnoreTypeLoadFailures = true;
            options.RegisterServicesFromAssembly(assembly);
        });
        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Equal("A", await provider.GetRequiredService<IMediator>().Send(new RuleQuery()));
    }

    /// <summary>
    /// Verifies a singleton mediator cannot be combined with scoped components.
    /// </summary>
    [Fact]
    public void AddMediator_WhenSingletonMediatorHasScopedHandlers_Throws()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(options =>
            {
                options.MediatorLifetime = ServiceLifetime.Singleton;
                options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>(ServiceLifetime.Scoped);
            }));

        Assert.StartsWith("Mediator registration failed:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("A singleton mediator resolves handlers from the root service provider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RuleQueryHandlerA is registered as Scoped", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a singleton mediator with scoped components and a duplicate handler lists both problems in one exception,
    /// whichever of the two handlers is scoped.
    /// </summary>
    /// <param name="firstLifetime">The lifetime of the first handler.</param>
    /// <param name="secondLifetime">The lifetime of the second handler.</param>
    /// <param name="expected">The expected description of the scoped component.</param>
    [Theory]
    [InlineData(ServiceLifetime.Scoped, ServiceLifetime.Transient, "RuleQueryHandlerA is registered as Scoped")]
    [InlineData(ServiceLifetime.Transient, ServiceLifetime.Scoped, "RuleQueryHandlerB is registered as Scoped")]
    public void AddMediator_WhenSingletonMediatorHasScopedAndDuplicateHandlers_ListsBoth(
        ServiceLifetime firstLifetime,
        ServiceLifetime secondLifetime,
        string expected)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddMediator(options =>
            {
                options.MediatorLifetime = ServiceLifetime.Singleton;
                options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>(firstLifetime);
                options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerB>(secondLifetime);
            }));

        Assert.StartsWith("Mediator registration failed:", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"  - A singleton mediator resolves handlers from the root service provider, which cannot resolve scoped services, but {expected}.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("has more than one handler", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a first call that fails leaves the collection as it was, so a corrected call with other settings succeeds.
    /// </summary>
    /// <param name="failOnLifetime">Whether the call fails on a lifetime problem rather than on a duplicate handler.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddMediator_WhenFirstCallFails_LeavesCollectionUnchangedAndCorrectedCallSucceeds(bool failOnLifetime)
    {
        var services = new ServiceCollection();
        services.AddSingleton<RuleEventLog>();
        ServiceDescriptor[] before = [.. services];

        Assert.Throws<InvalidOperationException>(() => services.AddMediator(options =>
        {
            options.MediatorLifetime = ServiceLifetime.Singleton;
            options.AddBehavior<RuleQuery, string, ARuleQueryBehavior>();
            options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>(failOnLifetime ? ServiceLifetime.Scoped : null);

            if (!failOnLifetime)
            {
                options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerB>();
            }
        }));

        Assert.Equal(before, services);

        services.AddMediator(options =>
        {
            options.MediatorLifetime = ServiceLifetime.Scoped;
            options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerB>(ServiceLifetime.Scoped);
        });
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.Equal("B", await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RuleQuery()));
    }

    /// <summary>
    /// Verifies a later call that fails leaves the collection and the tracked handlers as they were.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenLaterCallFails_LeavesCollectionAndTrackedHandlersUnchanged()
    {
        var services = new ServiceCollection();
        Scan(services, typeof(RuleQueryHandlerA));
        ServiceDescriptor[] before = [.. services];

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddMediator(options => options
            .AddBehavior<RuleQuery, string, ARuleQueryBehavior>()
            .AddRequestHandler<ExplicitQuery, string, ExplicitQueryHandler>()
            .AddRequestHandler<RuleQuery, string, RuleQueryHandlerB>()
            .AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>()));

        Assert.Contains("Register only one of them", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, services);

        // The failed call would have tracked B as the explicit handler, which would make this registration a no-op.
        services.AddMediator(options => options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerB>());
        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Equal("B", await provider.GetRequiredService<IMediator>().Send(new RuleQuery()));
    }

    /// <summary>
    /// Verifies an exception thrown while scanning leaves the collection as it was.
    /// </summary>
    [Fact]
    public void AddMediator_WhenScanningThrows_LeavesCollectionUnchanged()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RuleEventLog>();
        ServiceDescriptor[] before = [.. services];

        Assert.Throws<BadImageFormatException>(() => services.AddMediator(options =>
        {
            options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>();
            options.RegisterServicesFromAssembly(new ThrowingAssembly());
        }));

        Assert.Equal(before, services);
    }

    /// <summary>
    /// Verifies an explicit lifetime for a handler an earlier call scanned replaces the descriptor at the same position.
    /// </summary>
    [Fact]
    public void AddRequestHandler_WhenEarlierCallScannedSameHandler_SetsExplicitLifetime()
    {
        var services = new ServiceCollection();
        Scan(services, typeof(RuleQueryHandlerA));
        var serviceType = typeof(IRequestHandler<RuleQuery, string>);
        var position = services.ToList().FindIndex(descriptor => descriptor.ServiceType == serviceType);

        services.AddMediator(options => options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>(ServiceLifetime.Singleton));

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == serviceType).Lifetime);
        Assert.Equal(position, services.ToList().FindIndex(descriptor => descriptor.ServiceType == serviceType));

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.Same(
            provider.GetRequiredService<IRequestHandler<RuleQuery, string>>(),
            scope.ServiceProvider.GetRequiredService<IRequestHandler<RuleQuery, string>>());
    }

    /// <summary>
    /// Verifies an explicit lifetime for a behavior an earlier call scanned applies without changing the behavior order.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddBehavior_WhenEarlierCallScannedSameBehavior_SetsLifetimeAndKeepsOrder()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RuleEventLog>();
        Scan(services, typeof(ARuleQueryBehavior), typeof(ZRuleQueryBehavior), typeof(RuleQueryHandlerA));

        services.AddMediator(options => options.AddBehavior<RuleQuery, string, ARuleQueryBehavior>(ServiceLifetime.Scoped));

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetServices<IPipelineBehavior<RuleQuery, string>>().ToList();
        var sameScope = firstScope.ServiceProvider.GetServices<IPipelineBehavior<RuleQuery, string>>().First();
        var otherScope = secondScope.ServiceProvider.GetServices<IPipelineBehavior<RuleQuery, string>>().First();

        Assert.IsType<ARuleQueryBehavior>(first[0]);
        Assert.IsType<ZRuleQueryBehavior>(first[1]);
        Assert.Same(first[0], sameScope);
        Assert.NotSame(first[0], otherScope);

        await firstScope.ServiceProvider.GetRequiredService<IMediator>().Send(new RuleQuery());

        Assert.Equal(["A", "Z"], provider.GetRequiredService<RuleEventLog>().Events);
    }

    /// <summary>
    /// Verifies an open behavior added again with a lifetime keeps its position and takes the lifetime, in one call or two.
    /// </summary>
    /// <param name="separateCalls">Whether the second registration is in a later <c>AddMediator</c> call.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddOpenBehavior_WhenAddedAgainWithLifetime_SetsLifetimeAndKeepsOrder(bool separateCalls)
    {
        var services = new ServiceCollection();
        services.AddSingleton<RuleEventLog>();

        if (separateCalls)
        {
            services.AddMediator(options => options
                .AddOpenBehavior(typeof(OpenRuleBehavior<,>))
                .AddBehavior<RuleQuery, string, ARuleQueryBehavior>());
            services.AddMediator(options => options.AddOpenBehavior(typeof(OpenRuleBehavior<,>), ServiceLifetime.Singleton));
        }
        else
        {
            services.AddMediator(options => options
                .AddOpenBehavior(typeof(OpenRuleBehavior<,>))
                .AddBehavior<RuleQuery, string, ARuleQueryBehavior>()
                .AddOpenBehavior(typeof(OpenRuleBehavior<,>), ServiceLifetime.Singleton));
        }

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var first = provider.GetServices<IPipelineBehavior<RuleQuery, string>>().ToList();
        var second = provider.GetServices<IPipelineBehavior<RuleQuery, string>>().ToList();

        Assert.Equal(2, first.Count);
        Assert.IsType<OpenRuleBehavior<RuleQuery, string>>(first[0]);
        Assert.IsType<ARuleQueryBehavior>(first[1]);
        Assert.Same(first[0], second[0]);
    }

    /// <summary>
    /// Verifies an explicit registration that names no lifetime leaves an existing registration's lifetime alone.
    /// </summary>
    [Fact]
    public void AddMediator_WhenExplicitRegistrationNamesNoLifetime_KeepsExistingLifetime()
    {
        var services = new ServiceCollection();
        Scan(services, typeof(RuleQueryHandlerA), typeof(ZRuleQueryBehavior));

        services.AddMediator(options =>
        {
            options.HandlerLifetime = ServiceLifetime.Singleton;
            options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>();
            options.AddBehavior<RuleQuery, string, ZRuleQueryBehavior>();
        });

        Assert.Equal(ServiceLifetime.Transient, Assert.Single(services, static d => d.ServiceType == typeof(IRequestHandler<RuleQuery, string>)).Lifetime);
        Assert.Equal(ServiceLifetime.Transient, Assert.Single(services, static d => d.ServiceType == typeof(IPipelineBehavior<RuleQuery, string>)).Lifetime);
    }

    /// <summary>
    /// Verifies a handler registered directly before <c>AddMediator</c> keeps its descriptor, even when an explicit
    /// registration names a scoped lifetime under a singleton mediator, because that lifetime is never registered.
    /// </summary>
    [Fact]
    public void AddRequestHandler_WhenHandlerRegisteredDirectlyBeforehand_DoesNotModifyIt()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<RuleQuery, string>, RuleQueryHandlerA>();
        var direct = services[^1];

        services.AddMediator(options =>
        {
            options.MediatorLifetime = ServiceLifetime.Singleton;
            options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>(ServiceLifetime.Scoped);
        });

        Assert.Same(direct, Assert.Single(services, static d => d.ServiceType == typeof(IRequestHandler<RuleQuery, string>)));
    }

    /// <summary>
    /// Verifies a singleton mediator rejects an explicit scoped lifetime for a handler an earlier call registered.
    /// </summary>
    [Fact]
    public void AddRequestHandler_WhenSingletonMediatorAndExistingHandlerMadeScoped_Throws()
    {
        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.MediatorLifetime = ServiceLifetime.Singleton;
            options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>();
        });
        ServiceDescriptor[] before = [.. services];

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(options => options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>(ServiceLifetime.Scoped)));

        Assert.Contains("RuleQueryHandlerA is registered as Scoped", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, services);
    }

    /// <summary>
    /// Verifies a singleton mediator accepts a scoped default lifetime that registers nothing, because the only component
    /// already has a registration and the call names no lifetime for it.
    /// </summary>
    [Fact]
    public void AddMediator_WhenSingletonMediatorScopedDefaultRegistersNothing_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.MediatorLifetime = ServiceLifetime.Singleton;
            options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>();
        });

        services.AddMediator(options =>
        {
            options.HandlerLifetime = ServiceLifetime.Scoped;
            options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>();
        });

        Assert.Equal(ServiceLifetime.Transient, Assert.Single(services, static d => d.ServiceType == typeof(IRequestHandler<RuleQuery, string>)).Lifetime);
    }

    /// <summary>
    /// Verifies a singleton mediator accepts a scoped registration that a later registration in the same call overrides.
    /// </summary>
    [Fact]
    public void AddRequestHandler_WhenSingletonMediatorAndScopedLifetimeOverriddenInSameCall_UsesLastLifetime()
    {
        var services = new ServiceCollection();

        services.AddMediator(options =>
        {
            options.MediatorLifetime = ServiceLifetime.Singleton;
            options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>(ServiceLifetime.Scoped);
            options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>(ServiceLifetime.Singleton);
        });

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, static d => d.ServiceType == typeof(IRequestHandler<RuleQuery, string>)).Lifetime);
    }

    /// <summary>
    /// Verifies a later <c>AddMediator</c> call cannot change settings the first call made, but can add handlers.
    /// </summary>
    [Fact]
    public void AddMediator_WhenCalledAgain_KeepsFirstSettingsAndAddsHandlers()
    {
        var services = new ServiceCollection();
        services.AddMediator(options => options.MediatorLifetime = ServiceLifetime.Scoped);

        services.AddMediator(options => options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>());
        var lifetimeChange = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(options => options.MediatorLifetime = ServiceLifetime.Transient));
        var publisherChange = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediator(options => options.UseNotificationPublishStrategy(NotificationPublishStrategy.Parallel)));

        Assert.Contains("cannot change it to Transient", lifetimeChange.Message, StringComparison.Ordinal);
        Assert.Contains("cannot change it to TaskWhenAllPublisher", publisherChange.Message, StringComparison.Ordinal);
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IMediator));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IRequestHandler<RuleQuery, string>));
    }

    /// <summary>
    /// Verifies every kind of component can be registered explicitly, without scanning, and runs under a validating container.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WithExplicitRegistrationOnly_RunsEveryComponent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RuleEventLog>();
        services.AddMediator(options => options
            .AddRequestHandler<ExplicitQuery, string, ExplicitQueryHandler>()
            .AddRequestHandler<ExplicitCommand, ExplicitCommandHandler>()
            .AddStreamRequestHandler<ExplicitStream, int, ExplicitStreamHandler>()
            .AddNotificationHandler<ExplicitNotification, ExplicitNotificationHandler>()
            .AddPreProcessor<ExplicitQuery, ExplicitPreProcessor>()
            .AddBehavior<ExplicitQuery, string, ExplicitBehavior>()
            .AddPostProcessor<ExplicitQuery, string, ExplicitPostProcessor>()
            .AddExceptionHandler<ExplicitCommand, Unit, InvalidOperationException, ExplicitCommandExceptionHandler>()
            .AddExceptionAction<ExplicitCommand, Exception, ExplicitCommandExceptionAction>()
            .AddStreamBehavior<ExplicitStream, int, ExplicitStreamBehavior>());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var events = scope.ServiceProvider.GetRequiredService<RuleEventLog>().Events;

        var response = await mediator.Send(new ExplicitQuery());
        await mediator.Send(new ExplicitCommand());
        List<int> streamed = [];

        await foreach (var value in mediator.CreateStream(new ExplicitStream()))
        {
            streamed.Add(value);
        }

        await mediator.Publish(new ExplicitNotification());

        Assert.Equal("query", response);
        Assert.Equal([10, 20], streamed);
        Assert.Equal(
            ["pre", "behavior", "query", "post", "command", "command-handled", "stream-behavior", "notification"],
            events);
    }

    /// <summary>
    /// Verifies handlers resolved through a scoped mediator are disposed when the scope ends, and not before.
    /// </summary>
    /// <param name="lifetime">The handler lifetime.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Scoped)]
    public async Task Send_WhenScopeEnds_DisposesHandlers(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DisposalLog>();
        services.AddMediator(options => options.AddRequestHandler<DisposableQuery, string, DisposableQueryHandler>(lifetime));
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var log = provider.GetRequiredService<DisposalLog>();

        await using (var scope = provider.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new DisposableQuery());
            await mediator.Send(new DisposableQuery());

            Assert.Equal(0, log.DisposedAsync);
        }

        Assert.Equal(lifetime == ServiceLifetime.Scoped ? 1 : 2, log.DisposedAsync);
        Assert.Equal(0, log.Disposed);
    }

    /// <summary>
    /// Scans only the given types into a new service collection.
    /// </summary>
    /// <param name="types">The types to scan.</param>
    private static void Scan(params Type[] types)
    {
        Scan(new ServiceCollection(), types);
    }

    /// <summary>
    /// Scans only the given types into a service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="types">The types to scan.</param>
    private static void Scan(IServiceCollection services, params Type[] types)
    {
        services.AddMediator(options => options.RegisterServicesFromAssembly(new TypeListAssembly(types)));
    }

    /// <summary>
    /// An assembly whose types cannot be read at all.
    /// </summary>
    private sealed class ThrowingAssembly : Assembly
    {
        /// <inheritdoc />
        public override string FullName => "ThrowingAssembly";

        /// <inheritdoc />
        public override Type[] GetTypes()
        {
            throw new BadImageFormatException("Unreadable assembly.");
        }
    }

    /// <summary>
    /// An assembly that exposes a fixed list of types, optionally failing to load some of them.
    /// </summary>
    /// <param name="types">The types the assembly exposes.</param>
    /// <param name="loaderException">The loader failure to report, or <see langword="null"/>.</param>
    private sealed class TypeListAssembly(Type[] types, Exception? loaderException = null) : Assembly
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TypeListAssembly"/> class that loads every type.
        /// </summary>
        /// <param name="types">The types the assembly exposes.</param>
        public TypeListAssembly(params Type[] types)
            : this(types, null)
        {
        }

        /// <inheritdoc />
        public override string FullName => "TypeListAssembly";

        /// <inheritdoc />
        public override Type[] GetTypes()
        {
            return loaderException is null
                ? types
                : throw new ReflectionTypeLoadException([.. types, null], [loaderException]);
        }
    }
}