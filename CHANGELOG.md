# Changelog

## 1.1.0 — 2026-10-16

- Automatically hot-reload the selected `games.json` before tool operations. Added games are immediately discoverable; changed mount settings invalidate only affected sessions; removed games cannot be acquired by new calls.
- Keep the last valid configuration during invalid, locked or temporarily missing saves, including atomic editor replacements. Active leases retain their original provider and export-directory snapshot.
- Preserve mounted providers for unrelated game additions and output/display-only edits. Discard a stale mount if its profile changes or is removed while it is loading.
- Initialize the embedded Detex library for native texture decoding. Already-loaded native codec DLLs remain process-global and require a restart to replace.
- Fixed Windows test cleanup after Detex initialization. Process-global native codecs now use a cache under the test output directory instead of disposable per-pak fixture directories; fixture deletion remains strict.

Verified all 21 encrypted-pak and configuration reload regressions, including locked saves, main/dynamic key changes, removals during a pending mount, and active-operation lifetime. A single live self-contained MCP process also mounted real Valorant archives, discovered and loaded a newly added game profile, applied a changed archive path/output directory, rejected a removed game, exported valid glTF, and recovered from invalid/missing saves via atomic replacement without a restart. Native BC7 decoding was also verified for the Detex update.

## 1.0.1 — 2026-10-12

- Fixed AES key assignment for paks with unencrypted indexes and encrypted file contents, including the reported Sea of Thieves version-3 layout. Keys are submitted before the initial unencrypted mount, so those readers receive their matching keys before `PostMount()` and asset reads.
- Added generated AES-256-ECB pak regressions covering uncompressed, zlib and Sea of Thieves LZ4 payloads, encrypted indexes, dynamic GUID keys, and missing-key/unencrypted controls. Build and release workflows run the suite.
- Clarified that `load_game(reload: true)` reloads archives and configured endpoints, not `games.json`. Restart the MCP server after changing keys or other configuration values; a new CLI invocation reads the updated file.
- Existing ImageSharp security advisories remain disclosed and unsuppressed.

Verified with nine isolated generated-pak tests and the self-contained 1.0.1 ZIP against real Valorant archives: mounting, typed search, inspection, glTF mesh, eight PNG textures, PSA animation, and reload. Full Sea of Thieves archive/export verification requires the reporter's installed game files; no game files or real AES keys are included in the fixtures.

## 1.0.0 — 2026-10-12

- Initial public CUE4Parse-MCP stdio server and CLI, with eight asset tools and a pinned CUE4Parse submodule.
- Consistent `Cue4ParseMcp` project/assembly naming and `CUE4PARSE_MCP_CONFIG` / `CUE4PARSE_MCP_LOG_LEVEL` environment variables.
- Valid example configuration, PowerShell quick start, detailed installation/client setup, and Windows publish automation.
- Version tags publish a self-contained Windows x64 ZIP and SHA256 checksum to GitHub Releases. Bundles include native animation support, the safe config template, installation docs, and dependency/license notices; packaging smoke-checks CLI and MCP startup from the extracted archive.
- CLI correctly handles options before commands, missing values, help, and invalid invocations; configuration paths resolve relative to the config file.
- Game leases keep providers alive across concurrent reload/shutdown and release archive resources after the final operation.
- Endpoint timeouts fall back to configured keys/mappings while caller cancellation still propagates. Native-library initialization failures are reported and can recover on a subsequent load.
- Inspection reports unreadable headers; typed search skips and counts them. Missing folders and all-failed exports return errors; material-loading failures are reported.
- Upgraded transitive Microsoft.Bcl.Memory to 9.0.14. ImageSharp 3.1.12 advisories remain an explicitly accepted deployment limitation; see the README's dependency security section. Audit warnings are not suppressed.

Verified on Windows x64 with real Valorant archives: MCP initialize/tools, mounting, typed search, folder/path resolution, property inspection, glTF mesh, material/PNG texture, and PSA animation exports; CLI errors/example config; concurrent reload, provider lifecycle, HTTP timeout fallback, native-library recovery, and caller cancellation. Other games and export formats have not been verified for this release.

Release packaging additionally verified from a relocated self-contained ZIP: example-config startup, embedded runtime/version identity, real Valorant mounting, glTF mesh export, clean stdio shutdown, checksum integrity, and exclusion of local configs/keys/game archives.
