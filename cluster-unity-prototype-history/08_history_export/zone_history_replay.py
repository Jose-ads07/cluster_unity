#!/usr/bin/env python3
import argparse
import json
import os
import sys
from collections import Counter


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
WORKER_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "raspberry_worker"))
if WORKER_DIR not in sys.path:
    sys.path.insert(0, WORKER_DIR)

from protocol import EntityState, compute_zone_checksum


def load_entries(path, zone_id_filter=None):
    entries = []
    with open(path, "r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, start=1):
            line = line.strip()
            if not line:
                continue
            payload = json.loads(line)
            if zone_id_filter is not None and int(payload.get("zone_id", -1)) != zone_id_filter:
                continue
            payload["_line"] = line_number
            entries.append(payload)
    return entries


def verify_entry_checksum(entry):
    entities_payload = entry.get("entities")
    if not entities_payload:
        return None

    entities = []
    for entity in entities_payload:
        entities.append(
            EntityState(
                entity_id=int(entity["id"]),
                x=float(entity["x"]),
                y=float(entity["y"]),
                vx=float(entity["vx"]),
                vy=float(entity["vy"]),
                speed=float(entity["speed"]),
                state=int(entity["state"]),
                flags=int(entity["flags"]),
                reserved=0,
            )
        )

    computed = compute_zone_checksum(int(entry["zone_id"]), int(entry["tick"]), entities)
    return int(computed) == int(entry["checksum"])


def main():
    parser = argparse.ArgumentParser(description="Inspect or verify exported worker zone history")
    parser.add_argument("--input", required=True, help="Path to a JSONL history export file")
    parser.add_argument("--zone-id", type=int)
    parser.add_argument("--tail", type=int, default=8)
    parser.add_argument("--verify-checksums", action="store_true")
    args = parser.parse_args()

    entries = load_entries(args.input, zone_id_filter=args.zone_id)
    if not entries:
        raise SystemExit("No matching history entries found")

    counts = Counter(entry.get("reason_name", f"UNKNOWN_{entry.get('reason')}") for entry in entries)
    first = entries[0]
    last = entries[-1]

    print("Zone History Replay")
    print(f"input={args.input}")
    print(f"entries={len(entries)}")
    print(f"zone_id={last.get('zone_id')}")
    print(f"worker_label={last.get('worker_label')}")
    print(f"tick_range={first.get('tick')}..{last.get('tick')}")
    print(f"snapshot_range={first.get('snapshot_id')}..{last.get('snapshot_id')}")
    print(f"reason_counts={dict(counts)}")

    if args.verify_checksums:
        verified = 0
        failed = 0
        skipped = 0
        for entry in entries:
            outcome = verify_entry_checksum(entry)
            if outcome is None:
                skipped += 1
                continue
            if outcome:
                verified += 1
            else:
                failed += 1
        print(f"checksum_verified={verified}")
        print(f"checksum_failed={failed}")
        print(f"checksum_skipped={skipped}")

    tail = max(1, int(args.tail))
    print("tail:")
    for entry in entries[-tail:]:
        print(
            f"line={entry['_line']} reason={entry.get('reason_name')} "
            f"snapshot_id={entry.get('snapshot_id')} tick={entry.get('tick')} "
            f"checksum={entry.get('checksum')} entities={entry.get('entity_count')} "
            f"removed={entry.get('removed_count', 0)} upsert={entry.get('upsert_count', 0)} "
            f"recorded_at_ms={entry.get('recorded_at_ms')}"
        )


if __name__ == "__main__":
    main()
