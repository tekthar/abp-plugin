using AbpPlugin.Packaging;

namespace AbpPlugin.Tests;

/// <summary>The signing tool's commands, as abp-plugin-sign and an application's own tool run them.</summary>
public sealed class SigningToolTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("abp-plugin-signing-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    [Fact]
    public void A_key_made_signs_a_package_that_then_verifies()
    {
        string keys = Path.Combine(_work, "keys");
        string package = Path.Combine(_work, "package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, PluginPackageFormat.Current.Manifest), """{ "key": "acme.sample", "version": "1.0.0" }""");

        (int code, string said, _) = Run("keygen", keys);
        Assert.Equal(0, code);
        Assert.Contains("is what sites trust", said);
        Assert.Equal(1, Run("keygen", keys).Code);

        Assert.Equal(0, Run("sign", "--key", Path.Combine(keys, "private.pem"), "--signer", "Acme", package).Code);
        Assert.True(File.Exists(Path.Combine(package, PluginPackageFormat.Current.Signature)));
        (code, said, _) = Run("verify", "--key", Path.Combine(keys, "public.pem"), package);
        Assert.Equal((0, $"good {package}"), (code, said.Trim()));

        (code, said, _) = Run("keyid", Path.Combine(keys, "public.pem"));
        Assert.Equal(PluginSignature.Read(package)!.KeyId, said.Trim());

        File.AppendAllText(Path.Combine(package, PluginPackageFormat.Current.Manifest), " ");
        (code, said, _) = Run("verify", "--key", Path.Combine(keys, "public.pem"), package);
        Assert.Equal((1, $"BAD  {package}"), (code, said.Trim()));
    }

    [Fact]
    public void A_folder_without_a_manifest_or_a_private_key_given_as_public_is_refused()
    {
        string keys = Path.Combine(_work, "keys");
        Run("keygen", keys);
        (int code, _, string errors) = Run("sign", "--key", Path.Combine(keys, "private.pem"), "--signer", "Acme", _work);
        Assert.Equal(1, code);
        Assert.Contains("isn't a plugin folder", errors);

        (code, _, errors) = Run("keyid", Path.Combine(keys, "private.pem"));
        Assert.Equal(1, code);
        Assert.Contains("That's a private key", errors);
    }

    [Fact]
    public void An_unknown_command_shows_the_usage_under_the_tools_own_name()
    {
        (int code, _, string errors) = Run("sing");
        Assert.Equal(2, code);
        Assert.Contains("acme-plugin-sign keygen <folder>", errors);
    }

    private static (int Code, string Output, string Errors) Run(params string[] args)
    {
        StringWriter output = new();
        StringWriter errors = new();
        int code = PluginSigningTool.Run(args, "acme-plugin-sign", output, errors);
        return (code, output.ToString(), errors.ToString());
    }
}
