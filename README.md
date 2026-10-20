# Couqui MCC Plugins

A schema-2 plugin marketplace for [Minecraft Console Client 2.0](https://github.com/MCCTeam/Minecraft-Console-Client).

MCC reads the marketplace index once, resolves the plugin dependency graph, downloads one archive per changed plugin, and verifies every SHA-256 digest before activation. Release archives are immutable: a published version is never rebuilt, and a bad release is yanked instead of edited.

## Prerequisites

- .NET SDK 10.0.4xx. The packaging tool targets `net10.0`; an SDK 8 installation cannot build it.
- NuGet access to `DMCBK.Marketplace` `0.1.0-preview.8`.
- A MCC 2.0 distribution (`Mcc.Cli`) for `--validate-plugin` and `--validate-marketplace`.

## Add this marketplace to MCC

```text
/plugins marketplace add https://raw.githubusercontent.com/DingCouqui/couqui-mcc-plugins/main/marketplace/mcc-marketplace.toml as couqui-mcc-plugins
/plugins marketplace list
/plugins search <text> in couqui-mcc-plugins
```

The name after `as` must equal the marketplace `id` in `marketplace/mcc-marketplace.toml` (`couqui-mcc-plugins`, case-insensitive). Omitting `as` uses the same id.

## Repository layout

```text
couqui-mcc-plugins/
├── marketplace/
│   ├── mcc-marketplace.toml        # schema-2 index: marketplace id and plugin identities
│   └── catalog/
│       └── <plugin-id>.toml        # one release catalogue per plugin, full history
├── src/
│   └── <plugin-id>/                # plugin author folders (source + manifest + resources)
├── tools/
│   └── PackRelease/                # deterministic zip, SHA-256 sidecar, catalogue fragment
├── releases/                       # published archives, served through their raw HTTPS URL
└── release-assets/                 # local packing output, ignored by Git
```

`marketplace/` is what users download. The archive at each `url` in a catalogue is immutable: publish a new plugin version instead of replacing an existing archive.

## Marketplace identity

- `id`: `couqui-mcc-plugins`
- Display name: `Couqui MCC Plugins`
- Index URL: `https://raw.githubusercontent.com/DingCouqui/couqui-mcc-plugins/main/marketplace/mcc-marketplace.toml`
- Default branch: `main`

The id is limited to ASCII letters, digits, `-`, `_`, and `.`, and must not start with a dot. Do not rename it after publishing: local publisher bindings and `plugins.lock.toml` record it.

## Published plugins

| Plugin | Version | What it does |
| --- | --- | --- |
| `chat-log-rotator` | 1.0.0 | Writes received chat to files rotated by day, ISO week or month. Keeps chat-log's filters, timestamp and placeholders, and adds `/chat-log-rotator period`. |

```text
/plugins install chat-log-rotator@couqui-mcc-plugins yes
/plugins enable chat-log-rotator
/chat-log-rotator status
/chat-log-rotator period week
```

## Add another plugin

1. Create `src/<plugin-id>/` and add the plugin files:
   - `plugin.toml` (schema 2, matching `id`, `version`, `kind`, `target`, `entry`, `api-version`, `dmcbk`, `umpk`, `framework`)
   - the entry `.cs` file for `kind = "source"`, or the built `.dll` for `kind = "compiled"`
   - optional `lang/en.toml`, `man/en/<topic>.md`, `defaults/settings.toml`
2. Validate the package folder:

   ```text
   Mcc.Cli --validate-plugin src/<plugin-id>
   ```

3. Pack a release asset. The packer writes the zip, a `.sha256` sidecar, and a `.catalogue.toml` fragment:

   ```text
   dotnet run --project tools/PackRelease -- src/<plugin-id> src/<plugin-id> release-assets source any https://github.com/DingCouqui/couqui-mcc-plugins/releases/download/plugin%2F<plugin-id>%2Fv<version>/
   ```

   For a compiled asset, build the DLL first and pass the payload folder instead of the source folder:

   ```text
   dotnet run --project tools/PackRelease -- src/<plugin-id> src/<plugin-id>/bin/Release/net10.0 release-assets compiled any https://github.com/DingCouqui/couqui-mcc-plugins/releases/download/plugin%2F<plugin-id>%2Fv<version>/
   ```

4. Upload the zip and its `.sha256` sidecar to an immutable HTTPS location. Either create a GitHub Release tagged `plugin/<plugin-id>/v<version>`, or commit the archive under `releases/<plugin-id>/<version>/` and use its raw URL. Never overwrite a published path; publish a new version instead.
5. Download the uploaded zip through its final URL and confirm the digest matches the generated sidecar.
6. Add the release metadata to `marketplace/catalog/<plugin-id>.toml`. On the first release, copy the generated `.catalogue.toml` fragment. For later versions, append one `[[releases]]` table and keep all previous entries.
7. Register the plugin in `marketplace/mcc-marketplace.toml`:

   ```toml
   [[plugins]]
   id = "<plugin-id>"
   description = "One line describing what the plugin does."
   tags = ["utility"]
   releases = "catalog/<plugin-id>.toml"
   ```

8. Validate the complete marketplace:

   ```text
   Mcc.Cli --validate-marketplace marketplace
   ```

9. Commit and push, then test the published metadata from a disposable MCC configuration folder:

   ```text
   /plugins marketplace add https://raw.githubusercontent.com/DingCouqui/couqui-mcc-plugins/main/marketplace/mcc-marketplace.toml as couqui-mcc-plugins
   /plugins search <text> in couqui-mcc-plugins
   /plugins install <plugin-id>@couqui-mcc-plugins yes
   /plugins info <plugin-id>
   ```

## Release catalogue shape

```toml
schema-version = 2
id = "<plugin-id>"

[[releases]]
version = "1.0.0"
api-version = "1.0"
dmcbk = ">=0.1.0-preview.8 <0.2.0"
umpk = ">=0.9.0-beta.7 <0.10.0"
framework = "net10.0"
needs = ["commands"]
yanked = false
assets = [
  { kind = "source", target = "any", url = "https://github.com/DingCouqui/couqui-mcc-plugins/releases/download/plugin%2F<plugin-id>%2Fv1.0.0/<plugin-id>-source.zip", sha256 = "<64 hex characters>" },
]
```

Rules that MCC enforces:

- `schema-version = 2` in the index, catalogue, and package manifest.
- Index plugin `id` must match the catalogue `id` and the package `id`.
- Every asset needs an absolute `http`/`https` URL and a 64-character hex SHA-256 digest.
- Published payload bytes, compatibility fields, dependencies, URLs, and asset lists are immutable. Change them only in a new plugin version.
- Set `yanked = true` to exclude a release from new ordinary selection without deleting its history.
- Supported targets: `any`, `win-x86`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-arm`, `linux-musl-x64`, `linux-musl-arm64`, `linux-musl-arm`, `osx-x64`, `osx-arm64`.

## Notes

- The index may be empty (`plugins` absent or `[]`) until the first plugin is published; MCC accepts it and the binding is still added.
- Use lowercase kebab-case for marketplace and plugin ids, for example `couqui-mcc-plugins` and `chat-log-daily`.
- Do not publish `plugins.lock.toml`, `userdata/`, or local MCC configuration in this repository.
