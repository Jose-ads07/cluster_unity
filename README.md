# Unity + Raspberry Pi Distributed Simulation

An experimental distributed-game prototype: Unity owns the authoritative game
state and rendering, while Python workers simulate zones on Raspberry Pi or on
local processes. The current version explores binary messages, snapshot patches,
delta results, adaptive zone assignment and bounded simulation history.

This repository contains integration scripts, **not a complete Unity project**.
You create a Unity scene and attach the supplied C# components. No supported
Unity version or hardware-performance guarantee is claimed without testing it.

## Start Here

| Directory | Role |
| --- | --- |
| [cluster-unity-prototype](cluster-unity-prototype/README.md) | Current implementation and detailed setup |
| [Architecture](cluster-unity-prototype/docs/ARCHITECTURE.md) | Master/worker responsibilities |
| [Protocol](cluster-unity-prototype/docs/PROTOCOL.md) | Binary messages and state synchronization |
| [Operations](cluster-unity-prototype/docs/OPERATIONS.md) | Local workers and diagnostic tools |
| [Historical snapshots](cluster-unity-prototype-history/00_LEEME_PRIMERO.md) | Earlier design stages, not alternative current releases |
| [Original exports](archive/exports/) | Two older ZIPs with distinct historical notes; preserved unchanged |

The root no longer duplicates an entire versioned directory in a byte-identical
ZIP. Earlier content is still recoverable from Git history. Source files and
the unpacked design snapshots have not been removed or merged together.

## Local Worker

Python 3.12 is used for the maintenance checks. The worker and command-line
diagnostics use the Python standard library.

```bash
cd cluster-unity-prototype
python3 raspberry_worker/worker.py --host 127.0.0.1 --port 7000 --label local-worker
```

In Unity, copy the current `unity_master/Cluster*.cs` files and `EnemyView.cs`
into your project's scripts directory, create the manager object and configure
its worker endpoints and scene references as described in the detailed guide.
There is no standalone C# console build target here.

## Local Diagnostics

These commands spawn temporary workers on loopback and terminate them afterward:

```bash
cd cluster-unity-prototype
python3 tools/zone_history_smoke.py --spawn-local-worker
python3 tools/zone_history_export_smoke.py
python3 tools/zone_history_rotation_smoke.py
python3 tools/load_test.py --spawn-local-workers --workers 2 --grid-columns 2 --grid-rows 2 --entities-per-zone 8 --ticks 10
```

Measured checks and their limits are recorded in [VALIDATION.md](VALIDATION.md).
Do not interpret a loopback smoke test as a Unity scene test, Raspberry Pi
benchmark or real-network reliability evaluation.

## Network Safety

The prototype protocol does not provide authentication or TLS. Use loopback or
an isolated, trusted lab network only. Do not forward worker ports to the Internet.
Binding to `0.0.0.0` is an explicit trusted-LAN operation, not a safe public-hosting
configuration. Check firewall and network permissions before using physical nodes.

## License

The owner authorized publication and the [MIT license](LICENSE) on
24 October 2026. This does not relicense Unity or third-party dependencies.
See [CONTRIBUTING.md](CONTRIBUTING.md) for small fixes, reproduction details and
the limits of the protocol checks.
