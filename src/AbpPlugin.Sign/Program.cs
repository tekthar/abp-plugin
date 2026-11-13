using AbpPlugin.Packaging;

// abp-plugin-sign: makes signing keys, and signs and checks plugin packages in AbpPlugin's own format
// (PluginSigningTool has the commands). An application whose packages have names of their own ships its own tool:
// it sets PluginPackageFormat.Current, then calls PluginSigningTool.Run with its command's name.
return PluginSigningTool.Run(args, "abp-plugin-sign");
