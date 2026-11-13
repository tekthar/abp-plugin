# The package format

What a plugin package is, what its manifest says, and how its signature is made and checked. `AbpPlugin.Packaging`
holds the format with .NET's own types only, so a marketplace, a build or a signing tool reads a package exactly as the
host does. The names below are AbpPlugin's defaults; an application whose packages are already out gives its own
([an application's own names](#an-applications-own-names)).

## A package

A package is a plugin's folder, zipped with the folder's contents at the zip's root (no folder of its own inside). A
host takes it as the folder or as the zip; the zip's extension is one of `PluginPackageFormat.PackageExtensions`
(`.zip`).

| Path | What |
|---|---|
| `plugin.json` | the manifest. Required |
| `plugin-signature.json` | the publisher's signature, when signed |
| the assembly `server.assembly` names, and what it needs | the plugin's code, loaded from here |
| `web/` | its web bundle, for the host application's front end |
| `mobile/` | its phone bundles |
| `messages/{language}.json` | its texts by language (nested JSON objects of strings) |
| `docs/` | its docs: Markdown pages and the images they show |
| `.plugin-package` | not in a package: the mark a server writes in a folder it unpacked, holding the package's SHA-256 |

`AbpPlugin.Build` makes this layout after a plugin's build ([building plugins](building-plugins.md)).

## The manifest

```json
{
  "key": "acme.ratings",
  "name": "Ratings",
  "version": "1.2.0",
  "kind": "plugin",
  "system": false,
  "vendor": { "name": "Acme", "url": "https://acme.example" },
  "requires": { "my-app": ">=2.0.0 <3.0.0", "acme.orders": ">=1.0.0" },
  "server": { "assembly": "Acme.Ratings.dll", "entry": "Acme.Ratings.RatingsPlugin" },
  "web": { "remoteName": "acme_ratings", "entry": "remoteEntry.js" },
  "projects.feature@1": [ { "key": "ratings", "title": "Ratings" } ]
}
```

| Field | What |
|---|---|
| `key` | the app ID: the publisher's slug, a dot and the app's slug. Required |
| `name` | what people call it. Required |
| `version` | one to three numbers, `major.minor.patch`. Required |
| `kind` | `plugin` by default; kept for the host application |
| `system` | a system plugin: ships with the application, needs a release key's signature, can be disabled but not removed |
| `vendor` | `name` and `url` of who made it |
| `requires` | what it runs on: the host application's modules or other plugins by app ID, each with a range |
| `server` | `assembly`, the file of its code, and `entry`, the full name of its `PluginEntry` class. A plugin without it fails to load |
| `web` | `remoteName` and `entry` of its web bundle, for the host application's front end |
| anything else | kept as JSON, by name: contributions to extension points (`"projects.feature@1": [ ... ]`, an array of items), and sections the host application reads (its `PluginHostProfile.ManifestSections`, checked when the manifest loads) |

The manifest may have comments and trailing commas. The host keeps it whole, as installed, in `plugin_manifests`.

`PluginPackageManifest.Parse` (or `Read(folder)`) reads the part a marketplace or a build checks before a package
reaches a host: the key, the version and `requires`. A manifest without a key or a version is refused, saying which.

## App IDs and schemas

A slug is 1 to 64 lowercase letters, digits and single hyphens, starting and ending with a letter or a digit
(`AppIds.IsSlug`); an app ID is two slugs and a dot (`AppIds.IsAppId`), at most 129 characters. A slug holds no `_`,
no `.` and no `--`.

A plugin's tables are in the schema `PluginSchemaNames.For(key)`: `plg_` and the key, the dot written `__` and each
hyphen `_`. `acme.ratings` is `plg_acme__ratings`, `acme.jira-sync` is `plg_acme__jira_sync`, and no two app IDs share
a schema. A name over 63 bytes is `plh_`, the first 46 characters, `_` and the first 12 hex digits of the app ID's
SHA-256. A host may pin a plugin to a schema it had before (`PluginHostProfile.PinnedSchemas`).

## Versions and ranges

`SemanticVersion.Parse` reads one to three numbers; missing ones are 0, and a prerelease or build part (`-beta.1`,
`+abc`) is dropped, so `1.0.0-beta.1` counts as `1.0.0`. A range is comparators separated by spaces, all of which must
hold: `>=`, `<=`, `>`, `<`, `=`, or a bare version for `=`.

```csharp
SemanticVersion.Parse("1.2.3").Satisfies(">=1.0.0 <2.0.0");   // true
```

## The signature

A package is signed with ECDSA on the P-256 curve with SHA-256 (`ECDSA-P256-SHA256`), built into .NET on every
platform.

1. **The listing.** Every file in the folder, at any depth, except these at its root: the signature
   (`plugin-signature.json`), the mark (`.plugin-package`) and the format's former marks, and the format's `Unsigned`
   files (none by default): its path relative to the folder, with `/`, and the SHA-256 of its bytes in lowercase hex. Sorted by path,
   ordinal. `PluginSignature.Listing(folder)` gives it, so a client can check files one by one.
2. **The digest.** For each file in the listing, its path, `\n`, its hash, `\n`, all joined; the SHA-256 of that text
   in UTF-8, in lowercase hex. `PluginSignature.Digest(folder)`.
3. **What's signed.** The UTF-8 bytes of the format's signed prefix and the digest:
   `abp-plugin-signature/1\n` followed by the 64 hex digits. The prefix says the digest is one of this format's
   packages, so the signature can't be used for anything else.
4. **The signature.** ECDSA over those bytes with SHA-256, in .NET's default encoding (IEEE P1363: `r` then `s`, 32
   bytes each), as base64.

The manifest is a file like any other, so a changed version or key breaks the signature too. A file changed, added or
removed after signing breaks it.

`plugin-signature.json`, beside the manifest:

```json
{
  "version": 1,
  "algorithm": "ECDSA-P256-SHA256",
  "keyId": "3f2a9c0d41b7e865",
  "signer": "Acme",
  "digest": "9b1c…(64 hex digits)",
  "signature": "…(64 bytes as base64)",
  "signedAt": "2026-11-14T09:00:00+00:00"
}
```

The key's public half isn't in it: a site checks the signature against the keys it trusts, found by `keyId`.

### Keys

| What | Form |
|---|---|
| A private key | PKCS#8 PEM (`-----BEGIN PRIVATE KEY-----`), as `abp-plugin-sign keygen` writes `private.pem`. Only the signer has it |
| A public key | SubjectPublicKeyInfo PEM (`-----BEGIN PUBLIC KEY-----`), as `public.pem`, or the same bytes as base64. P-256 only |
| A key ID | the first 16 hex digits (lowercase) of the SHA-256 of the public key's SubjectPublicKeyInfo bytes |

`PluginSignature.ReadPublicKey` takes a public key as PEM or base64 and refuses anything else, a private key above all:
a site is never given one. `PluginSignature.IsPrivateKey` says whether text holds one (a PEM labelled as a private
key, or an EC or RSA private key as base64), so a form can say so before keeping anything.

### Checking a package

```csharp
using AbpPlugin.Packaging;

PluginPackageManifest manifest = PluginPackageManifest.Read(folder) ?? throw new PluginException("No manifest.");
PluginSignatureFile? signature = PluginSignature.Read(folder);
byte[] key = PluginSignature.ReadPublicKey(File.ReadAllText("public.pem")) ?? throw new PluginException("Not a public key.");

bool signedByKey = signature is not null
    && signature.KeyId == PluginSignature.KeyId(key)
    && PluginSignature.Verify(signature, folder, key);
```

`PluginSignature.Matches(signature, folder)` checks only that the files are the ones the digest was made over, without
a key: it shows nothing changed since, not who signed. A host refuses a package that fails it whoever the signer is.

### Signing in code

```csharp
(string privatePem, string publicPem, string keyId) = PluginSignature.CreateKey();
PluginSignatureFile signed = PluginSignature.Sign(folder, privatePem, "Acme");
```

`Sign` writes `plugin-signature.json` into the folder. Zip the folder after.

## An application's own names

An application whose packages are already out says its format once, before any package is read or signed: in a module
initializer of its own assembly, or at the start of `Program.cs`. Its build and signing tool say the same.

```csharp
PluginPackageFormat.Current = new PluginPackageFormat
{
    Manifest = "my-plugin.json",
    FormerManifests = ["old-plugin.json"],     // read when a package has no Manifest
    Signature = "my-signature.json",
    Marker = ".my-package",
    FormerMarkers = [".old-package"],
    Unsigned = ["my-countersignature.json"],   // added after the publisher signed
    SignedPrefix = "my-plugin-signature/1\n",
    PackageExtensions = [".zip", ".mypkg"],
};
```

| Name | Default | What changing it means |
|---|---|---|
| `Manifest` | `plugin.json` | the manifest's file name; former names are still read |
| `Signature` | `plugin-signature.json` | the signature's file name, left out of the digest |
| `Marker` | `.plugin-package` | the mark a server writes on an unpacked package, left out of the digest; former marks are still read, and left out too |
| `Unsigned` | none | files at a package's root left out of the digest, for ones added after the publisher signed, such as a marketplace's countersignature; the publisher's signature doesn't vouch for them, so name only files the host doesn't run |
| `SignedPrefix` | `abp-plugin-signature/1\n` | what signatures are made over: packages signed under one prefix don't verify under another |
| `PackageExtensions` | `.zip` | the extensions a host unzips |

The digest and the algorithm are the same in every format. `PluginPackageFormat.Default` is AbpPlugin's own.

`abp-plugin-sign` signs in AbpPlugin's own format. An application with its own ships its own tool under its own
command name, a small program that sets its format and runs the same commands:

```csharp
using AbpPlugin.Packaging;

PluginPackageFormat.Current = new PluginPackageFormat { Manifest = "my-plugin.json", Signature = "my-signature.json", SignedPrefix = "my-plugin-signature/1\n" };
return PluginSigningTool.Run(args, "my-plugin-sign");
```

Its build package then sets `AbpPluginSignCommand` to that command, and `AbpPluginManifestFile` to its manifest's
name ([building plugins](building-plugins.md#building-and-packing)).
