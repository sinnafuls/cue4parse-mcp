# CUE4Parse-MCP

An MCP stdio server and command-line tool for searching, inspecting, and exporting Unreal Engine assets directly from local game archives: meshes, textures, animations, complete property JSON, DataTables (paged queries, JSON/CSV exports), audio, and previewable batch exports, plus sanitized mount diagnostics and outgoing dependency listings. Uses [CUE4Parse](https://github.com/FabianFG/CUE4Parse), the parsing library behind FModel; no FModel installation or UI automation is required.

**Supported deployment target: Windows x64.** Release ZIPs include the .NET 10 runtime and native animation DLL; no separate .NET installation is required. Source builds need the .NET 10 SDK. Other platforms have not been verified. The server reads game files on disk, downloads configured keys/mappings and native decompression libraries, and writes exports/cache files. It does not attach to the running game. Use only assets you are authorized to access; game terms and asset copyrights still apply. Repacking or using modified assets in a game is outside this project's scope.

**Known dependency limitation:** the initial deployment retains CUE4Parse's ImageSharp 3.1.12 with documented security advisories. See [dependency security](#dependency-security) before building or distributing it.

## Quick start

Download the Windows x64 ZIP from [Releases](https://github.com/sinnafuls/cue4parse-mcp/releases/latest). Extract **all files** to a permanent folder, for example `C:/Tools/Cue4ParseMcp`; do not run from inside the ZIP or move only the executable.

```powershell
Set-Location C:/Tools/Cue4ParseMcp
Copy-Item games.example.json games.json
notepad games.json
```

Edit your game's `paksDir`, AES key/endpoint, and mappings path/endpoint. Remove unused games. Paths in JSON must use `/` or escaped `\\`; relative configuration paths resolve against the config file's directory.

Verify configuration and mounting before registering an MCP client:

```powershell
.\Cue4ParseMcp.exe help
.\Cue4ParseMcp.exe load valorant --config "$PWD/games.json"
```

Register with Claude Code, using absolute paths so the client's working directory does not matter:

```powershell
claude mcp add cue4parse --scope user --transport stdio -- "$PWD/Cue4ParseMcp.exe" --config "$PWD/games.json"
```

Restart the client and call `list_games`. For source builds, other MCP clients, configuration details, checksum verification, and troubleshooting, see the [installation guide](docs/INSTALL.md).

## MCP tools

| Tool | Purpose |
|---|---|
| `list_games` | List configured games, loaded games, and the config location |
| `load_game(game, reload?)` | Mount archives using configured AES keys and mappings; reload after a game patch |
| `search_assets(query?, type?, under?, limit?, game?)` | Search paths with all terms required, case-insensitive `*` wildcards, and optional class-hierarchy filtering |
| `list_folder(path?, limit?, game?)` | List direct subfolders and assets; an empty path lists roots |
| `inspect_asset(path, properties?, maxChars?, game?)` | Inspect export names/classes and optionally serialized property JSON |
| `export_mesh(path, format?, lods?, materials?, outDir?, game?)` | Export skeletal/static meshes with optional material JSON and textures |
| `export_textures(path, format?, outDir?, game?)` | Export a texture, a material's textures, or all textures of a mesh's materials |
| `export_anim(path, format?, outDir?, game?)` | Export animation sequences, montages, or composites |
| `export_json(path, outDir?, game?)` | Write the complete, untruncated property JSON of a package or one object to a file |
| `query_table(path, columns?, filters?, offset?, limit?, game?)` | Return one filtered page (at most 500 rows) of a DataTable |
| `export_table(path, format?, columns?, filters?, outDir?, game?)` | Write every matching DataTable row as `json` (default) or `csv` |
| `diagnose_game(game?, probe?)` | Sanitized mount, encryption-key, mappings, and codec report; optional single-asset read probe |
| `get_dependencies(path, depth?, limit?, game?)` | Bounded outgoing package references with source, resolvability, and completeness information |
| `export_audio(path, mode?, outDir?, game?)` | Export sound payloads in their original codec (default) or, where genuinely decodable, as WAV |
| `preview_batch(kind, paths?, query?, type?, under?, limit?, format?, lods?, materials?, audioMode?, outDir?, game?)` | Resolve a bounded batch export without writing anything; returns a `previewId` |
| `start_batch(previewId)` | Start a previewed batch as a background job in the server process |
| `get_batch_job(jobId, offset?, limit?)` | Job state, progress, and a page of per-item results |
| `cancel_batch(jobId)` | Cancel a running or queued job |

Asset tools auto-load their game. Pass `game` explicitly when using multiple games; otherwise the last-loaded game is selected (or the sole configured game). A typed search reads package headers and caps scanning at 20,000 name matches; use `under` and narrower queries when results are truncated.

The selected `games.json` is hot-reloaded automatically on the next tool call. Added games become available immediately; changed keys, archive paths, engine versions or mappings cause the affected game to re-mount on its next use. Removed games disappear from new calls. Existing operations finish using their original provider and output settings; unrelated games stay mounted. Invalid JSON, incomplete saves that fail parsing, or temporarily missing files keep the last valid configuration and log a warning. `load_game(reload: true)` still forces an archive remount when the file itself has not changed.

No restart is needed for game/profile or export-directory edits. Native codec DLLs already loaded into the process cannot be replaced by configuration reload; restart to replace an already-loaded Oodle/zlib/Detex library.

Formats:

- Mesh: `psk` (default; `.psk`/`.pskx`), `gltf` (binary `.glb`), `ueformat`, `usd`. LODs: `highest` (default), `lowest`, `all`.
- Texture: `png` (default), `tga`, `jpeg`, `webp`.
- Animation: `psa` (default), `ueformat`, `usd`. ACL-compressed animations need `CUE4Parse-Natives`.

Paths accept provider paths (`ShooterGame/Content/...`), Unreal paths (`/Game/...`), `Package.Object`, and class-qualified references (`SkeletalMesh'/Game/...'`). Search results provide canonical paths.

Exports preserve in-game folders beneath `outDir`, the game's `outputDir`, or `<defaultOutputDir>/<game>`, in that order. Export reports separate `rootFiles`, `dependencyFiles`, and `failures`; always check failures, even if some files were written. These are local filesystem paths on the server machine, not downloads returned to the MCP client.

### Property JSON and DataTables

`export_json` writes the same CUE4Parse property representation as `inspect_asset(properties: true)`, but untruncated and to a file instead of the response: `<packagePath>.json` (an array of every export) for a package, or `<packagePath>.<Object>.json` for an explicit `Package.Object`. Files are indented UTF-8 without BOM and written atomically; if any export fails to deserialize, the call fails and no partial file is left. Bulk binary data (texture mips, mesh buffers, audio) is not decoded into JSON; use the dedicated exporters.

Deserialization errors propagate instead of silently exporting partially read objects. A failed atomic write preserves any previous destination file. JSON still represents only the data supported by the pinned CUE4Parse parsers.

`query_table` and `export_table` accept `UDataTable` and its subclasses (a `CompositeDataTable` uses its cooked, merged row map). Curve tables, string tables, and other classes are rejected. Cell values use the same JSON as property inspection (enums such as `"EMyEnum::Value"`, structs as objects, arrays as arrays).

- **Columns** are top-level row properties as CUE4Parse names them (fixed-array elements appear as `Prop[1]`), in first-seen order across rows, matched case-insensitively. `$rowName` is the row key and is always returned. Unknown columns are rejected with the list of available columns; omitting columns selects all.
- **Filters** are objects `{"column": "...", "op": "...", "value": ...}`; all must match. Operators: `eq`, `ne`, `contains`, `gt`, `gte`, `lt`, `lte`. Numbers compare numerically (`1` equals `1.0`); strings compare ordinally and case-sensitively, except `contains`, which is a case-insensitive substring test on string cells and an element test on array cells. There is no cross-type coercion (`5` never equals `"5"`); booleans support only `eq`/`ne`. A missing or null cell matches only `eq null` and `ne <non-null value>`; ordering operators and `contains` never match it. Object/array cells support `eq`/`ne` by deep JSON equality only. Unsupported operators, missing values, and incompatible value types are rejected rather than ignored.
- **Paging:** `query_table` uses `offset` (≥ 0, default 0) and `limit` (1–500, default 50) and reports `totalRows`, `matchedRows`, `returned`, `hasMore`, and `nextOffset`. Out-of-range values are errors, not clamped. Rows are `{rowName, values}`; missing cells are omitted, while stored nulls stay `null`.
- **Exports:** `export_table` writes *every* matching row, not one page, as `<packagePath>.table.json` (default; nested values preserved) or `<packagePath>.table.csv` (with `.<Object>` before `.table` for explicit object input). The `.table` suffix keeps it separate from `export_json` output.

CSV is UTF-8 **with** BOM (so spreadsheet applications detect non-ASCII text), CRLF records, RFC 4180 quoting: a field is quoted if it contains a comma, quote, CR or LF, or is an empty string; embedded quotes are doubled. Null/missing cells are empty unquoted fields, so they differ from `""`. Numbers keep their invariant JSON text (`-7`, `1.25`, `1E+30`); booleans are `true`/`false`. To keep spreadsheets from evaluating formulas, any text field (row names, headers, string cells) beginning with `=`, `+`, `-`, `@`, tab, CR or LF is prefixed with `'`; numeric cells are unchanged. CSV accepts only flat scalar cells: a selected struct/array value fails the export, naming the column and row, and nothing is written. Select scalar columns or use `json`.

### Diagnostics

`diagnose_game` mounts the game if needed and reports instead of failing when mounting fails. `mount.status` is `mounted`, `partial`, `none-mounted`, `no-containers`, or `failed`; a failure carries a `failureCategory` (`paks-dir-not-found`, `unknown-engine-version`, `io-error`, `mount-error`), a sanitized message, and preflight checks. Unknown or unselected game ids are still errors. The report covers the selected config file's name (not its path); the configured/effective engine version; which keys, endpoints, and mappings are configured; mounted/unmounted container counts by type (up to 50 unmounted containers with reasons); file and package counts; mappings source and type count; whether native Oodle, zlib-ng, Detex, and `CUE4Parse-Natives` (ACL animation) libraries are loaded, plus compression methods in use; and warnings.

`encryption` lists each required key GUID with where its key came from (`games.json`, `endpoint`, `endpoint-unknown`, `none`), container/entry encryption counts, and a `state`: `verified`, `rejected`, `missing`, `unverified`, or `untested`. A key is `verified` only by evidence that it decrypted something: CUE4Parse validating an encrypted index/IoStore container (at mount, or with its key test for still-unmounted containers), an encrypted `DefaultGame.ini` in a plain-index pak decrypting into valid project settings, or a probe decrypting an encrypted entry into a valid package header. `rejected` includes CUE4Parse unmounting a plain-index pak whose `DefaultGame.ini` check failed. Reading unencrypted data, or opaque encrypted files the probe cannot validate, never verifies a key.

The optional `probe` (CLI `--probe PATH`) reads one asset and reports the stages `locate`, `read`, `header`, and `deserialize` (up to 64 exports) as `ok`, `failed`, `partial`, or `skipped`, categorized as `not-found`, `decryption`, `decompression`, `read`, `header`, `properties`, or `properties-no-mappings`, plus whether its container index and entry are encrypted.

The report is sanitized for sharing: no AES key values, endpoint URLs reduced to `scheme://host` (no credentials, path, query, or fragment), configured local paths replaced by labels such as `<paksDir>`, other drive/UNC/home paths by `<local-path>`, long hex runs and JWT-like tokens removed, no stack traces, and messages limited to one length-capped line. Public key GUIDs, container file names, and game paths are kept. Review the output before posting it anyway; redaction is pattern-based.

### Dependencies

`get_dependencies` lists **outgoing** references breadth-first from one package (the object part of a `Package.Object` input is ignored). `depth` (1–8, default 1) is the number of package levels whose references are listed, 1 meaning the root's direct references; `limit` (1–2000, default 200) caps the total reference entries. Packages are deduplicated by canonical path, references from a package to itself are omitted, and a reference to an ancestor is marked `cycle`.

- **Hard** references come from the legacy import map or IoStore store entries/imported packages. **Soft** references are typed `FSoftObjectPath` values in deserialized properties (including containers, structs, instanced structs, and DataTable rows; up to 512 exports per package) and the package header's soft-object-path list when present. `/Script/...` imports are listed as `scriptPackages` and never traversed.
- Each reference reports `kind`, `source`, whether it resolved to a mounted package, and whether it was scanned. Unresolved targets are missing, in unmounted/encrypted containers, or not shipped; unresolved IoStore targets appear as `packageId:<id>`.
- `truncated` means the limit omitted later references, `depthLimited` that resolvable references at the final depth were not scanned, and `complete` is true only without truncation or errors, relative to the requested depth.
- Nested property walks stop at 64 value levels and mark soft-reference coverage incomplete. Hard and soft references to the same target remain distinct; each target package is traversed only once.

Not reported: referencers (there is no reverse index), runtime-constructed or string/FName-based paths, soft references inside native (non-property) serialized data other than DataTable rows, IoStore container-header soft package lists, Blueprint bytecode references, and asset-registry-only dependencies. Treat the result as the references CUE4Parse can parse from these packages, not a whole-game dependency graph.

### Audio

`export_audio` accepts `SoundWave`, `SoundNodeWave`, and `AkMediaAssetData` assets, plus loose `.wem`, `.wav`, `.ogg`, `.opus`, and `.flac` archive files. Loose files are not indexed by `search_assets`/`list_folder`; pass their exact archive path including the extension. Sound cues, Wwise events, and banks (`.bnk`, `.pck`, `.bank`, `.awb`, `.acb`) are rejected; use `get_dependencies` or `inspect_asset` to find the sound assets they play.

- `mode: original` (default) writes the cooked payload byte-for-byte, with an extension chosen from its actual header: `.wav` (RIFF/WAVE), `.wem` (Wwise), `.at9`, `.xma`, `.ogg`/`.opus` (Ogg), `.flac`, or for headerless engine streams `.ueopus` (Unreal Opus, not playable as standard Ogg Opus), `.binka` (Bink Audio), `.rada` (RAD Audio); unknown codecs are named after the codec or `.bin`. Loose files keep their own path and extension, and a loose file whose bytes do not match its extension is refused.
- `mode: wav` succeeds only when the payload already is integer PCM RIFF/WAVE: CUE4Parse's pinned `SoundDecoder` must identify it as WAV (for example PCM assets, or Unreal "ADPCM" assets actually cooked as PCM) and its header must validate as a little-endian RIFF file with 8/16/24/32-bit samples; it is then written unchanged. The pinned CUE4Parse has no decoder that turns Opus, Bink Audio, RAD Audio, Vorbis, WEM, MS-ADPCM, or float WAVE data into PCM, so those fail with an error recommending `original` and an external decoder. A malformed PCM header is also an error. Nothing is written when an export fails.

Nothing is ever transcoded (`transcoded` is always `false`). Files are written atomically to `<outDir>/<packagePath>[.<Object>].<ext>` (loose files under their own archive path). Reports include `sourceKind`, `sourceCodec`, the detected `container`, `outputFormat`, byte size, parsed WAVE header details when present, and notes, for example when a sound holds several cooked formats and the first was exported.

### Batch exports

Batches run `mesh` (the CLI request default), `textures`, `anim`, `json`, `table`, or `audio` exports over up to 500 assets. Select assets with an explicit non-empty `paths` list **or** a `query`/`type`/`under` search (same semantics as `search_assets`), not both. `limit` (1–500, default 100) caps the search selection, and the preview notes when the cap truncated it; explicit lists have an independent maximum of 500 paths. `format`, `lods`, and `materials` apply as for the single-asset tools of that kind: `format` is `json`/`csv` for tables and, if given, must be `json` for the `json` kind. Audio batches take `audioMode` (`original` default, or `wav`) and no `format`.

1. `preview_batch` resolves exact canonical paths, the output directory, and options. It writes nothing and returns a `previewId`, valid for 10 minutes (at most 32 stored previews).
2. `start_batch(previewId)` re-acquires the current game profile and rejects a stale preview (game remounted, mount settings or output directory changed since the preview); preview again in that case. The job then keeps its own game lease until its manifest is final, so configuration hot reloads do not disturb it.
3. `get_batch_job(jobId, offset?, limit?)` reports `state` (`queued`, `running`, `cancelling`, `finalizing`, then `completed`, `completed_with_errors`, `failed`, or `cancelled`), progress, and a page of items (`offset` default 0, `limit` default 50, maximum 200; `nextItemOffset`/`itemsTruncated`). Each item has its own root files, errors, and, for audio, the actual codec metadata. Dependencies shared through the native export session are reported once for the whole batch, not per item: the first `limit` paths plus `totalDependencyFiles`/`dependencyFilesTruncated`.
   Dependency failures in `notes` are also bounded by `limit`, with `totalDependencyFailures` and `dependencyFailuresTruncated`; the manifest includes every failure.
4. `cancel_batch(jobId)` cancels the actual work; the job settles as `cancelled` after its manifest is written and its lease released.

One batch runs at a time; at most 8 unfinished jobs and 32 retained jobs exist, and the oldest finished job is evicted only when a new one is added. Mesh, texture, and animation items share one native export session, so dependencies shared between items (for example a common texture) are exported once. Other kinds run sequentially. Each job writes a complete JSON manifest of all results to `<outputDir>/.cue4parse-batches/<jobId>.json`. The manifest lists only files confirmed by a completed export; a native exporter failure or cancellation can leave unconfirmed partial files on disk, which the manifest notes, and nothing already written is deleted. Jobs live only in the memory of the server process that started them: stopping the server cancels unfinished jobs and waits for them to finalize, and nothing except manifests and exported files persists.

## CLI examples

```powershell
.\Cue4ParseMcp.exe search valorant sarge --type SkeletalMesh --limit 10
.\Cue4ParseMcp.exe ls valorant /Game/Characters
.\Cue4ParseMcp.exe inspect valorant "/Game/path/from/search" --props
.\Cue4ParseMcp.exe export-mesh valorant "path/from/search" --format gltf --out D:/Exports
.\Cue4ParseMcp.exe export-textures valorant "path/from/search" --format png --out D:/Exports
.\Cue4ParseMcp.exe export-anim valorant "path/from/search" --format psa --out D:/Exports
.\Cue4ParseMcp.exe export-json valorant "path/from/search" --out D:/Exports
.\Cue4ParseMcp.exe query-table valorant "table/from/search" --columns Name,Damage --filters '[{"column":"Damage","op":"gte","value":50}]' --limit 20
.\Cue4ParseMcp.exe export-table valorant "table/from/search" --format csv --columns Name,Damage --out D:/Exports
.\Cue4ParseMcp.exe diagnose valorant --probe "path/from/search"
.\Cue4ParseMcp.exe dependencies valorant "path/from/search" --depth 2 --limit 500
.\Cue4ParseMcp.exe export-audio valorant "sound/from/search" --mode original --out D:/Exports
.\Cue4ParseMcp.exe batch valorant --request ./batch.json
.\Cue4ParseMcp.exe batch valorant --request ./batch.json --run
```

Use an actual asset path returned by `search`, not the illustrative `path/from/search`; column names come from a query's `availableColumns`. `--config PATH` selects configuration explicitly. CLI results are JSON on stdout; logs go to stderr. Set `CUE4PARSE_MCP_LOG_LEVEL` to `Debug`, `Information`, or `Warning` to change verbosity. Running without a command starts the MCP stdio server, not an interactive prompt. Run `help` for every command and option.

Words after the game id form the asset path, so paths with spaces need no quotes; option values such as `--filters` JSON or a `--probe` path are single arguments and need quotes when they contain spaces. In PowerShell, single-quote JSON so `$rowName` is not expanded. PowerShell 7.3 and later pass embedded double quotes to native programs unchanged; Windows PowerShell 5.1 strips them, so escape each as `\"` there. `--columns` is comma-separated; `--filters` uses the filter objects above.

A batch request file contains the `preview_batch` arguments without `game`; unknown properties are rejected:

```json
{
  "kind": "mesh",
  "query": "sarge",
  "type": "SkeletalMesh",
  "limit": 20,
  "format": "gltf",
  "outDir": "D:/Exports"
}
```

`batch` only previews by default. `--run` starts the previewed job and waits for it in the same process, printing the final bounded job status and manifest path; the first Ctrl+C cancels the job and still prints its final status, a second one terminates immediately. CLI jobs end with the command, so a `previewId` or `jobId` from one invocation cannot be used by a later one; use the MCP tools for background jobs you poll or cancel later.

Exit codes: `0` success; `2` invalid command line or unusable `--request` file; `1` runtime errors (configuration, mounting, invalid paths or option values rejected by a service) and any `--run` batch that did not end `completed`, including `completed_with_errors`, `failed`, and `cancelled`. Single-asset exports that write some files still exit `0` and list partial `failures` in the report.
`diagnose` can exit `0` with `mount.status: "failed"`: the diagnostic report itself succeeded. Inspect its stages and status rather than treating its exit code as proof that the game mounted.

## Layout and maintenance

```text
src/Cue4ParseMcp/
  Program.cs          stdio host and service registration
  Cli.cs              command-line parsing and commands
  Config/             configuration loading
  Core/               game lifecycle, resource loading, asset/data/audio/analysis operations, batch jobs
  Tools/              MCP tool declarations
lib/CUE4Parse/         pinned upstream submodule (including conversion/native code and test fixture paks)
tests/Cue4ParseMcp.Tests/  generated encrypted-pak regressions and fixture-pak feature tests
docs/INSTALL.md       installation, client setup, troubleshooting
games.example.json    safe configuration template
Directory.Build.targets  patched transitive dependency override
scripts/Package-Release.ps1  self-contained publish, ZIP packaging, extracted-bundle smoke
.github/workflows/    master build artifacts and version-tagged GitHub Releases
```

The submodule is pinned to a reviewed commit; cloning does not automatically fetch newer parsing behavior. To intentionally update it:

```powershell
git -C lib/CUE4Parse fetch origin master
git -C lib/CUE4Parse checkout origin/master
git submodule update --init --recursive
dotnet build CUE4Parse-MCP.slnx -c Release
```

Verify mounting, search, inspection, and exports against your current game build, then commit the updated submodule pointer. Game patches may also require new AES keys and matching `.usmap` files. See [CHANGELOG.md](CHANGELOG.md) for project changes. CUE4Parse retains its upstream Apache-2.0 license and notices.

### Publishing a release

Add the version's notes to `CHANGELOG.md`, commit and push the changes to `master`, then create and push a stable version tag:

```powershell
git tag v1.2.0
git push origin v1.2.0
```

Use a **new** `vMAJOR.MINOR.PATCH` tag for each release; never move a published tag. The release workflow derives the executable's version from the tag, publishes self-contained `win-x64` without trimming/single-file bundling, includes the config template, docs, and license notices, and smoke-checks the extracted ZIP. Only then does it create a GitHub Release with the ZIP and a `.sha256` checksum. Ordinary `master` pushes still produce CI artifacts, not releases.

To run identical packaging locally from a source checkout with native build tools installed:

```powershell
pwsh -File scripts/Package-Release.ps1 -Version 1.2.0
```

The version must have a changelog section. Output is under `artifacts/releases/`; an existing ZIP is not overwritten.

Run the regression suite with `dotnet test tests/Cue4ParseMcp.Tests/Cue4ParseMcp.Tests.csproj -c Release -p:CUE4PARSE_SKIP_NATIVE=true`. Encrypted-pak fixtures are generated in temporary directories; feature tests mount the uncompressed cooked fixture paks checked into the `lib/CUE4Parse` submodule, so initialize submodules first. No game installation, real keys, downloaded Oodle/zlib libraries, or endpoint access is needed; Detex loads from the embedded library.

## Dependency security

`Directory.Build.targets` upgrades transitive `Microsoft.Bcl.Memory` to 9.0.14 to address [GHSA-73j8-2gch-69rq](https://github.com/advisories/GHSA-73j8-2gch-69rq), without changing upstream source.

**ImageSharp 3.1.12 remains vulnerable** to the following published advisories:

| Advisory | Affected operation |
|---|---|
| [GHSA-gwg2-r3hj-4w44](https://github.com/advisories/GHSA-gwg2-r3hj-4w44) | ICC CLUT allocation |
| [GHSA-j3p4-wp97-rph4](https://github.com/advisories/GHSA-j3p4-wp97-rph4) | Histogram equalization |
| [GHSA-j9gm-c75j-xc9q](https://github.com/advisories/GHSA-j9gm-c75j-xc9q) | TIFF CCITT T4 encoding |
| [GHSA-jjfr-hcj7-qf5w](https://github.com/advisories/GHSA-jjfr-hcj7-qf5w) | TIFF CCITT T6 encoding |
| [GHSA-wmxv-xphr-5c9g](https://github.com/advisories/GHSA-wmxv-xphr-5c9g) | BigTIFF decoding |

The fixed version, 4.1.2, requires a valid [Six Labors license](https://sixlabors.com/pricing/) during Release builds. Retaining 3.1.12 for this deployment is an explicitly accepted limitation, not a clean security audit. NuGet audit warnings remain enabled. Source review found CUE4Parse's direct ImageSharp usage in landscape heightmap handling; this server exposes mesh, texture, material, animation, audio, property-JSON, and DataTable exports (individually or batched), diagnostics, and dependency listings, not landscape, TIFF, ICC, or histogram operations. That reduces observed exposure but does **not** establish that the dependency is safe.

Use trusted local game archives and do not expand image/landscape functionality without resolving these advisories. To reassess the complete dependency graph:

```powershell
dotnet package list --project src/Cue4ParseMcp/Cue4ParseMcp.csproj --include-transitive --vulnerable
```
