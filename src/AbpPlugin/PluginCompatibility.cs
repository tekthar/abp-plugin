using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AbpPlugin;

namespace AbpPlugin.Hosting;

/// <summary>What a server offers a plugin's code: the types and members of its own shared assemblies.</summary>
public interface IHostSurface
{
    bool HasType(string assembly, string fullName);

    /// <summary>A method or constructor (".ctor") with this name and number of parameters, or a field with this name (parameters null).</summary>
    bool HasMember(string assembly, string typeFullName, string name, int? parameters);
}

/// <summary>The running server's own assemblies, as loaded: the SDK and the contracts every plugin shares with it.</summary>
public sealed class RunningHost : IHostSurface
{
    public static readonly RunningHost Instance = new();

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static Type? Find(string assembly, string fullName)
    {
        Assembly? host = System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == assembly);
        if (host is null)
        {
            try
            {
                host = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(assembly));
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        return host.GetType(fullName, throwOnError: false);
    }

    public bool HasType(string assembly, string fullName) => Find(assembly, fullName) is not null;

    public bool HasMember(string assembly, string typeFullName, string name, int? parameters)
    {
        if (Find(assembly, typeFullName) is not { } type)
        {
            return false;
        }

        // An interface's members include the ones it inherits; a class's, its base classes' (BindingFlags reach them).
        IEnumerable<Type> types = type.IsInterface ? [type, .. type.GetInterfaces()] : [type];
        if (parameters is null)
        {
            return types.Any(t => t.GetField(name, All) is not null);
        }

        return name == ".ctor"
            ? type.GetConstructors(All).Any(c => c.GetParameters().Length == parameters)
            : types.SelectMany(t => t.GetMethods(All)).Any(m => m.Name == name && m.GetParameters().Length == parameters);
    }
}

/// <summary>
/// Whether a plugin's code fits a server: every type and member it uses from the application's shared assemblies (the SDK
/// and the contracts, which it shares with the host by name) is one the server has. Read from the plugin's metadata
/// without loading it, at install and upgrade, so a plugin built against newer contracts is refused with what it lacks,
/// rather than failing with MissingMethodException when the method that uses it first runs.
/// </summary>
public static class PluginCompatibility
{
    /// <summary>What a plugin's assemblies in <paramref name="folder"/> use that <paramref name="host"/> hasn't got, as "Type.Member (Assembly)".</summary>
    public static List<string> Missing(string folder, IHostSurface host)
    {
        SortedSet<string> missing = new(StringComparer.Ordinal);
        foreach (string dll in Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly))
        {
            // The host's own assemblies packed beside the plugin aren't the plugin's code: the host's are used instead.
            if (PluginLoadContext.IsHostAssembly(Path.GetFileNameWithoutExtension(dll)))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(dll);
            using PEReader pe = new(stream);
            if (!pe.HasMetadata)
            {
                continue;
            }

            Check(pe.GetMetadataReader(), host, missing);
        }

