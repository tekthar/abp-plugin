# Security

Please report a vulnerability privately, through GitHub's "Report a vulnerability" on this repository, not in an
issue. Say what it is, how to reach it, and what it lets someone do.

The host's own lines:

- A package whose files changed, were added or were removed after it was signed is never installed, whoever signed it.
- System plugins, and packages under the application's own publishers, go in only with a release key's signature
  (unless the server's operator sets `PluginHost:AllowUnsignedSystem`).
- An upgrade comes from the plugin's own signer: a plugin that went in signed by a release key or a trusted publisher
  takes only a version that same key, or a release key, signed.
- Unsigned packages, and packages signed by a key the site doesn't know, go in only where the operator lets admins allow
  them (`PluginHost:AdminsMayAllowUnsigned`) and the admins do.
- A private key is never taken as a release key or a publisher's key.
- A plugin reads only its own settings, files, secrets and extension points, and its context maps tables only in its
  own schema.
- A plugin's endpoints need a signed-in caller, except under `public/`, where a body is capped at 1 MB and calls at 120
  a minute from one address and 3000 a minute for the plugin.

A way past any of these is a vulnerability.

What the host doesn't promise: a plugin's code runs in the server's process, with the server's rights. A load context
is for unloading and for versions, not a sandbox: a plugin's code can read files, open connections and use
reflection like any other code in the process. Which plugins run is decided by the signatures a site trusts. The
default `IOutboundHttp` doesn't refuse private or loopback addresses: an application whose plugins call addresses
people type exposes one of its own that does.
