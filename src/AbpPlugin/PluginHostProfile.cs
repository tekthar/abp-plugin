namespace AbpPlugin.Hosting;

/// <summary>
/// What the application hosting plugins tells the plugin host: its name in messages, the manifest sections
/// it reads, the names its shared assemblies had before, the release keys built into it, and which plugins are its
/// own. The host application sets it once, before any plugin is read. It's static, since manifests and packages are
/// read where no services are at hand.
/// </summary>
public sealed class PluginHostProfile
{
    private static volatile PluginHostProfile _current = new();

    /// <summary>The host application's profile; a neutral one (no sections, no former names, no own plugins) until set.</summary>
    public static PluginHostProfile Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The application's name in messages, as "only Acme's release key signs it".</summary>
    public string ProductName { get; init; } = "the host";

    /// <summary>
    /// The configuration key the application turns safe mode on with (<see cref="PluginHostOptions.SafeMode"/>, which it
    /// binds), named in the refusals safe mode causes; none when it has none.
    /// </summary>
    public string? SafeModeSetting { get; init; }

    /// <summary>The Data Protection purpose plugins' secrets are protected under; changing it loses the secrets kept.</summary>
    public string SecretsPurpose { get; init; } = "AbpPlugin.Secrets";

    /// <summary>
    /// Where the application's operators set a host setting besides its configuration key, said after the key in a
    /// message ("; in the chart, plugins.allowUnsigned"), or nothing.
    /// </summary>
    public IReadOnlyDictionary<string, string> SettingPlaces { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>A setting's configuration key, with where else operators set it.</summary>
    public string Setting(string key) => SettingPlaces.TryGetValue(key, out string? place) ? key + place : key;

    /// <summary>
    /// The kinds of entry the application's plugins have besides AbpPlugin.Sdk's <c>PluginEntry</c>, each run through its
    /// bridge.
    /// </summary>
    public IReadOnlyList<IPluginEntryRunner> EntryRunners { get; init; } = [];

    /// <summary>The manifest sections the application reads, checked in this order when a manifest is loaded.</summary>
    public IReadOnlyList<IPluginManifestSection> ManifestSections { get; init; } = [];

    /// <summary>
    /// The assemblies, by name prefix, that the application shares with plugins and whose types and members a plugin's
    /// code is checked against, as "Acme.". Only the host's own assemblies among them are checked.
    /// </summary>
    public IReadOnlyList<string> SharedAssemblyPrefixes { get; init; } = [];

    /// <summary>Shared assemblies by a name they had before, and their name now: a plugin built against one is told to rebuild.</summary>
    public IReadOnlyDictionary<string, string> FormerAssemblies { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// A type a plugin uses from a shared assembly (assembly, full name) that moved since: its name now and the assembly
    /// it's in now; null when it didn't move.
    /// </summary>
    public Func<string, string, (string Now, string Assembly)?> MovedType { get; init; } = (_, _) => null;

    /// <summary>The attribute by which a plugin's assembly says it falls back on older hosts (namespace, name); none when empty.</summary>
    public (string Namespace, string Name) FallsBackAttribute { get; init; } = ("", "");

    /// <summary>
    /// The refusal of a plugin built against former names (key, version, what it lacks as named, all of it), or null
    /// for the general one; the application words its own renames.
    /// </summary>
    public Func<string, string, string, IReadOnlyList<string>, string?> FormerNamesRefusal { get; init; } = (_, _, _, _) => null;

    /// <summary>
    /// Plugins' keys before app IDs, and their app IDs: a former key is still read for one release (an
    /// installed package, a URL, a configuration section, a tool's name, a module a plugin requires).
    /// </summary>
    public IReadOnlyDictionary<string, string> FormerKeys { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The application's modules a plugin may require, by their former names, and their names now.</summary>
    public IReadOnlyDictionary<string, string> FormerModules { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Schemas some plugins keep for good, by app ID, rather than the one their key gives.</summary>
    public IReadOnlyDictionary<string, string> PinnedSchemas { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Joins the application's own work to a plugin call's unit of work while the call runs, so the call's
    /// changes and the application's are one transaction: what <c>using</c> ends, or null when there's nothing to join.
    ///
    /// </summary>
    public Func<IDisposable?> JoinUnitsOfWork { get; init; } = () => null;

    /// <summary>
    /// How a plugin's class built on the application's SDK says whether its calls are one unit of work with the server,
    /// or null when it doesn't say.
    /// </summary>
    public Func<Type, bool?> TransactionalMark { get; init; } = _ => null;

    /// <summary>Release keys built into the application (public keys, PEM or base64), trusted beside <c>PluginHost:ReleaseKeys</c>.</summary>
    public IReadOnlyList<string> ReleaseKeys { get; init; } = [];

    /// <summary>
    /// Whether a plugin key is under one of the application's own publishers, whose packages only a release key signs:
    /// anyone can name a plugin so, so a publisher's key or the admins' leave doesn't do.
    /// </summary>
    public Func<string, bool> IsOwn { get; init; } = _ => false;
}

/// <summary>
/// A section of plugins' manifests that the host application reads: its name, and the check a manifest's
/// section must pass when the manifest is loaded. The host keeps sections as JSON (<see cref="PluginManifest.Section{T}"/>).
/// </summary>
public interface IPluginManifestSection
{
    /// <summary>Its name in the manifest, such as "ui".</summary>
    string Name { get; }

    /// <summary>Throws <see cref="PluginException"/>, with what to change, when the manifest's section isn't right.</summary>
    void Check(PluginManifest manifest);
}
