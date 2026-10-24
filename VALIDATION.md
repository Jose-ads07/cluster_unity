# Validation and Maintenance Scope

Review date: 24 October 2026. The owner confirmed publication rights and
authorized the MIT license. No Unity/Raspberry Pi hardware result is invented.

## Local Checks

The current Python worker and tools were exercised with Python 3.12.14:

| Check | Measured result |
| --- | --- |
| Zone-history smoke test | PASS; snapshot, simulation and snapshot-patch history verified |
| History-export smoke test | PASS; four records, final tick 2 and snapshot 2 |
| History-rotation smoke test | PASS; compressed rotated export and replay verification |
| Small loopback load test | PASS; two workers, four zones, eight entities per zone, ten ticks |
| Load-test result counts | 40 successful results; zero timeouts, checksum errors or stale results |
| Python syntax | Parsed without executing all archived Python programs |

The worker processes were temporary and terminated after the checks. Timing
depends on the machine and is not presented as a Raspberry Pi benchmark.

## Preserved Content

All 79 protected Python/C# source files remain byte-identical. The two older ZIP
exports were moved into `archive/exports/` without changing their bytes because
they contain distinct historical notes. A third ZIP was removed from the current
tree only after all 94 of its file members matched already-versioned files.
Its original bytes remain in Git history. macOS metadata and generated Python
bytecode were removed; historical source snapshots remain available.

Machine-local Markdown hyperlinks were replaced with valid relative paths.
Only documentation/configuration and the owner-authorized license were added;
the simulation and protocol algorithms were not rewritten.

## Limits

- No complete Unity project, scene, prefab package or Unity version is supplied.
- Native Unity/C# integration and physical Raspberry Pi behavior were not tested.
- A short loopback test is not a distributed reliability or security evaluation.
- The protocol has no authentication or TLS; use a trusted isolated network.
- Common credential-pattern scans complement review but cannot prove arbitrary
  secrets are absent from every binary or historical artifact.

GitHub Actions runs the same bounded Python checks with read-only repository
permissions. Its remote result is verified separately after the maintenance push.
