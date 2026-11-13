using System.Security.Cryptography;

namespace AbpPlugin.Packaging;

/// <summary>
/// The signing tool's commands, for <c>abp-plugin-sign</c> and for an application's own tool under its own name, which
/// sets its <see cref="PluginPackageFormat"/> first and calls <see cref="Run"/>:
/// <list type="bullet">
/// <item><c>keygen &lt;folder&gt;</c>: a new key pair, private.pem (keep it secret) and public.pem (give it out);</item>
/// <item><c>sign --key &lt;private.pem&gt; --signer &lt;name&gt; &lt;plugin folder&gt;...</c>: writes the signature into each folder;</item>
/// <item><c>verify --key &lt;public.pem&gt; &lt;plugin folder&gt;...</c>: checks each folder's signature against the key;</item>
/// <item><c>keyid &lt;public.pem&gt;</c>: the key's id, as a site shows it.</item>
/// </list>
/// </summary>
public static class PluginSigningTool
{
    /// <summary>Runs one command; 0 when it did what it said, 1 when it couldn't, 2 for a usage the tool doesn't know.</summary>
    public static int Run(string[] args, string command, TextWriter? output = null, TextWriter? errors = null)
    {
        output ??= Console.Out;
        errors ??= Console.Error;
        try
        {
            return args switch
            {
                ["keygen", var folder] => KeyGen(folder, output, errors),
                ["sign", .. var rest] when Option(rest, "--key") is { } key && Option(rest, "--signer") is { } signer =>
                    Sign(File.ReadAllText(key), signer, Folders(rest), command, output, errors),
                ["verify", .. var rest] when Option(rest, "--key") is { } key => Verify(File.ReadAllText(key), Folders(rest), output),
                ["keyid", var key] => KeyId(File.ReadAllText(key), output),
                _ => Usage(command, errors),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or CryptographicException)
        {
            errors.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int KeyGen(string folder, TextWriter output, TextWriter errors)
    {
        Directory.CreateDirectory(folder);
        string privatePath = Path.Combine(folder, "private.pem");
        if (File.Exists(privatePath))
        {
            errors.WriteLine($"{privatePath} exists already: not replacing a key.");
            return 1;
        }

        (string privatePem, string publicPem, string keyId) = PluginSignature.CreateKey();
        File.WriteAllText(privatePath, privatePem);
        File.WriteAllText(Path.Combine(folder, "public.pem"), publicPem);
        output.WriteLine($"Key {keyId}: {privatePath} signs (keep it secret), {Path.Combine(folder, "public.pem")} is what sites trust.");
        return 0;
    }

    private static int Sign(string privatePem, string signer, IReadOnlyList<string> folders, string command, TextWriter output, TextWriter errors)
    {
        if (folders.Count == 0)
        {
            return Usage(command, errors);
        }

        foreach (string folder in folders)
        {
            if (!File.Exists(Path.Combine(folder, PluginPackageFormat.Current.Manifest)))
            {
                errors.WriteLine($"{folder} isn't a plugin folder (no {PluginPackageFormat.Current.Manifest}).");
                return 1;
            }

            PluginSignatureFile file = PluginSignature.Sign(folder, privatePem, signer);
            output.WriteLine($"Signed {folder} as {signer} (key {file.KeyId})");
        }

        return 0;
    }

    private static int Verify(string publicPem, IReadOnlyList<string> folders, TextWriter output)
    {
        byte[] key = PublicKey(publicPem);
        int failed = 0;
        foreach (string folder in folders)
        {
            PluginSignatureFile? file = PluginSignature.Read(folder);
            bool good = file is not null && file.KeyId == PluginSignature.KeyId(key) && PluginSignature.Verify(file, folder, key);
            output.WriteLine($"{(good ? "good" : "BAD ")} {folder}{(file is null ? " (not signed)" : "")}");
            failed += good ? 0 : 1;
        }

        return failed == 0 ? 0 : 1;
    }

    private static int KeyId(string publicPem, TextWriter output)
    {
        output.WriteLine(PluginSignature.KeyId(PublicKey(publicPem)));
        return 0;
    }

    private static byte[] PublicKey(string pem) =>
        PluginSignature.ReadPublicKey(pem) ?? throw new InvalidDataException(PluginSignature.IsPrivateKey(pem)
            ? "That's a private key: give the public one (public.pem)."
            : "That isn't an EC P-256 public key.");

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static List<string> Folders(string[] args)
    {
        List<string> folders = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            folders.Add(args[i]);
        }

        return folders;
    }

    private static int Usage(string command, TextWriter errors)
    {
        errors.WriteLine($"""
            Usage:
              {command} keygen <folder>
              {command} sign --key <private.pem> --signer <name> <plugin folder>...
              {command} verify --key <public.pem> <plugin folder>...
              {command} keyid <public.pem>
            """);
        return 2;
    }
}
