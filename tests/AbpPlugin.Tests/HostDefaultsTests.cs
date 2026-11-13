using AbpPlugin.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AbpPlugin.Tests;

/// <summary>
/// The host's defaults for one server, which an application replaces by registering its own: limits counted in memory,
/// recurring jobs' slots claimed once, and services plugins get unless the application exposes its own.
/// </summary>
public sealed class HostDefaultsTests
{
    [Fact]
    public async Task A_public_endpoints_limit_takes_up_to_its_max_in_each_window()
    {
        InMemoryPluginCallLimits limits = new();
        TimeSpan window = TimeSpan.FromMilliseconds(300);
        Assert.True(await limits.TryTakeAsync("acme.sample:hook", 2, window));
        Assert.True(await limits.TryTakeAsync("acme.sample:hook", 2, window));
        Assert.False(await limits.TryTakeAsync("acme.sample:hook", 2, window));
        Assert.True(await limits.TryTakeAsync("acme.other:hook", 2, window), "each key counts on its own");
        await Task.Delay(window + TimeSpan.FromMilliseconds(100));
        Assert.True(await limits.TryTakeAsync("acme.sample:hook", 2, window), "a new window starts afresh");
        Assert.False(await new InMemoryPluginCallLimits().TryTakeAsync("none", 0, window), "a max of none takes none");
    }

    [Fact]
    public async Task A_recurring_jobs_slot_is_claimed_once()
    {
        OneServerPluginJobSlots slots = new();
        DateTime nine = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
        Assert.True(await slots.TryClaimAsync("acme.sample:Report", nine));
        Assert.False(await slots.TryClaimAsync("acme.sample:Report", nine));
        Assert.False(await slots.TryClaimAsync("acme.sample:Report", nine.AddHours(-1)), "an earlier slot isn't run late");
        Assert.True(await slots.TryClaimAsync("acme.sample:Report", nine.AddHours(1)));
        Assert.True(await slots.TryClaimAsync("acme.sample:Other", nine));
    }

    [Fact]
    public void The_applications_registrations_win_over_the_defaults()
    {
        ServiceCollection services = new();
        services.AddSingleton<IPluginCallLimits, NoLimits>();
        services.AddPluginHostDefaults();
        Assert.IsType<NoLimits>(services.BuildServiceProvider().GetRequiredService<IPluginCallLimits>());
        Assert.IsType<OneServerPluginJobSlots>(new ServiceCollection().AddPluginHostDefaults().BuildServiceProvider().GetRequiredService<IPluginJobSlots>());
    }

    [Fact]
    public void A_fallback_is_exposed_only_when_the_application_exposes_none_of_its_type()
    {
        PluginHostServiceOptions options = new();
        options.ExposeIfNone<IOutboundHttp>((_, _) => throw new InvalidOperationException());
        options.ExposeIfNone<IEventPublisher>((_, _) => throw new InvalidOperationException());
        options.Expose<IEventPublisher>((_, _) => throw new InvalidOperationException());

        Assert.Equal([typeof(IEventPublisher), typeof(IOutboundHttp)], options.InEffect().Select(e => e.ServiceType).ToArray());
        Assert.Single(options.InEffect(), e => e.ServiceType == typeof(IEventPublisher));
    }

    private sealed class NoLimits : IPluginCallLimits
    {
        public Task<bool> TryTakeAsync(string key, int max, TimeSpan window, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
