namespace AbpPlugin.Packaging;

/// <summary>
/// The names a package format gives its own files, and what its signatures are made over. The host
/// application says which format its packages are in, once, before any package is read or signed
/// (<see cref="Current"/>): one with packages already out keeps the names they carry. A build, a signing tool or a
/// marketplace working for that application says the same.
/// </summary>
public sealed record PluginPackageFormat
{
    /// <summary>AbpPlugin's own names, for an application with no packages of its own yet.</summary>
    public static readonly PluginPackageFormat Default = new();

    private static volatile PluginPackageFormat _current = Default;

    /// <summary>The format packages are read, signed and checked in: the application's, or <see cref="Default"/>.</summary>
    public static PluginPackageFormat Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The manifest, at the package's root.</summary>
    public string Manifest { get; init; } = "plugin.json";

    /// <summary>Names the manifest had in packages made before, read when a package has no <see cref="Manifest"/>.</summary>
    public IReadOnlyList<string> FormerManifests { get; init; } = [];

    /// <summary>The publisher's signature, beside the manifest (<see cref="PluginSignature"/>).</summary>
    public string Signature { get; init; } = "plugin-signature.json";

    /// <summary>The mark a host writes on a package it unpacked; the digest leaves it out.</summary>
    public string Marker { get; init; } = ".plugin-package";

    /// <summary>Names the mark had before, still read, and left out of the digest as the mark is.</summary>
    public IReadOnlyList<string> FormerMarkers { get; init; } = [];

    /// <summary>
    /// Files at a package's root that the digest leaves out, besides the signature and the marks: ones added after the
    /// publisher signed, such as a marketplace's countersignature. A file named here can't be vouched for by the
    /// publisher's signature, so name only files the host doesn't run or read as the plugin's.
    /// </summary>
    public IReadOnlyList<string> Unsigned { get; init; } = [];

    /// <summary>
    /// What a signature is made over, before the digest: it says the digest is one of this format's packages, so a
    /// signature can't be used for anything else.
    /// </summary>
    public string SignedPrefix { get; init; } = "abp-plugin-signature/1\n";

    /// <summary>The file extensions a package comes in, zipped.</summary>
    public IReadOnlyList<string> PackageExtensions { get; init; } = [".zip"];

    /// <summary>The manifest's names, its own first.</summary>
    public IEnumerable<string> Manifests => [Manifest, .. FormerManifests];

    /// <summary>The manifest's path in a package's folder, by its name or a former one, or null when it has none.</summary>
    public string? ManifestIn(string folder) => Manifests.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
}