        return [.. missing];
    }

    /// <summary>
    /// The name an assembly a plugin references has now, when the host application renamed it (its profile's former
    /// assemblies); else null. A plugin built against one is told to rebuild against the new name, rather than failing
    /// when the code that uses it first runs.
    /// </summary>
    public static string? RenamedTo(string reference) => PluginHostProfile.Current.FormerAssemblies.GetValueOrDefault(reference);

    /// <summary>The name a type a plugin uses from a shared assembly has now, when it moved to another assembly; else null.</summary>
    public static string? MovedTo(string assembly, string fullName) => PluginHostProfile.Current.MovedType(assembly, fullName)?.Now;

    private static void Check(MetadataReader md, IHostSurface host, SortedSet<string> missing)
    {
        foreach (AssemblyReferenceHandle handle in md.AssemblyReferences)
        {
            string reference = md.GetString(md.GetAssemblyReference(handle).Name);
            if (RenamedTo(reference) is { } now)
            {
                missing.Add($"{reference}, now {now}: rebuild the plugin against {now}");
            }
        }

        foreach (TypeReferenceHandle handle in md.TypeReferences)
        {
            if (Shared(md, handle) is ({ } assembly, { } fullName) && !host.HasType(assembly, fullName))
            {
                missing.Add(PluginHostProfile.Current.MovedType(assembly, fullName) is { } moved
                    ? $"{fullName}, now {moved.Now}: rebuild the plugin against {moved.Assembly}"
                    : $"{fullName} ({assembly})");
            }
        }

        foreach (MemberReferenceHandle handle in md.MemberReferences)
        {
            MemberReference member = md.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference || Shared(md, (TypeReferenceHandle)member.Parent) is not ({ } assembly, { } typeName))
            {
                continue; // a member of a generic instantiation or of the plugin's own types: not checked here
            }

            string name = md.GetString(member.Name);
            int? parameters = member.GetKind() == MemberReferenceKind.Method ? ParameterCount(md, member.Signature) : null;
            if (host.HasType(assembly, typeName) && !host.HasMember(assembly, typeName, name, parameters))
            {
                missing.Add($"{typeName}.{name} ({assembly})");
            }
        }
    }

    /// <summary>The host assembly and runtime name ("Outer+Inner") of a type the plugin uses from the shared assemblies (the profile's prefixes), or nulls.</summary>
    private static (string? Assembly, string? FullName) Shared(MetadataReader md, TypeReferenceHandle handle)
    {
        TypeReference type = md.GetTypeReference(handle);
        string name = md.GetString(type.Name);
        string ns = md.GetString(type.Namespace);
        string full = ns.Length > 0 ? $"{ns}.{name}" : name;
        EntityHandle scope = type.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
        {
            // A nested type: its outer type's, then "+" and its own name.
            (string? assembly, string? outer) = Shared(md, (TypeReferenceHandle)scope);
            return outer is null ? (null, null) : (assembly, $"{outer}+{name}");
        }

        if (scope.Kind != HandleKind.AssemblyReference)
        {
            return (null, null);
        }

        string reference = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        return PluginHostProfile.Current.SharedAssemblyPrefixes.Any(p => reference.StartsWith(p, StringComparison.Ordinal))
            && PluginLoadContext.IsHostAssembly(reference) ? (reference, full) : (null, null);
    }

    private static int ParameterCount(MetadataReader md, BlobHandle signature)
    {
        BlobReader blob = md.GetBlobReader(signature);
        SignatureHeader header = blob.ReadSignatureHeader();
        if (header.IsGeneric)
        {
            blob.ReadCompressedInteger(); // the number of generic parameters
        }

        return blob.ReadCompressedInteger();
    }

    /// <summary>
    /// Whether what a plugin lacks includes an assembly or type renamed since it was built: the server is newer than the
    /// plugin, not older, so falling back on an older server (<see cref="Tolerates"/>) doesn't help: a plugin that falls
    /// back would be taken, and fail when it first asked for what was renamed.
    /// </summary>
    public static bool BuiltBeforeARename(IEnumerable<string> missing) =>
        missing.Any(m => m.Contains(": rebuild the plugin against ", StringComparison.Ordinal));

    /// <summary>Whether a plugin's assembly says it falls back on older servers (the profile's <see cref="PluginHostProfile.FallsBackAttribute"/>).</summary>
    public static bool Tolerates(string assemblyPath)
    {
        (string ns, string name) = PluginHostProfile.Current.FallsBackAttribute;
        if (name.Length == 0 || !File.Exists(assemblyPath))
        {
            return false;
        }

        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader pe = new(stream);
        MetadataReader md = pe.GetMetadataReader();
        foreach (CustomAttributeHandle handle in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            EntityHandle constructor = md.GetCustomAttribute(handle).Constructor;
            if (constructor.Kind == HandleKind.MemberReference && md.GetMemberReference((MemberReferenceHandle)constructor).Parent is { Kind: HandleKind.TypeReference } parent)
            {
                TypeReference type = md.GetTypeReference((TypeReferenceHandle)parent);
                if (md.GetString(type.Namespace) == ns && md.GetString(type.Name) == name)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The refusal: what the plugin uses that the server hasn't got, the first few named. One built against names the
    /// host application had before is worded by the application (its profile); else the server is older than the plugin.
    /// </summary>
    public static string Refusal(string key, string version, IReadOnlyList<string> missing)
    {
        string named = string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? $" and {missing.Count - 5} more" : "");
        PluginHostProfile profile = PluginHostProfile.Current;
        return profile.FormerNamesRefusal(key, version, named, missing)
            ?? $"'{key}' {version} uses what this server hasn't got: {named}. It was built against a newer {profile.ProductName}: upgrade the server, "
               + "or install a version of the plugin built for this one.";
    }
}
