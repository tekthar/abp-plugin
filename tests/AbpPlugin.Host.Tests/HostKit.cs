using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using AbpPlugin.Packaging;
using AbpPlugin.SampleHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace AbpPlugin.Host.Tests;

/// <summary>The host test classes, run one after another: each starts the sample host on a database and folder of its own.</summary>
[CollectionDefinition(Name)]
public sealed class HostCollection : ICollectionFixture<SamplePackages>
{
    public const string Name = "AbpPlugin host";
}

/// <summary>
/// The sample plugin (acme.abp-sample, built on AbpPlugin.Sdk alone) as a publisher ships it: built at 0.1.0 and 0.2.0
/// with the build targets, signed with a key of the run's own, and zipped; and as it mustn't come: unsigned, and changed
/// after signing. Built once for every test class.
/// </summary>
public sealed class SamplePackages : IAsyncLifetime
{
    public const string Key = "acme.abp-sample";

    /// <summary>Its web part's remote name (plugin.template.json's <c>web</c>).</summary>
    public const string WebRemote = "acme_abp_sample";

    private readonly string _work = Directory.CreateTempSubdirectory("abp-plugin-packages-").FullName;

    public string PublicKey { get; private set; } = "";

    public string V010 => Path.Combine(_work, "acme.abp-sample-0.1.0.zip");

    public string V020 => Path.Combine(_work, "acme.abp-sample-0.2.0.zip");

    public string Unsigned => Path.Combine(_work, "acme.abp-sample-unsigned.zip");

    public string Changed => Path.Combine(_work, "acme.abp-sample-changed.zip");

    /// <summary>0.1.0 signed by a key no site trusts.</summary>
    public string Stranger => Path.Combine(_work, "acme.abp-sample-stranger.zip");

