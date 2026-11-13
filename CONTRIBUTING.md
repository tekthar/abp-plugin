# Contributing

- Build with `dotnet build AbpPlugin.slnx`.
- `dotnet test AbpPlugin.slnx` runs the unit tests and the host tests. The host tests need PostgreSQL (see the README),
  and build the sample plugin with `AbpPlugin.Build`'s targets themselves, so a change to the targets is tested there.
- Keep `AbpPlugin.Packaging` on .NET's own assemblies, and no product's name in any package: a unit test holds the
  first, and `bash scripts/check-names.sh` (in CI, on the repository and on the packed packages) holds the name
  everywhere.
- A change to the tables comes with a migration in the sample host (`dotnet ef migrations add` in
  `samples/AbpPlugin.SampleHost`), whose defaults keep what's there working as before.
- A change to the package format keeps packages already signed installable: a file that gets a new name keeps its old
  one among the format's former names, and the digest and the signed prefix don't change for a format already in use.
- A change to a trust rule, or to what a plugin may reach, comes with a host test that shows it, and a line in
  CHANGELOG.md.
- Each version has its entry in CHANGELOG.md before it's tagged: `bash scripts/check-release.sh` checks it.
