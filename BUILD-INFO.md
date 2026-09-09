# Build / validation record

- Date: 2026-09-09 (JST)
- Version: 1.0.0.2. Update: recipient-based damage capture, reflected/source-directed damage routing, ActorControl periodic damage, persisted event identifiers.
- Target: Windows x64, Dalamud API15, .NET 10
- .NET SDK: 10.0.300
- Dalamud.NET.Sdk: 15.0.0
- Reference Dalamud assembly: 15.0.3.2
- Reference distribution commit: ca5f595572182451f6b46206c8c856016ebb0e46
- Dalamud.dll SHA256: A419797D37EB3ABEB24790E7E0B067AE1E3E7A7FA30387A7109DA30CE5C26E1F
- Result: Release build, zero errors / warnings; 48 offline checks passed.
- In-game load, native hook behavior, UI rendering and live combat accuracy: NOT TESTED.
- Complete capture of all forms of damage: NOT GUARANTEED. Two native notification paths are supported; unnotified / direct HP-setting loss is not inferred. See README.ja.md.

Reference sources inspected:

- FFXIVClientStructs: f5c817534f3ce4d1e0e5c578cf6ea50c6483b160
- BossMod: 162fde51b5e56ca133cbc13502ae03548a23f461

The source repositories and host DLLs are not bundled in this package.
The 48 tests cover damage decoding, non-damage exclusion, same-event grouping,
duplicate handling, missing sequences, CSV schema/escaping/BOM, elapsed time,
live write flushing, filename collisions, persistent history retention and
corrupt-history handling, incoming retaliation routing, unknown/helper sources,
periodic damage versus healing, and synthetic Earthshaker-to-CSV processing.
The Earthshaker test is synthetic, not a captured in-game packet.
They do not substitute for native game integration tests.

Rebuilding against another Dalamud development installation changes the host
reference inputs; this record describes the initial supplied build only.
