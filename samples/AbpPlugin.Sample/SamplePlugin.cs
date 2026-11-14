using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AbpPlugin.Sample;

/// <summary>
/// A plugin built on AbpPlugin.Sdk alone: every part of the plugin runtime once, so a host shows it
/// runs them. <c>GET ping</c> says what it found; <c>POST visit</c> records a visit, keeps a setting and a file, and
/// raises an event of its own, whose handler counts it in another setting.
/// </summary>
public sealed class SamplePlugin : PluginEntry
{
    public override void ConfigureServices(IPluginServices services) => services
        .AddDbContext<SampleDb>()
        .AddSeeder<SampleSeeder>()
        .AddLocalEventHandler<VisitRecorded, VisitHeard>()
        .AddEndpoint<Ping>("GET", "ping")
        .AddEndpoint<RecordVisit>("POST", "visit")
        .AddHealthCheck<SampleCheck>("sample");
}

public sealed class Visit
{
    public Guid Id { get; set; }

    public string Note { get; set; } = "";
}

public sealed class SampleDb(DbContextOptions<SampleDb> options) : PluginDbContext(options)
{
    public DbSet<Visit> Visits => Set<Visit>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<Visit>(b =>
    {
        b.ToTable("visits");
        b.Property(v => v.Id).ValueGeneratedNever();
        b.Property(v => v.Note).HasMaxLength(200).IsRequired();
    });
}

/// <summary>For dotnet ef: the schema the host gives acme.abp-sample.</summary>
public sealed class SampleDbFactory : IDesignTimeDbContextFactory<SampleDb>
{
    public SampleDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SampleDb>()
            .UseNpgsql("Host=localhost;Database=design", npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", PluginSchemaNames.For("acme.abp-sample")))
            .UsePluginSchema(PluginSchemaNames.For("acme.abp-sample"))
            .Options);
}

/// <summary>The first visit, so a fresh install has one: run again on upgrade, it adds none.</summary>
public sealed class SampleSeeder(SampleDb db) : IPluginSeeder
{
    public static readonly Guid First = Guid.Parse("6f1c9b1e-5d1a-4e83-9c2b-2a2f0c7b1e01");

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!await db.Visits.AnyAsync(v => v.Id == First, cancellationToken))
        {
            db.Visits.Add(new Visit { Id = First, Note = "seeded" });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

public sealed record VisitRecorded(Guid VisitId);

/// <summary>Counts the visits it hears of, in a setting: the event reached a plugin built on AbpPlugin.Sdk.</summary>
public sealed class VisitHeard(IPluginSettings settings) : ILocalEventHandler<VisitRecorded>
{
    public async Task HandleAsync(VisitRecorded eventData, CancellationToken cancellationToken)
    {
        int heard = await settings.GetAsync<int>("heard", cancellationToken: cancellationToken);
        await settings.SetAsync("heard", heard + 1, cancellationToken: cancellationToken);
    }
}

public sealed class RecordVisit(SampleDb db, IPluginSettings settings, IPluginFiles files, ISecretProtector secrets, IEventPublisher events, IPluginJson json)
    : IPluginEndpoint
{
    public async Task<IResult> HandleAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        string note = request.Query("note") is { Length: > 0 } given ? given : "visit";
        Visit visit = new() { Id = Guid.NewGuid(), Note = note };
        db.Visits.Add(visit);
        await db.SaveChangesAsync(cancellationToken);
        await settings.SetAsync("last", note, cancellationToken: cancellationToken);
        await settings.SetAsync("secret", secrets.Protect(note), cancellationToken: cancellationToken);
        await files.SaveAsync("last.txt", new MemoryStream(Encoding.UTF8.GetBytes(note)), cancellationToken);
        await events.PublishAsync(new VisitRecorded(visit.Id), cancellationToken);
        return json.Result(new { visit.Id });
    }
}

public sealed class Ping(SampleDb db, IPluginSettings settings, IPluginFiles files, ISecretProtector secrets, PluginContext context, IPluginJson json)
    : IPluginEndpoint
{
    public async Task<IResult> HandleAsync(PluginRequest request, CancellationToken cancellationToken)
    {
        string? file = null;
        await using (Stream? stream = await files.OpenReadAsync("last.txt", cancellationToken))
        {
            if (stream is not null)
            {
                file = await new StreamReader(stream).ReadToEndAsync(cancellationToken);
            }
        }

        string? secret = await settings.GetAsync<string>("secret", cancellationToken: cancellationToken);
        return json.Result(new
        {
            context.Key,
            context.Version,
            context.Schema,
            Visits = await db.Visits.CountAsync(cancellationToken),
            Seeded = await db.Visits.AnyAsync(v => v.Id == SampleSeeder.First, cancellationToken),
            Last = await settings.GetAsync<string>("last", cancellationToken: cancellationToken),
            Heard = await settings.GetAsync<int>("heard", cancellationToken: cancellationToken),
            File = file,
            Secret = secret is null ? null : secrets.Unprotect(secret),
            Caller = request.User.Identity?.IsAuthenticated == true,
        });
    }
}

public sealed class SampleCheck(SampleDb db) : IPluginHealthCheck
{
    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken) =>
        await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy("its table answers") : HealthCheckResult.Unhealthy("no database");
}
