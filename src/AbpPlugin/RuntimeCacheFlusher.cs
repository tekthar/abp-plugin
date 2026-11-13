using System.Reflection;
using System.Reflection.Metadata;

namespace AbpPlugin.Hosting;

/// <summary>
/// After a plugin unloads, asks every framework library to drop its reflection caches, using the same
/// [MetadataUpdateHandler] hooks .NET hot reload uses. System.Text.Json, for example, keeps a static
/// cache of compiled member accessors that would otherwise pin an unloaded plugin's types.
/// </summary>
internal static class RuntimeCacheFlusher
{
    public static void ClearCaches()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || assembly.IsCollectible)
            {
                continue;
            }

            IEnumerable<MetadataUpdateHandlerAttribute> handlers;
            try
            {
                handlers = assembly.GetCustomAttributes<MetadataUpdateHandlerAttribute>();
            }
            catch
            {
                continue;
            }

            foreach (MetadataUpdateHandlerAttribute handler in handlers)
            {
                MethodInfo? clearCache = handler.HandlerType.GetMethod(
                    "ClearCache", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Type[])]);
                try
                {
                    clearCache?.Invoke(null, [null]);
                }
                catch
                {
                    // Best effort: a handler that fails only leaves its own cache in place.
                }
            }
        }
    }
}
