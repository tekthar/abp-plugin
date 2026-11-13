namespace AbpPlugin;

/// <summary>
/// Outgoing HTTP for plugins: a client that refuses private and loopback addresses unless the server allows
/// a host, as the host's own webhooks, MCP servers and channels do. A plugin calling an address someone typed uses it.
/// </summary>
public interface IOutboundHttp
{
    HttpClient Client();
}
