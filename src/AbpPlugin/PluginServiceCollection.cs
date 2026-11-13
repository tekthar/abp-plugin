using System.Reflection;
using AbpPlugin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AbpPlugin.Hosting;

/// <summary>
/// What a plugin registers, as the host keeps it: its own services, and every registration the host must wire
/// up, each with how the host calls it, so the host never needs to know the SDK a plugin was built on. A plugin built on
/// AbpPlugin.Sdk registers through <see cref="IPluginServices"/>; one built on another SDK, through its host application's
/// bridge over the methods taking types.
/// </summary>
public sealed class PluginServiceCollection : IPluginServices
{
    private readonly IServiceProvider _efInternalServiceProvider;

    internal PluginServiceCollection(IServiceCollection services, PluginContext context, IServiceProvider efInternalServiceProvider)
    {
        Services = services;
        Context = context;
        _efInternalServiceProvider = efInternalServiceProvider;
    }

    /// <summary>The plugin's private service collection, for its own internal services.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The running plugin: its key, version, schema, connection and folder.</summary>
    public PluginContext Context { get; }

    internal PluginRegistrations Registrations { get; } = new();

    // AbpPlugin.Sdk's registrations.

    IPluginServices IPluginServices.AddDbContext<TContext>()
    {
        AddDbContext(typeof(TContext), (builder, schema) => builder.UsePluginSchema(schema));
        return this;
    }

    IPluginServices IPluginServices.AddSeeder<TSeeder>()
    {
        AddSeeder(typeof(TSeeder), (seeder, cancellationToken) => ((IPluginSeeder)seeder).SeedAsync(cancellationToken));
        return this;
    }

    IPluginServices IPluginServices.AddEventHandler<TEvent, THandler>()
    {
        AddEventHandler(typeof(TEvent), typeof(THandler), local: false,
            (handler, eventData, cancellationToken) => ((IEventHandler<TEvent>)handler).HandleAsync((TEvent)eventData, cancellationToken));
        return this;
    }

    IPluginServices IPluginServices.AddLocalEventHandler<TEvent, THandler>()
    {
        AddEventHandler(typeof(TEvent), typeof(THandler), local: true,
            (handler, eventData, cancellationToken) => ((ILocalEventHandler<TEvent>)handler).HandleAsync((TEvent)eventData, cancellationToken));
        return this;
    }

    IPluginServices IPluginServices.AddRecurringJob<TJob>(string cron)
    {
        AddRecurringJob(typeof(TJob), cron, (job, cancellationToken) => ((IRecurringJob)job).ExecuteAsync(cancellationToken));
        return this;
    }

    IPluginServices IPluginServices.AddExtension<TExtensionPoint, TImplementation>()
    {
        AddExtension(typeof(TExtensionPoint), typeof(TImplementation));
        return this;
    }

    IPluginServices IPluginServices.AddEndpoint<TEndpoint>(string method, string pattern)
    {
        AddEndpoint(method, pattern, typeof(TEndpoint), isPublic: false, Call);
        return this;
    }

    IPluginServices IPluginServices.AddPublicEndpoint<TEndpoint>(string method, string pattern)
    {
        AddEndpoint(method, pattern, typeof(TEndpoint), isPublic: true, Call);
        return this;
    }

    private static Task<IResult> Call(object endpoint, HttpRequest request, RouteValueDictionary values, CancellationToken cancellationToken) =>
        ((IPluginEndpoint)endpoint).HandleAsync(new PluginRequest(request, values), cancellationToken);

    IPluginServices IPluginServices.AddHealthCheck<TCheck>(string name)
    {
        AddHealthCheck(name, typeof(TCheck), (check, cancellationToken) => ((IPluginHealthCheck)check).CheckAsync(cancellationToken));
        return this;
    }

    // The registrations by type, each with how the host calls it: what an SDK's bridge registers through.

    /// <summary>
    /// The plugin's DbContext, of <paramref name="contextType"/>: its options carry the plugin's schema by
    /// <paramref name="useSchema"/> (the SDK's own extension, which its context reads). Migrations run on install and
    /// upgrade, in the plugin's schema.
    /// </summary>
    public void AddDbContext(Type contextType, Func<DbContextOptionsBuilder, string, DbContextOptionsBuilder> useSchema)
    {
        if (!typeof(DbContext).IsAssignableFrom(contextType))
        {
            throw new ArgumentException($"'{contextType}' isn't a DbContext.", nameof(contextType));
        }

        AddDbContextOf.MakeGenericMethod(contextType).Invoke(this, [useSchema]);
    }

