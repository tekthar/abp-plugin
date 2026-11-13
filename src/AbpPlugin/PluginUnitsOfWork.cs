using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using AbpPlugin;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;
using Volo.Abp.Uow.EntityFrameworkCore;

namespace AbpPlugin.Hosting;

/// <summary>
/// A plugin call (an endpoint, a recurring job, an event handler) as one unit of work with the server. A
/// transactional one opens the server's transaction before the plugin's services are made, so the plugin's contexts
/// join it (<see cref="HostTransactions"/>), and the server's services the plugin calls join it too, whichever module
/// (<see cref="ModuleUnitsOfWork"/>): all of it commits when the call ends, or none of it when it throws. One that
/// isn't (a GET, work marked <see cref="PluginUnitOfWorkAttribute"/> non-transactional) commits each save by itself.
/// </summary>
public static class PluginCalls
{
    /// <summary>
    /// Whether a plugin's call is one unit of work with the server: unless its class is marked otherwise, with
    /// AbpPlugin.Sdk's attribute or the application's SDK's (the profile's).
    /// </summary>
    public static bool IsTransactional(Type callType) =>
        (callType.GetCustomAttribute<AbpPlugin.PluginUnitOfWorkAttribute>()?.IsTransactional ?? PluginHostProfile.Current.TransactionalMark(callType)) != false;

    public static Task RunAsync(IUnitOfWorkManager unitOfWorks, LoadedPlugin plugin, bool transactional, Func<IServiceProvider, Task> call) =>
        RunAsync<object?>(unitOfWorks, plugin.CreateScope, transactional, async services =>
        {
            await call(services);
            return null;
        }, committed: null);

    public static Task RunAsync<T>(IUnitOfWorkManager unitOfWorks, LoadedPlugin plugin, bool transactional, Func<IServiceProvider, Task<T>> call,
        Func<T, Task>? committed) =>
        RunAsync(unitOfWorks, plugin.CreateScope, transactional, call, committed);

    /// <summary>
    /// A call whose answer is used once it's committed (an endpoint's result, written then), while the plugin's
    /// services are still there: a stream's run on them.
    /// </summary>
    public static async Task<T> RunAsync<T>(IUnitOfWorkManager unitOfWorks, Func<AsyncServiceScope> createScope, bool transactional,
        Func<IServiceProvider, Task<T>> call, Func<T, Task>? committed)
    {
        await using AsyncServiceScope scope = createScope();
        T answer;
        using (IUnitOfWork uow = unitOfWorks.Begin(new AbpUnitOfWorkOptions(isTransactional: transactional), requiresNew: true))
        {
            if (transactional)
            {
                await HostTransactions.OpenAsync(uow);
            }

            // The switch ends before the commit, so what runs once it's done (local event handlers) starts afresh.
            using (transactional ? PluginHostProfile.Current.JoinUnitsOfWork() : null)
            {
                answer = await call(scope.ServiceProvider);
            }

            // A call that finished commits, whether or not its caller is still there to hear it.
            await uow.CompleteAsync(CancellationToken.None);
        }

        if (committed is not null)
        {
            await committed(answer);
        }

        return answer;
    }
}

/// <summary>A plugin's own batches (<see cref="IPluginUnitsOfWork"/>): each a transactional plugin call of its own, on a fresh scope.</summary>
internal sealed class PluginUnitsOfWork(IServiceScopeFactory pluginScopes, IUnitOfWorkManager unitOfWorks) : IPluginUnitsOfWork
{
    public Task RunAsync(Func<IServiceProvider, CancellationToken, Task> batch, CancellationToken cancellationToken = default) =>
        RunAsync<object?>(async (services, ct) =>
        {
            await batch(services, ct);
            return null;
        }, cancellationToken);

    public Task<T> RunAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> batch, CancellationToken cancellationToken = default) =>
        PluginCalls.RunAsync(unitOfWorks, pluginScopes.CreateAsyncScope, transactional: true, services => batch(services, cancellationToken), committed: null);
}

/// <summary>
/// The transaction a plugin's context joins: the open one of the server's unit of work on the server's
/// database, found as ABP keys it ("EntityFrameworkCore_" and the connection string). Null outside a transactional unit
/// of work: the plugin's context then has a connection of its own.
/// </summary>
internal sealed class HostTransactions(IServiceProvider hostServices)
{
    /// <summary>Opens the server's transaction in a unit of work that has none yet, awaited, as ABP opens it.</summary>
    public static async Task OpenAsync(IUnitOfWork uow) =>
        await ((IServiceProviderAccessor)uow).ServiceProvider!.GetRequiredService<IDbContextProvider<IPluginsDbContext>>().GetDbContextAsync();

    public DbTransaction? Find(string connectionString)
    {
        if (hostServices.GetService<IUnitOfWorkManager>()?.Current is not { Options.IsTransactional: true } uow)
        {
            return null;
        }

        string key = "EntityFrameworkCore_" + connectionString;
        if (uow.FindTransactionApi(key) is not EfCoreTransactionApi api)
        {
            // An extension leased in a unit of work that hasn't touched the database yet (a plugin call opened it
            // already): it's opened here the way ABP's own contexts open it, from a constructor that can't await.
#pragma warning disable CS0618
            ((IServiceProviderAccessor)uow).ServiceProvider!.GetRequiredService<IDbContextProvider<IPluginsDbContext>>().GetDbContext();
#pragma warning restore CS0618
            api = uow.FindTransactionApi(key) as EfCoreTransactionApi ?? throw new InvalidOperationException(
                "A plugin's tables must be on the server's database (ConnectionStrings:Default), to commit with the server's.");
        }

        return api.DbContextTransaction.GetDbTransaction();
    }
}

/// <summary>The server's transaction for one plugin scope, looked up the first time one of its contexts is made.</summary>
internal sealed class PluginSharedTransaction(Func<DbTransaction?> find)
{
    private readonly Lazy<DbTransaction?> _transaction = new(find);

    public DbTransaction? Current => _transaction.Value;
}
