using System.Net;
using AbpPlugin.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AbpPlugin.Tests;

/// <summary>
/// A plugin's public endpoints: only paths under public/ take anyone's call, a plugin's other endpoints can't claim that
/// prefix, and the host caps a public call's body and how often an address and the plugin are called.
/// </summary>
public class PublicCallsTests
{
    private sealed class Nothing : IPluginEndpoint
    {
        public Task<IResult> HandleAsync(PluginRequest request, CancellationToken cancellationToken) => Task.FromResult(Results.NoContent());
    }

    private static DefaultHttpContext Call(string address, long? length = null)
    {
        DefaultHttpContext http = new();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        http.Request.Method = "POST";
        http.Request.ContentLength = length;
        return http;
    }

    [Theory]
    [InlineData("public", true)]
    [InlineData("public/hooks", true)]
    [InlineData("/Public/hooks/1", true)]
    [InlineData("publications", false)]
    [InlineData("summary/public", false)]
    [InlineData(null, false)]
    public void Only_paths_under_public_are_public(string? path, bool expected) => Assert.Equal(expected, PublicCalls.IsPublic(path));

    [Fact]
    public void A_public_endpoints_pattern_is_matched_without_the_prefix() => Assert.Equal("hooks/1", PublicCalls.Inner("public/hooks/1"));

    [Theory]
    [InlineData("/api/plugins/payments/public/notify", true)]
    [InlineData("/API/Plugins/payments/Public", true)]
    [InlineData("/api/plugins/payments/invoices", false)]
    [InlineData("/api/plugins/public", false)]
    [InlineData("/api/projects/public/x", false)]
    public void A_request_for_a_public_endpoint_is_known_before_sign_in(string path, bool expected) =>
        Assert.Equal(expected, PublicCalls.IsPublicRequest(new PathString(path)));

    [Fact]
    public async Task A_public_calls_body_reads_again_from_its_start_after_something_read_it_first()
    {
        DefaultHttpContext http = Call("203.0.113.5");
        http.Request.Path = "/api/plugins/payments/public/notify";
        http.Request.Body = new MemoryStream("access_token=nope&amount=10"u8.ToArray());
        PublicCalls.Prepare(http);
        _ = await new StreamReader(http.Request.Body).ReadToEndAsync(); // as sign-in reads a form body for a token
        PublicCalls.Rewind(http);
        Assert.Equal("access_token=nope&amount=10", await new StreamReader(http.Request.Body).ReadToEndAsync());
    }

    [Fact]
    public void A_signed_in_endpoint_cant_take_the_public_prefix()
    {
        PluginServiceCollection host = new(new ServiceCollection(), new PluginContext("acme.sample", "1.0.0", "plg_acme__sample", "", ""), new ServiceCollection().BuildServiceProvider());
        IPluginServices services = host; // as a plugin's entry registers
        Assert.Throws<InvalidOperationException>(() => services.AddEndpoint<Nothing>("GET", "public/ping"));

        services.AddPublicEndpoint<Nothing>("POST", "hooks").AddEndpoint<Nothing>("GET", "hooks");
        Assert.Equal([true, false], host.Registrations.Endpoints.Select(e => e.Public));
    }

    [Fact]
    public async Task A_body_over_a_megabyte_is_refused_before_anything_is_read()
    {
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, await PublicCalls.CheckAsync(Call("203.0.113.5", PublicCalls.MaxBody + 1), "payments", new InMemoryPluginCallLimits(), default));
        Assert.Equal(0, await PublicCalls.CheckAsync(Call("203.0.113.5", PublicCalls.MaxBody), "payments", new InMemoryPluginCallLimits(), default));
    }

    [Fact]
    public async Task An_address_has_so_many_calls_a_minute_and_another_address_still_gets_through()
    {
        InMemoryPluginCallLimits limits = new();
        for (int i = 0; i < PublicCalls.PerAddress; i++)
        {
            Assert.Equal(0, await PublicCalls.CheckAsync(Call("203.0.113.5"), "payments", limits, default));
        }

        DefaultHttpContext over = Call("203.0.113.5");
        Assert.Equal(StatusCodes.Status429TooManyRequests, await PublicCalls.CheckAsync(over, "payments", limits, default));
        Assert.Equal("60", over.Response.Headers.RetryAfter.ToString());
        Assert.Equal(0, await PublicCalls.CheckAsync(Call("203.0.113.6"), "payments", limits, default));
        Assert.Equal(0, await PublicCalls.CheckAsync(Call("203.0.113.5"), "another-plugin", limits, default)); // each plugin counts its own
    }

    [Fact]
    public async Task A_plugin_has_so_many_calls_a_minute_from_everyone()
    {
        InMemoryPluginCallLimits limits = new();
        for (int i = 0; i < PublicCalls.PerPlugin; i++)
        {
            Assert.Equal(0, await PublicCalls.CheckAsync(Call($"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}"), "payments", limits, default));
        }

        Assert.Equal(StatusCodes.Status429TooManyRequests, await PublicCalls.CheckAsync(Call("198.51.100.1"), "payments", limits, default));
    }
}
