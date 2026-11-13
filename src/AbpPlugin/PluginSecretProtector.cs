using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using AbpPlugin;

namespace AbpPlugin.Hosting;

/// <summary>Data Protection with a purpose per plugin, so a plugin can only decrypt what it encrypted itself.</summary>
internal sealed class PluginSecretProtector(IDataProtectionProvider provider, string pluginKey) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector(PluginHostProfile.Current.SecretsPurpose, pluginKey);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
