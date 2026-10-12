# Changelog

## 1.0.0 — 2026-10-12

- Initial public CUE4Parse-MCP stdio server and CLI, with eight asset tools and a pinned CUE4Parse submodule.
- Consistent `Cue4ParseMcp` project/assembly naming and `CUE4PARSE_MCP_CONFIG` / `CUE4PARSE_MCP_LOG_LEVEL` environment variables.
- Valid example configuration, PowerShell quick start, detailed installation/client setup, and Windows publish automation.
- CLI correctly handles options before commands, missing values, help, and invalid invocations; configuration paths resolve relative to the config file.
- Game leases keep providers alive across concurrent reload/shutdown and release archive resources after the final operation.
- Endpoint timeouts fall back to configured keys/mappings while caller cancellation still propagates. Native-library initialization failures are reported and can recover on a subsequent load.
- Inspection reports unreadable headers; typed search skips and counts them. Missing folders and all-failed exports return errors; material-loading failures are reported.
- Upgraded transitive Microsoft.Bcl.Memory to 9.0.14. ImageSharp 3.1.12 advisories remain an explicitly accepted deployment limitation; see the README's dependency security section. Audit warnings are not suppressed.

Verified on Windows x64 with real Valorant archives: MCP initialize/tools, mounting, typed search, folder/path resolution, property inspection, glTF mesh, material/PNG texture, and PSA animation exports; CLI errors/example config; concurrent reload, provider lifecycle, HTTP timeout fallback, native-library recovery, and caller cancellation. Other games and export formats have not been verified for this release.
