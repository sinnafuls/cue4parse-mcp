# CUE4Parse-MCP

An MCP stdio server and command-line tool for searching, inspecting, and exporting Unreal Engine assets directly from local game archives. Uses [CUE4Parse](https://github.com/FabianFG/CUE4Parse), the parsing library behind FModel; no FModel installation or UI automation is required.

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

Asset tools auto-load their game. Pass `game` explicitly when using multiple games; otherwise the last-loaded game is selected (or the sole configured game). A typed search reads package headers and caps scanning at 20,000 name matches; use `under` and narrower queries when results are truncated.

Formats:

- Mesh: `psk` (default; `.psk`/`.pskx`), `gltf` (binary `.glb`), `ueformat`, `usd`. LODs: `highest` (default), `lowest`, `all`.
- Texture: `png` (default), `tga`, `jpeg`, `webp`.
- Animation: `psa` (default), `ueformat`, `usd`. ACL-compressed animations need `CUE4Parse-Natives`.

Paths accept provider paths (`ShooterGame/Content/...`), Unreal paths (`/Game/...`), `Package.Object`, and class-qualified references (`SkeletalMesh'/Game/...'`). Search results provide canonical paths.

Exports preserve in-game folders beneath `outDir`, the game's `outputDir`, or `<defaultOutputDir>/<game>`, in that order. Export reports separate `rootFiles`, `dependencyFiles`, and `failures`; always check failures, even if some files were written. These are local filesystem paths on the server machine, not downloads returned to the MCP client.

## CLI examples

```powershell
.\Cue4ParseMcp.exe search valorant sarge --type SkeletalMesh --limit 10
.\Cue4ParseMcp.exe ls valorant /Game/Characters
.\Cue4ParseMcp.exe inspect valorant "/Game/path/from/search" --props
.\Cue4ParseMcp.exe export-mesh valorant "path/from/search" --format gltf --out D:/Exports
.\Cue4ParseMcp.exe export-textures valorant "path/from/search" --format png --out D:/Exports
.\Cue4ParseMcp.exe export-anim valorant "path/from/search" --format psa --out D:/Exports
```

Use an actual asset path returned by `search`, not the illustrative `path/from/search`. `--config PATH` selects configuration explicitly. CLI results are JSON on stdout; logs go to stderr. Set `CUE4PARSE_MCP_LOG_LEVEL` to `Debug`, `Information`, or `Warning` to change verbosity. Running without a command starts the MCP stdio server, not an interactive prompt.

## Layout and maintenance

```text
src/Cue4ParseMcp/
  Program.cs          stdio host and CLI
  Config/             configuration loading
  Core/               game lifecycle, resource loading, asset operations
  Tools/              MCP tool declarations
lib/CUE4Parse/         pinned upstream submodule (including conversion/native code)
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
git tag v1.0.0
git push origin v1.0.0
```

Use a **new** `vMAJOR.MINOR.PATCH` tag for each release; never move a published tag. The release workflow derives the executable's version from the tag, publishes self-contained `win-x64` without trimming/single-file bundling, includes the config template, docs, and license notices, and smoke-checks the extracted ZIP. Only then does it create a GitHub Release with the ZIP and a `.sha256` checksum. Ordinary `master` pushes still produce CI artifacts, not releases.

To run identical packaging locally from a source checkout with native build tools installed:

```powershell
pwsh -File scripts/Package-Release.ps1 -Version 1.0.0
```

The version must have a changelog section. Output is under `artifacts/releases/`; an existing ZIP is not overwritten.

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

The fixed version, 4.1.2, requires a valid [Six Labors license](https://sixlabors.com/pricing/) during Release builds. Retaining 3.1.12 for this deployment is an explicitly accepted limitation, not a clean security audit. NuGet audit warnings remain enabled. Source review found CUE4Parse's direct ImageSharp usage in landscape heightmap handling; this server exposes mesh, texture, material, and animation exports, not landscape, TIFF, ICC, or histogram operations. That reduces observed exposure but does **not** establish that the dependency is safe.

Use trusted local game archives and do not expand image/landscape functionality without resolving these advisories. To reassess the complete dependency graph:

```powershell
dotnet package list --project src/Cue4ParseMcp/Cue4ParseMcp.csproj --include-transitive --vulnerable
```
