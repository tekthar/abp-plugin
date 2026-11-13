using System.Text.Json;
using Microsoft.AspNetCore.Http;
using AbpPlugin;

namespace AbpPlugin.Hosting;

/// <summary>Per-plugin serializer options, so type metadata caches are released with the plugin.</summary>
internal sealed class PluginJson : IPluginJson
{
    // A resolver instance of our own: System.Text.Json shares caching contexts between options with
    // equal settings, which would let the host's long-lived options pin this plugin's types.
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public IResult Result(object? value, int statusCode = StatusCodes.Status200OK) =>
        Results.Text(JsonSerializer.SerializeToUtf8Bytes(value, value?.GetType() ?? typeof(object), _options), "application/json; charset=utf-8", statusCode);

    public ValueTask<T?> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken = default) =>
        JsonSerializer.DeserializeAsync<T>(request.Body, _options, cancellationToken);
}
