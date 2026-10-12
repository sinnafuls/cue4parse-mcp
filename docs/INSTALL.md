# Installation and configuration

## 1. Prerequisites

This guide targets **Windows x64**. You need local game archives you are authorized to read, Git, and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Check both commands in a new PowerShell terminal:

```powershell
git --version
dotnet --version
```

For ACL-compressed animation export, also install:

- [Visual Studio 2022 Build Tools](https://visualstudio.microsoft.com/downloads/), with **Desktop development with C++**, an x64 MSVC toolchain, and a Windows SDK.
- [CMake](https://cmake.org/download/), added to `PATH`.

CUE4Parse's build invokes CMake to build `CUE4Parse-Natives.dll`. A native configure failure can leave the managed build usable, but a later native compilation failure can fail the build. Do not assume a successful managed-only build provides animation support.

## 2. Clone and build

```powershell
git clone --recurse-submodules https://github.com/sinnafuls/ceu4parse-mcp.git
Set-Location ceu4parse-mcp
dotnet publish src/Cue4ParseMcp/Cue4ParseMcp.csproj -c Release -o artifacts/server
```

Already cloned without submodules? Run `git submodule update --init --recursive` in the repository before building.

The executable is `artifacts/server/Cue4ParseMcp.exe`. Keep the **entire output directory**, including DLLs and runtime/configuration files. This is framework-dependent: the machine running it needs the .NET 10 runtime (the SDK already includes it).

If you deliberately do not need ACL animation decompression, skip the native build explicitly:

```powershell
dotnet publish src/Cue4ParseMcp/Cue4ParseMcp.csproj -c Release -o artifacts/server -p:CUE4PARSE_SKIP_NATIVE=true
```

Do not reuse a previous native-enabled publish directory when checking what a native-free build contains. Standard solution build: `dotnet build CUE4Parse-MCP.slnx -c Release`.

## 3. Configure games

```powershell
Copy-Item games.example.json games.json
notepad games.json
```

Edit existing entries or add a game supported by [CUE4Parse's `EGame` enum](https://github.com/FabianFG/CUE4Parse/blob/master/CUE4Parse/UE4/Versions/EGame.cs). Remove games you will not use. The example's DbD AES value is a placeholder and must be replaced; endpoint-backed Valorant uses a null local key until you configure a fallback.

A minimal configuration for an unencrypted game with versioned properties:

```json
{
  "defaultOutputDir": "exports",
  "games": {
    "mygame": {
      "displayName": "My game",
      "paksDir": "C:/Games/MyGame/Content/Paks",
      "ueVersion": "GAME_UE5_3"
    }
  }
}
```

Use the exact engine/game enum matching your installed build, not necessarily `GAME_UE5_3`. Most cooked UE5 games use unversioned properties and need mappings as well. A warning about missing AES keys does not imply that an unencrypted game needs one.

### Configuration fields

| Field | Meaning |
|---|---|
| `defaultOutputDir` | Base export directory; default is `exports` beside the config, with a subfolder per game |
| `cacheDir` | Native-library and mappings cache; default `%LOCALAPPDATA%/Cue4ParseMcp` |
| `oodlePath`, `zlibPath` | Optional existing native DLL paths; when omitted, CUE4Parse downloads them into the cache |
| `games.<id>.displayName` | Optional human-readable name |
| `games.<id>.paksDir` | Local archive directory, scanned recursively; IoStore games also need their `.utoc`/`.ucas` files |
| `games.<id>.ueVersion` | CUE4Parse `EGame` enum, e.g. `GAME_Valorant`, `GAME_DeadByDaylight` |
| `games.<id>.aesUrl` | JSON endpoint supplying `mainKey` and optional `dynamicKeys` |
| `games.<id>.aesKey` | Local main AES key, hex with optional `0x`; used when no endpoint key is available |
| `games.<id>.dynamicKeys` | Optional object mapping container encryption GUIDs to AES keys |
| `games.<id>.mappingsUrl` | Endpoint describing a downloadable `.usmap` |
| `games.<id>.mappings` | Local `.usmap` fallback path |
| `games.<id>.outputDir` | Per-game export directory, overriding `defaultOutputDir` |

Use forward slashes in JSON paths (`C:/Games/...`) or escape backslashes (`C:\\Games\\...`). Comments and trailing commas are accepted by this server, although strict JSON editors may flag them. Relative local paths are resolved against **the directory containing the config**, not the client's working directory. An explicit export `outDir`/CLI `--out` is a per-request filesystem path; use an absolute path when invoking from an MCP client.

Config lookup order:

1. `--config PATH`.
2. `CUE4PARSE_MCP_CONFIG` environment variable.
3. `games.json` in the working directory.
4. `games.json` beside the executable, then its parent directories.

An explicit missing file is an error; it does not silently fall back. Use an absolute `--config` path for client registration.

### Keys and mappings

Keys must match encrypted containers. FModel users can reuse keys from `%APPDATA%/FModel/AppSettings.json` under `PerDirectory.<paks dir>.AesKeys.mainKey`, and native DLLs from FModel's `Output/.data`; FModel is not otherwise required.

Supported AES endpoint shape:

```json
{"mainKey":"0x...","dynamicKeys":[{"guid":"...","key":"0x..."}]}
```

Supported mappings descriptor shapes:

```json
[{"url":"https://example.com/build.usmap","fileName":"build.usmap"}]
```

```json
{"mappings":{"ZStandard":"https://example.com/build.usmap"}}
```

The endpoint URLs in the Valorant template are third-party services, not guaranteed project infrastructure. Remote keys are tried first, with configured keys as fallback. Mappings are cached by game and filename; on endpoint failure, the local mappings path is tried, then the newest cached `.usmap`. Remove stale cached mappings if a publisher reuses a filename for a different build.

**Mappings must match the game build.** DbD users need a current-build `.usmap` (for example, generated with Dumper-7). Mounting archives successfully does not prove that mappings are compatible; inspect and export an asset too. Only configure trusted endpoints and native DLLs.

## 4. Verify locally

```powershell
.\artifacts\server\Cue4ParseMcp.exe help
.\artifacts\server\Cue4ParseMcp.exe load valorant --config "$PWD/games.json"
.\artifacts\server\Cue4ParseMcp.exe search valorant sarge --type SkeletalMesh --limit 5 --config "$PWD/games.json"
```

Replace `valorant` and the query with your game and desired asset. Check mount counts, warnings, and search notes. Use a returned path for `inspect`, then an appropriate export command from the [README](../README.md). Check the export report's `failures` and verify the resulting file can be opened by your consuming application.

## 5. Register an MCP client

### Claude Code

From the repository directory:

```powershell
claude mcp add cue4parse --scope user --transport stdio -- "$PWD/artifacts/server/Cue4ParseMcp.exe" --config "$PWD/games.json"
claude mcp get cue4parse
```

Restart the client if needed and call `list_games`, then `load_game`. When multiple games are configured, pass `game` to asset tools to avoid ambiguity.

### Claude Desktop / other stdio clients

Merge this entry into your client's MCP configuration; replace paths with your actual absolute locations:

```json
{
  "mcpServers": {
    "cue4parse": {
      "command": "C:/Tools/ceu4parse-mcp/artifacts/server/Cue4ParseMcp.exe",
      "args": ["--config", "C:/Tools/ceu4parse-mcp/games.json"]
    }
  }
}
```

Claude Desktop on Windows uses `%APPDATA%/Claude/claude_desktop_config.json`. Other clients may use a different file/schema; the executable and args remain the same. Restart the client after editing. No HTTP port, API key for this server, or running FModel instance is needed.

## Troubleshooting

| Symptom | Action |
|---|---|
| .NET target/runtime not found | Install the .NET **10** SDK to build, or runtime to run a published framework-dependent app; reopen your terminal |
| CUE4Parse project/submodule missing | Run `git submodule update --init --recursive` |
| CMake/MSVC failure | Install CMake and the C++ build workload, or deliberately use `-p:CUE4PARSE_SKIP_NATIVE=true` if ACL animations are not needed |
| `No games.json found` | Pass `--config` with an absolute path; verify the file exists |
| Invalid escapable character in JSON | Change Windows `\` to `/` or `\\` inside string values |
| Paks directory not found | Set `paksDir` to the installed game's archive directory |
| Containers remain encrypted/unmounted | Check keys and game version; inspect load warnings. Not every unmounted auxiliary container indicates an AES failure |
| Mount works, inspect/export fails to deserialize | Obtain matching mappings, then consider updating CUE4Parse for game-specific parsing fixes |
| ACL/native DLL load failure | Rebuild with the native prerequisites and ensure `CUE4Parse-Natives.dll` is beside the executable |
| Download fails/offline setup | Supply existing trusted `oodlePath`, `zlibPath`, `aesKey`, and `mappings`; omit remote endpoints to avoid requests |
| Type search is truncated | Narrow the query and use `under`; the type scan is capped |
| ImageSharp `NU1902` / `NU1903` warnings | Known, retained advisories; read the [dependency security disclosure](../README.md#dependency-security). They are not suppressed, and the patched 4.1.2 release needs a Six Labors license |
| Client waits when launching executable manually | No command means MCP stdio mode; use `help` or a CLI command instead |

Logs are written to stderr to keep stdout valid MCP/CLI output. For troubleshooting, set `$env:CUE4PARSE_MCP_LOG_LEVEL = 'Debug'` before launching; return to `Warning` afterward.

`games.json`, build outputs, exports, caches, and local agent state are gitignored. Keep keys, machine-specific paths, downloaded mappings, proprietary game files, and exported assets out of commits.