    public async Task InitializeAsync()
    {
        (string privatePem, string publicPem, _) = PluginSignature.CreateKey();
        PublicKey = publicPem;
        string v010 = await BuildAsync("0.1.0");
        string v020 = await BuildAsync("0.2.0");
        AddWebPart(v010, "0.1.0");
        AddWebPart(v020, "0.2.0");

        ZipFile.CreateFromDirectory(v010, Unsigned);
        string stranger = Copy(v010, "stranger");
        PluginSignature.Sign(stranger, PluginSignature.CreateKey().PrivatePem, "Someone else");
        ZipFile.CreateFromDirectory(stranger, Stranger);

        PluginSignature.Sign(v010, privatePem, "Acme");
        PluginSignature.Sign(v020, privatePem, "Acme");
        ZipFile.CreateFromDirectory(v010, V010);
        ZipFile.CreateFromDirectory(v020, V020);

        string changed = Copy(v010, "changed");
        File.AppendAllText(Path.Combine(changed, PluginPackageFormat.Current.Manifest), " ");
        ZipFile.CreateFromDirectory(changed, Changed);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_work, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>The sample plugin built at <paramref name="version"/> into a folder of its own, as its build packs it.</summary>
    private async Task<string> BuildAsync(string version)
    {
        string package = Path.Combine(_work, $"acme.abp-sample-{version}") + Path.DirectorySeparatorChar;
        ProcessStartInfo start = new("dotnet")
        {
            ArgumentList =
            {
                "build", SampleProject(), "-c", "Release", "-nologo", "-v", "quiet",
                $"-p:PluginVersion={version}", $"-p:AbpPluginPackageDir={package}", "-p:AbpPluginZip=false",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process build = Process.Start(start)!;
        Task<string> output = build.StandardOutput.ReadToEndAsync();
        Task<string> errors = build.StandardError.ReadToEndAsync();
        await build.WaitForExitAsync();
        Assert.True(build.ExitCode == 0, $"The sample plugin didn't build at {version}:\n{await output}\n{await errors}");
        Assert.True(File.Exists(Path.Combine(package, PluginPackageFormat.Current.Manifest)), $"No manifest in {package}");
        return package.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// The web part the tests read, in the built package's <c>web/</c>: an entry naming its version, a chunk and the
    /// federation's manifest. The sample's own (samples/AbpPlugin.Sample/web) is built only where Node is, and the tests
    /// check the server, not the bundle; its manifest names it already.
    /// </summary>
    private static void AddWebPart(string package, string version)
    {
        string web = Path.Combine(package, "web");
        Directory.CreateDirectory(Path.Combine(web, "assets"));
        File.WriteAllText(Path.Combine(web, "remoteEntry.js"), $"export const version = \"{version}\";\n");
        File.WriteAllText(Path.Combine(web, "assets", "chunk.js"), "export default 1;\n");
        File.WriteAllText(Path.Combine(web, "mf-manifest.json"), "{}\n");
    }

    private string Copy(string folder, string name)
    {
        string copy = Path.Combine(_work, name);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(copy, Path.GetRelativePath(folder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return copy;
    }

    /// <summary>The sample plugin's project, found up from the tests' folder.</summary>
    private static string SampleProject()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string path = Path.Combine(folder.FullName, "samples", "AbpPlugin.Sample");
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        throw new DirectoryNotFoundException("The sample plugin's project isn't above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// The sample host on a database and data folder of its own, signed in as one account. Needs Postgres: localhost:5432
/// (postgres/postgres), or the server ABP_PLUGIN_TEST_POSTGRES names.
/// </summary>
public sealed class SampleHost : IAsyncDisposable
{
    public static readonly string Server =
        Environment.GetEnvironmentVariable("ABP_PLUGIN_TEST_POSTGRES") is { Length: > 0 } server ? server : "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    public static readonly Guid Admin = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _data = Directory.CreateTempSubdirectory("abp-plugin-host-").FullName;

    public SampleHost()
    {
        Database = "abp_plugin_host_" + Guid.NewGuid().ToString("N")[..12];
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"{Server};Database={Database}");
        Environment.SetEnvironmentVariable("Sample__DataRoot", _data);
        // The operator's word that admins may allow unsigned plugins, which the sample's settings leave off.
        Environment.SetEnvironmentVariable("PluginHost__AdminsMayAllowUnsigned", "true");
        _factory = new WebApplicationFactory<Program>();
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add(SampleAccount.Header, Admin.ToString());
    }

    public string Database { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => _factory.Services;

    /// <summary>A client that signs in as nobody.</summary>
    public HttpClient AnonymousClient() => _factory.CreateClient();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HttpResponseMessage> UploadAsync(string url, string zip)
    {
        await using FileStream file = File.OpenRead(zip);
        using StreamContent body = new(file);
        return await Client.PostAsync(url, body);
    }

    public async Task<SampleAdmin.PluginRow> InstalledAsync(string url, string zip)
    {
        HttpResponseMessage response = await UploadAsync(url, zip);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<SampleAdmin.PluginRow>(text, Json)!;
    }

    public async Task<string> RefusedAsync(string url, string zip)
    {
        HttpResponseMessage response = await UploadAsync(url, zip);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == 400, $"{url} answered {(int)response.StatusCode}, not 400: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("error").GetString()!;
    }

    public async Task TrustAsync(string publicKey)
    {
        HttpResponseMessage response = await Client.PostAsJsonAsync("/admin/publishers", new { name = "Acme", publicKey });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Whether the database has the schema.</summary>
    public async Task<bool> HasSchemaAsync(string schema)
    {
        await using NpgsqlConnection connection = new($"{Server};Database={Database}");
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select count(*) from information_schema.schemata where schema_name = @schema", connection);
        command.Parameters.AddWithValue("schema", schema);
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using (NpgsqlConnection connection = new($"{Server};Database=postgres"))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand drop = new($"drop database if exists {Database} with (force)", connection);
            await drop.ExecuteNonQueryAsync();
        }

        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A plugin's assembly the process still has open: the folder is a temporary one.
        }
    }
}
