using Microsoft.AspNetCore.Http;

namespace AbpPlugin;

/// <summary>
/// JSON for plugin endpoints. Use this instead of <see cref="Results.Json"/>, <see cref="Results.Ok"/> and the like with a
/// value: the host's shared serializer options cache metadata for every type they see, which would keep an unloaded
/// plugin's types alive. Each plugin gets its own options, released with the plugin.
/// </summary>
public interface IPluginJson
{
    /// <summary>
    /// The value as a JSON answer. It's serialized now, inside the call, so a value that can't be fails the call (and
    /// rolls it back) rather than an answer the server has already committed to.
    /// </summary>
    IResult Result(object? value, int statusCode = StatusCodes.Status200OK);

    ValueTask<T?> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken = default);
}
