using Microsoft.Extensions.DependencyInjection;

namespace Wondarr.Core.Jobs;

/// <summary>
/// The service collection the container was built from, kept so a handler that cannot be built can
/// be told apart from the handlers that can. <see cref="ICommandHandler.Name"/> is an instance
/// property, so without this one registration whose constructor throws would make every
/// <c>GetServices&lt;ICommandHandler&gt;()</c> throw, and with it every command, healthy or not.
/// </summary>
/// <param name="Services">The collection <c>AddWondarrCore</c> registered into.</param>
internal sealed record CommandHandlerRegistrations(IServiceCollection Services);

/// <summary>What <see cref="CommandHandlerResolver"/> found for one command name.</summary>
/// <param name="Handler">The handler that answers to the name, when one was built.</param>
/// <param name="Failures">The handlers that could not be built, as <c>"Type: reason"</c>.</param>
/// <param name="OwnedByCaller">
/// Whether the caller has to dispose <paramref name="Handler"/>: it was built outside the scope's
/// own tracking, so the scope will not dispose it.
/// </param>
internal sealed record CommandHandlerLookup(ICommandHandler? Handler, IReadOnlyList<string> Failures, bool OwnedByCaller = false)
{
    /// <summary>Disposes the handler when the caller owns it.</summary>
    /// <returns>A task that completes when the handler is disposed.</returns>
    public async ValueTask ReleaseAsync()
    {
        if (OwnedByCaller)
        {
            await CommandHandlerResolver.DisposeAsync(Handler).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Finds the handler for a command name without letting one broken registration hide the others.
/// </summary>
internal static class CommandHandlerResolver
{
    /// <summary>Finds the handler named <paramref name="name"/> in <paramref name="services"/>.</summary>
    /// <param name="services">The scope's service provider.</param>
    /// <param name="name">The command name.</param>
    /// <returns>The handler, if one answers to the name, and the handlers that could not be built.</returns>
    public static CommandHandlerLookup Find(IServiceProvider services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            var handler = services.GetServices<ICommandHandler>().FirstOrDefault(candidate => Matches(candidate, name));

            return new CommandHandlerLookup(handler, []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var registrations = services.GetService<CommandHandlerRegistrations>();
            if (registrations is null)
            {
                return new CommandHandlerLookup(null, [exception.Message]);
            }

            return FindOneByOne(services, registrations.Services, name);
        }
    }

    private static CommandHandlerLookup FindOneByOne(IServiceProvider services, IServiceCollection collection, string name)
    {
        var failures = new List<string>();

        foreach (var descriptor in collection.Where(descriptor => descriptor.ServiceType == typeof(ICommandHandler)).ToList())
        {
            try
            {
                var (handler, owned) = Build(services, descriptor);
                if (Matches(handler, name))
                {
                    return new CommandHandlerLookup(handler, failures, owned);
                }

                // Built only to read its name: nothing else will dispose it.
                if (owned)
                {
                    DisposeSync(handler);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var type = descriptor.ImplementationType?.Name ?? descriptor.ServiceType.Name;
                failures.Add($"{type}: {exception.Message}");
            }
        }

        return new CommandHandlerLookup(null, failures);
    }

    // Whether the caller owns the result: a registered instance belongs to the container, anything
    // built here belongs to us because no scope tracks it.
    private static (ICommandHandler Handler, bool Owned) Build(IServiceProvider services, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is ICommandHandler instance)
        {
            return (instance, false);
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return ((ICommandHandler)factory(services), true);
        }

        return ((ICommandHandler)ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!), true);
    }

    /// <summary>Disposes <paramref name="handler"/> if it is disposable.</summary>
    /// <param name="handler">The handler, or <see langword="null"/>.</param>
    /// <returns>A task that completes when it is disposed.</returns>
    internal static async ValueTask DisposeAsync(ICommandHandler? handler)
    {
        switch (handler)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private static void DisposeSync(ICommandHandler handler)
    {
        if (handler is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else if (handler is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static bool Matches(ICommandHandler handler, string name) =>
        string.Equals(handler.Name, name, StringComparison.OrdinalIgnoreCase);
}
