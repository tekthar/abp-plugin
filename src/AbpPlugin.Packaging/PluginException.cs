namespace AbpPlugin.Packaging;

/// <summary>
/// A plugin package the host (or a marketplace, or a build) can't take, with the reason in words a person can act on:
/// a version that isn't one, a manifest without its key. The host's own refusals derive from it.
/// </summary>
public class PluginException(string message) : Exception(message);