    private static readonly MethodInfo AddDbContextOf =
        typeof(PluginServiceCollection).GetMethod(nameof(AddDbContextCore), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private void AddDbContextCore<TContext>(Func<DbContextOptionsBuilder, string, DbContextOptionsBuilder> useSchema) where TContext : DbContext
    {
        // Inside a transactional unit of work of the server's the context is made on its open connection and joins its
        // transaction, so the plugin's writes commit and roll back with the server's; outside one (the
        // install's migrations, a GET, work that isn't transactional) it has a connection of its own.
        Services.AddScoped(sp => Options<TContext>(sp.GetService<PluginSharedTransaction>()?.Current, useSchema));
        Services.AddScoped(sp =>
        {
            DbContextOptions<TContext> options = sp.GetRequiredService<DbContextOptions<TContext>>();
            TContext db = ActivatorUtilities.CreateInstance<TContext>(sp, options);
            if (sp.GetService<PluginSharedTransaction>()?.Current is { } shared)
            {
                db.Database.UseTransaction(shared);
            }

            return db;
        });
        Registrations.DbContexts.Add(typeof(TContext));
    }

    private DbContextOptions<TContext> Options<TContext>(System.Data.Common.DbTransaction? shared,
        Func<DbContextOptionsBuilder, string, DbContextOptionsBuilder> useSchema) where TContext : DbContext
    {
        DbContextOptionsBuilder<TContext> builder = new();
        void Npgsql(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) => npgsql
            .MigrationsHistoryTable("__EFMigrationsHistory", Context.Schema)
            // Pass the Assembly itself: EF resolves a name through the default load context, where the plugin is invisible.
            .MigrationsAssembly(typeof(TContext).Assembly);
        if (shared?.Connection is { } connection)
        {
            builder.UseNpgsql(connection, Npgsql);
        }
        else
        {
            builder.UseNpgsql(Context.ConnectionString, Npgsql);
        }

        useSchema(builder, Context.Schema);
        return builder
            // EF Core keeps its model and query caches in the internal service provider.
            // One per plugin, disposed on unload, so those caches don't pin the plugin's types.
            .UseInternalServiceProvider(_efInternalServiceProvider)
            .Options;
    }

    /// <summary>Runs after migrations on install and every upgrade, by <paramref name="seed"/>. Must be idempotent.</summary>
    public void AddSeeder(Type seederType, Func<object, CancellationToken, Task> seed)
    {
        Services.AddScoped(seederType);
        Registrations.Seeders.Add(new SeederRegistration(seederType, seed));
    }

    /// <summary>Implements an extension point owned by the host or by another plugin.</summary>
    public void AddExtension(Type extensionPointType, Type implementationType)
    {
        Services.AddScoped(implementationType);
        Registrations.Extensions.Add(new ExtensionRegistration(extensionPointType, implementationType));
    }

    /// <summary>A job on a cron schedule (seconds field supported), run by <paramref name="execute"/>. Cancelled on disable.</summary>
    public void AddRecurringJob(Type jobType, string cron, Func<object, CancellationToken, Task> execute)
    {
        Services.AddScoped(jobType);
        Registrations.Jobs.Add(new RecurringJobRegistration(jobType, cron, execute));
    }

    /// <summary>A check shown on the platform health page, made by <paramref name="check"/>.</summary>
    public void AddHealthCheck(string name, Type checkType, Func<object, CancellationToken, Task<HealthCheckResult>> check)
    {
        Services.AddScoped(checkType);
        Registrations.HealthChecks.Add(new HealthCheckRegistration(name, checkType, check));
    }

    /// <summary>
    /// A handler of <paramref name="eventType"/>, called by <paramref name="handle"/> (the handler, the event): local, in
    /// the request after the change commits, or distributed, from the outbox. Unsubscribed on disable.
    /// </summary>
    public void AddEventHandler(Type eventType, Type handlerType, bool local, Func<object, object, CancellationToken, Task> handle)
    {
        Services.AddScoped(handlerType);
        Registrations.EventHandlers.Add(new EventHandlerRegistration(eventType, handlerType, local, handle));
    }

    /// <summary>
    /// An endpoint under /api/plugins/{key}/{pattern}, for signed-in callers, or anyone's under public/ when
    /// <paramref name="isPublic"/>, answered by <paramref name="handle"/> (the endpoint, the request, the values its
    /// pattern matched).
    /// </summary>
    public void AddEndpoint(string method, string pattern, Type endpointType, bool isPublic,
        Func<object, HttpRequest, RouteValueDictionary, CancellationToken, Task<IResult>> handle)
    {
        if (!isPublic && PublicCalls.IsPublic(pattern))
        {
            throw new InvalidOperationException($"The endpoint pattern '{pattern}' starts with public/, which is kept for AddPublicEndpoint.");
        }

        Services.AddScoped(endpointType);
        Registrations.Endpoints.Add(new EndpointRegistration(method.ToUpperInvariant(), pattern, endpointType, isPublic, handle));
    }

    /// <summary>A host application's check on who sees one of its pages; the host only keeps it.</summary>
    public void AddPageAccess(string pageKey, Type accessType)
    {
        Services.AddScoped(accessType);
        Registrations.PageAccess.Add(new PageAccessRegistration(pageKey, accessType));
    }
}

/// <summary>
/// Runs a plugin's entry of a kind the host application knows: an entry derived from <see cref="BaseName"/>,
/// which registers through the application's bridge over the host's registrations. The host runs AbpPlugin.Sdk's
/// <see cref="PluginEntry"/> itself.
/// </summary>
public interface IPluginEntryRunner
{
    /// <summary>The base class its entries derive from, as a message names it.</summary>
    string BaseName { get; }

    bool Runs(Type entryType);

    /// <summary>Makes the entry and has it register, through <paramref name="services"/>.</summary>
    void Configure(Type entryType, PluginServiceCollection services);
}
