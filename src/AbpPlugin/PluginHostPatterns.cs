namespace AbpPlugin.Hosting;

/// <summary>How long one of the plugin host's patterns may take on one input before it gives up.</summary>
internal static class PluginHostPatterns
{
    public const int TimeoutMilliseconds = 1000;
}
