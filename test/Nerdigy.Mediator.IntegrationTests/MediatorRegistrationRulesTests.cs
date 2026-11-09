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
    /// Verifies the default handler lifetime is reported when it would register a scoped component under a singleton mediator.
    /// </summary>
    [Fact]
    public void AddMediator_WhenSingletonMediatorHasScopedDefaultLifetime_ReportsHandlerLifetime()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddMediator(options =>
            {
                options.MediatorLifetime = ServiceLifetime.Singleton;
                options.HandlerLifetime = ServiceLifetime.Scoped;
                options.AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>();
            }));

        Assert.Contains("but HandlerLifetime is Scoped.", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a scoped notification publisher is reported under a singleton mediator.
    /// </summary>
    [Fact]
    public void AddMediator_WhenSingletonMediatorHasScopedPublisher_ReportsPublisher()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddMediator(options =>
            {
                options.MediatorLifetime = ServiceLifetime.Singleton;
                options.UseNotificationPublisher<TaskWhenAllPublisher>(ServiceLifetime.Scoped);
            }));

        Assert.Contains("but the notification publisher is Scoped.", exception.Message, StringComparison.Ordinal);
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
    /// Verifies a singleton mediator works from the root provider under scope validation.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenSingletonMediatorHasNoScopedComponents_WorksFromRootProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RuleEventLog>();
        services.AddMediator(options =>
        {
            options.MediatorLifetime = ServiceLifetime.Singleton;
            options
                .AddRequestHandler<ExplicitQuery, string, ExplicitQueryHandler>()
                .AddNotificationHandler<ExplicitNotification, ExplicitNotificationHandler>(ServiceLifetime.Singleton)
                .AddStreamRequestHandler<ExplicitStream, int, ExplicitStreamHandler>();
        });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var mediator = provider.GetRequiredService<IMediator>();

        var response = await mediator.Send(new ExplicitQuery());
        await mediator.Publish(new ExplicitNotification());
        var streamed = await ToListAsync(mediator.CreateStream(new ExplicitStream()));

        Assert.Same(mediator, provider.GetRequiredService<IMediator>());
        Assert.Same(mediator, provider.GetRequiredService<ISender>());
        Assert.Equal("query", response);
        Assert.Equal([10, 20], streamed);
        Assert.Equal(["query", "notification"], provider.GetRequiredService<RuleEventLog>().Events);
    }

    /// <summary>
    /// Verifies an open stream behavior registered explicitly runs for a stream request.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddOpenStreamBehavior_WhenStreaming_RunsBehavior()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RuleEventLog>();
        services.AddMediator(options => options
            .AddStreamRequestHandler<ExplicitStream, int, ExplicitStreamHandler>()
            .AddOpenStreamBehavior(typeof(OpenRuleStreamBehavior<,>)));
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var streamed = await ToListAsync(scope.ServiceProvider.GetRequiredService<IMediator>().CreateStream(new ExplicitStream()));

        Assert.Equal([10, 20], streamed);
        Assert.Equal(["open-stream-behavior"], provider.GetRequiredService<RuleEventLog>().Events);
    }

    /// <summary>
    /// Verifies a stream exception handler registered explicitly replaces a failed stream.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddStreamExceptionHandler_WhenStreamFails_ReplacesStream()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RuleEventLog>();
        services.AddMediator(options => options
            .AddStreamRequestHandler<FailingRuleStream, int, FailingRuleStreamHandler>()
            .AddStreamExceptionHandler<FailingRuleStream, int, InvalidOperationException, FailingRuleStreamExceptionHandler>());
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var streamed = await ToListAsync(scope.ServiceProvider.GetRequiredService<IMediator>().CreateStream(new FailingRuleStream()));

        Assert.Equal([7], streamed);
        Assert.Equal(["stream-recovered"], provider.GetRequiredService<RuleEventLog>().Events);
    }

    /// <summary>
    /// Verifies an open behavior type that is abstract or an interface is rejected.
    /// </summary>
    /// <param name="openBehaviorType">The open behavior type.</param>
    [Theory]
    [InlineData(typeof(AbstractOpenRuleBehavior<,>))]
    [InlineData(typeof(IPipelineBehavior<,>))]
    public void AddOpenBehavior_WhenTypeIsNotConcreteClass_Throws(Type openBehaviorType)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddMediator(options => options.AddOpenBehavior(openBehaviorType)));

        Assert.Contains("must be a concrete class", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies an open behavior type that does not implement the behavior interface is rejected.
    /// </summary>
    [Fact]
    public void AddOpenBehavior_WhenTypeDoesNotImplementInterface_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddMediator(options => options.AddOpenBehavior(typeof(NotAPipelineBehavior<,>))));

        Assert.Contains("must implement IPipelineBehavior<TRequest, TResponse>", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies an unsupported notification publish strategy is rejected.
    /// </summary>
    [Fact]
    public void UseNotificationPublishStrategy_WhenStrategyIsUnsupported_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddMediator(options => options.UseNotificationPublishStrategy((NotificationPublishStrategy)999)));

        Assert.Contains("Unsupported notification publish strategy.", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a behavior closed over an abstract request type is rejected with the abstract class wording.
    /// </summary>
    [Fact]
    public void AddBehavior_WhenTargetingAbstractMessage_ThrowsWithAbstractClassWording()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddMediator(options => options.AddBehavior<AbstractRuleRequest, string, AbstractRuleRequestBehavior>()));

        Assert.Contains("AbstractRuleRequest is an abstract class. Components are resolved", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a handler closed over a non-concrete request type is rejected with the handler message.
    /// </summary>
    [Fact]
    public void AddRequestHandler_WhenTargetingNonConcreteRequest_ThrowsWithHandlerMessage()
    {
        var abstractRequest = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddMediator(options => options.AddRequestHandler<AbstractRuleRequest, string, AbstractRuleRequestHandler>()));
        var interfaceRequest = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddMediator(options => options.AddRequestHandler<IRuleRequest, string, InterfaceRuleRequestHandler>()));

        Assert.Contains("AbstractRuleRequest is an abstract class. Requests are dispatched by their concrete type, so this handler never runs.", abstractRequest.Message, StringComparison.Ordinal);
        Assert.Contains("IRuleRequest is an interface. Requests are dispatched by their concrete type, so this handler never runs.", interfaceRequest.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies keyed registrations neither count as an existing handler nor take a lifetime from an explicit registration.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenKeyedRegistrationsExist_IgnoresThem()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RuleEventLog>();
        services.AddKeyedTransient<IRequestHandler<RuleQuery, string>, RuleQueryHandlerB>("key");
        services.AddKeyedTransient<IPipelineBehavior<RuleQuery, string>, ARuleQueryBehavior>("key");
        var keyed = services.Where(static descriptor => descriptor.IsKeyedService).ToList();

        services.AddMediator(options => options
            .AddRequestHandler<RuleQuery, string, RuleQueryHandlerA>()
            .AddBehavior<RuleQuery, string, ARuleQueryBehavior>(ServiceLifetime.Singleton));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Equal("A", await provider.GetRequiredService<IMediator>().Send(new RuleQuery()));
        Assert.Equal(["A"], provider.GetRequiredService<RuleEventLog>().Events);
        Assert.Equal(keyed, services.Where(static descriptor => descriptor.IsKeyedService));
    }

    /// <summary>
    /// Verifies an abstract component in a scanned assembly is skipped.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenScannedAssemblyHasAbstractHandler_SkipsIt()
    {
        var services = new ServiceCollection();
        Scan(services, typeof(AbstractRuleQueryHandler), typeof(RuleQueryHandlerA));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Equal("A", await provider.GetRequiredService<IMediator>().Send(new RuleQuery()));
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
    /// Collects an asynchronous sequence into a list.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="source">The sequence.</param>
    /// <returns>The items.</returns>
    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        List<T> items = [];

        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
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