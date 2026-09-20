# Build / validation record

- Date: 2026-09-20 (JST)
- Version: 1.0.0.3. Update: both modes use the monotonic real-time clock from combat detection; playback position is used only for seek detection and metadata. Pause time is included; playback speed does not scale elapsed time.
- Target: Windows x64, Dalamud API15, .NET 10
- .NET SDK: 10.0.300
- Dalamud.NET.Sdk: 15.0.0
- Reference Dalamud assembly: 15.0.3.5
- Reference distribution commit: e81744f6aea94bb6781affdd0d0b9319592f95d9
- Dalamud.dll SHA256: 1F662AA14DCD6FFB6E9B2A27B33EF93C04792CA2F9C41E863539AF9EE9F6E255
- Result: Release build, zero errors / warnings; 165 offline checks passed.
- In-game load, native hook behavior, UI rendering and live combat accuracy: NOT TESTED.
- Complete capture of all forms of damage: NOT GUARANTEED. Two native notification paths are supported; unnotified / direct HP-setting loss is not inferred. See README.ja.md.

Reference sources inspected:

- FFXIVClientStructs: f5c817534f3ce4d1e0e5c578cf6ea50c6483b160
- BossMod: 162fde51b5e56ca133cbc13502ae03548a23f461

The source repositories and host DLLs are not bundled in this package.
The 165 tests cover replay/live mode isolation, pause/seek/speed policies, replay metadata,
pet/companion exclusion and preservation of enemy helper events,
damage decoding, non-damage exclusion, same-event grouping,
duplicate handling, missing sequences, CSV schema/escaping/BOM, elapsed time,
live write flushing, filename collisions, persistent history retention and
corrupt-history handling, incoming retaliation routing, unknown/helper sources,
periodic damage versus healing, and synthetic Earthshaker-to-CSV processing.
The Earthshaker test is synthetic, not a captured in-game packet.
They do not substitute for native game integration tests.
Replay packet reception, seek restoration behavior, ReplayGroup membership and the
redesigned ImGui UI require in-game validation; no live replay/render test was run.

Rebuilding against another Dalamud development installation changes the host
reference inputs; this record describes the initial supplied build only.

Additional revision at requested version 1.0.0.3: FC-equivalent filtering by buff icon family, separate buff/debuff fields and CSV columns, native game icons paired with names, legacy history normalization. UI icon rendering remains untested in game.


Current revision: AMT-only update at unchanged version 1.0.0.3. Reduction rates are extracted from Japanese ActionTransient descriptions, with status/ability identity mappings and observed status-application provenance for ambiguous child buffs. No hard-coded percentage fallback. Recovery and barrier percentages are excluded. The additive aggregation and base*(1+percent/100)+barrier formula remain unchanged. Description evidence is persisted and exported. Prior linked devLibra source, binary and ZIP were not modified.

165 offline checks passed, including full-width/decimal text, physical/magic splits, healing exclusion, conflicting rates, source-specific secondary effects, changed description values, history preservation and provenance expiry. In-game native provenance hooks and UI rendering remain NOT TESTED. Packaging builds only AMT and includes only AMT sources.
