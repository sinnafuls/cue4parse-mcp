# Changelog

## 1.2.0 — 2026-10-16

- Added `export_json` / `export-json`: complete, untruncated property JSON for a package (every export) or one `Package.Object`, streamed atomically to a file instead of returned inline.
- Added `query_table` / `query-table`: paged DataTable queries (`offset` ≥ 0, `limit` 1–500) with column selection, typed `eq`/`ne`/`contains`/`gt`/`gte`/`lt`/`lte` filters combined with AND, documented null/missing/nested semantics, and total/matched/next-offset information. Unknown columns and unsupported operators are rejected.
- Added `export_table` / `export-table`: every matching row as nested JSON or RFC 4180 CSV (UTF-8 with BOM, CRLF, invariant numbers, distinct null and empty string). Text fields that spreadsheets could evaluate as formulas are prefixed with `'`; struct/array cells are rejected for CSV instead of flattened.
- Added `diagnose_game` / `diagnose`: sanitized mount report covering engine version, mount status, mounted/unmounted archives, index/entry encryption, required encryption GUIDs with configured/assigned/verified/rejected state, mappings source, and native codec/ACL availability, with an optional single-asset probe that separates locate/read/header/deserialize failures. Mount failures are reported rather than thrown. Keys are marked verified only by evidence of successful decryption (CUE4Parse index validation, an encrypted `DefaultGame.ini`, or a probed encrypted package header), never by unencrypted reads. Key values, endpoint credentials/paths/queries, local paths, token-like strings, and stack traces are not reported.
- Added `get_dependencies` / `dependencies`: bounded (depth 1–8, limit 1–2000) outgoing hard (import map/IoStore) and soft (typed soft object path) package references with source/kind, resolvability, deduplication, cycle marking, and explicit truncation/depth/completeness flags. It is not a reverse index; runtime-constructed, string-based, bytecode, native-serialized, and asset-registry-only references are not reported.
- Added `export_audio` / `export-audio` for SoundWave, SoundNodeWave, AkMediaAssetData, and loose `.wem`/`.wav`/`.ogg`/`.opus`/`.flac` archive files. `original` (default) writes the cooked payload unchanged with an extension matching its actual header (for example `.wem`, `.ogg`, `.ueopus`, `.binka`, `.rada`); `wav` succeeds only for payloads that already are valid integer PCM RIFF/WAVE. Nothing is transcoded, compressed codecs are never labeled as WAV, and sound banks are rejected.
- Added previewable batch exports (`preview_batch`, `start_batch`, `get_batch_job`, `cancel_batch`; CLI `batch --request PATH [--run]`) for mesh, texture, animation, JSON, table, and audio kinds, up to 500 assets. Previews write nothing and expire; stale previews are rejected. Jobs keep their own game lease across configuration hot reloads, report progress and per-item results, support cancellation, share one native export session to deduplicate mesh/texture/animation dependencies, and write a full JSON manifest.
- CLI: new commands with typed option parsing (`--columns`, `--filters`, `--offset`, `--depth`, `--probe`, `--mode`, `--request`, `--run`). `--run` batches exit `1` unless the job completes without errors; Ctrl+C cancels the running job and still prints its final status. The CLI source moved from `Program.cs` to `Cli.cs`.
- Parser failures now propagate rather than returning partially deserialized objects as successful exports; atomic single-file exports preserve previous output on failure. Dependency discovery follows actual legacy import ownership, omits native class-default objects, and marks bounded property traversal incomplete when it reaches its nesting budget.
- Existing ImageSharp security advisories remain disclosed and unsuppressed.

Verified all 61 encrypted-pak, configuration-reload, structured-data, diagnostics/redaction, dependency, audio, and batch regressions, including genuine cooked fixtures, byte-identical audio, rejected unsupported WAV conversions, atomic-write rollback, partial batch failures, stale previews, deduplication, and cancellation. A live MCP server exposed all 18 tools, mounted 289,307 real Valorant packages, exported valid full JSON and CSV, reported truthful unencrypted-probe evidence, and exported two glTF meshes in one batch with 15 shared dependency files.

The relocated self-contained ZIP also passed MCP DataTable paging/numeric filters/CSV, typed dependency traversal, actual PCM WAV and Unreal Opus extraction, unsupported-WAV rejection, raw archive-audio batching, and CLI preview/run workflows. On real Valorant packages, the bundled native library reported ACL available, dependency traversal scanned 7 packages and 25 references, and ActorX animation export succeeded. Cancelling a 42-selection batch and closing the server with another active job both wrote cancelled manifests and released the process cleanly. The ZIP checksum matched and it contained no local config, keys, mappings, or game archives.

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
