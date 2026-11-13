using AbpPlugin.Hosting;
using AbpPlugin.Packaging;
using Volo.Abp.Uow;

namespace AbpPlugin.SampleHost;

/// <summary>
/// The sample's administration, over HTTP: what an application's own plugin pages call. Packages come as a zip in the
/// request's body. Any signed-in account may use it here; an application lets only its admins. A refused package answers
/// 400 with the host's reason.
/// </summary>
public static class SampleAdmin
{
    public sealed record PluginRow(string Key, string Name, string Version, string State, string? SignatureKind, string? Signer, string? LastError);

    public sealed record PublisherInput(string? Name, string? PublicKey);

    public sealed record UnsignedInput(bool Allow);

    public static void MapSampleAdmin(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder admin = endpoints.MapGroup("/admin").RequireAuthorization();

        admin.MapGet("/plugins", (PluginManager manager, IUnitOfWorkManager units, CancellationToken cancellationToken) =>
            InUnitAsync(units, async () => Results.Ok((await manager.ListAsync(cancellationToken)).Select(Row))));

        admin.MapPost("/plugins", (HttpRequest request, PluginManager manager, IUnitOfWorkManager units, CancellationToken cancellationToken) =>
            WithPackageAsync(request, path => InUnitAsync(units, async () => Results.Ok(Row(await manager.InstallAsync(path, cancellationToken: cancellationToken))))));

        admin.MapPost("/plugins/{key}/upgrade", (string key, HttpRequest request, PluginManager manager, IUnitOfWorkManager units, CancellationToken cancellationToken) =>
            WithPackageAsync(request, path => InUnitAsync(units, async () => Results.Ok(Row(await manager.UpgradeAsync(key, path, cancellationToken: cancellationToken))))));

        admin.MapPost("/plugins/{key}/disable", (string key, PluginManager manager, IUnitOfWorkManager units, CancellationToken cancellationToken) =>
            InUnitAsync(units, async () => Results.Ok(Row(await manager.DisableAsync(key, cancellationToken)))));

        admin.MapPost("/plugins/{key}/enable", (string key, PluginManager manager, IUnitOfWorkManager units, CancellationToken cancellationToken) =>
            InUnitAsync(units, async () => Results.Ok(Row(await manager.EnableAsync(key, cancellationToken)))));

        admin.MapDelete("/plugins/{key}", (string key, PluginManager manager, IUnitOfWorkManager units, CancellationToken cancellationToken) =>
            InUnitAsync(units, async () =>
            {
                await manager.UninstallAsync(key, cancellationToken);
                return Results.NoContent();
            }));

        admin.MapPost("/publishers", (PublisherInput input, PluginTrust trust, IPluginCaller caller, IUnitOfWorkManager units, CancellationToken cancellationToken) =>
            InUnitAsync(units, async () =>
            {
                PluginPublisher publisher = await trust.AddPublisherAsync(input.Name, input.PublicKey, caller.AccountId, cancellationToken);
                return Results.Ok(new { publisher.KeyId, publisher.Name });
            }));

        admin.MapPut("/unsigned", (UnsignedInput input, PluginTrust trust, IUnitOfWorkManager units, CancellationToken cancellationToken) =>
            InUnitAsync(units, async () =>
            {
                await trust.SetAllowUnsignedAsync(input.Allow, cancellationToken);
                return Results.NoContent();
            }));
    }

    private static PluginRow Row(PluginInstallation row) =>
        new(row.Key, row.Name, row.Version, row.State.ToString(), row.SignatureKind, row.Signer, row.LastError);

    /// <summary>One unit of work for the call, committed when it succeeds; a refusal answers 400 with its reason.</summary>
    private static async Task<IResult> InUnitAsync(IUnitOfWorkManager units, Func<Task<IResult>> action)
    {
        try
        {
            using IUnitOfWork unit = units.Begin(requiresNew: true, isTransactional: false);
            IResult result = await action();
            await unit.CompleteAsync();
            return result;
        }
        catch (PluginException refused)
        {
            return Results.BadRequest(new { error = refused.Message });
        }
    }

    /// <summary>The request's body saved as a zip for the host to read, deleted after.</summary>
    private static async Task<IResult> WithPackageAsync(HttpRequest request, Func<string, Task<IResult>> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"abp-plugin-upload-{Guid.NewGuid():N}.zip");
        try
        {
            await using (FileStream file = File.Create(path))
            {
                await request.Body.CopyToAsync(file, request.HttpContext.RequestAborted);
            }

            return await action(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
