#!/usr/bin/env python3
import argparse
import math
import os
import re
import subprocess
import sys


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
LOAD_TEST_PATH = os.path.join(SCRIPT_DIR, "load_test.py")
SCENARIOS = (
    (100, 2, 2),
    (100, 4, 4),
    (500, 2, 2),
    (500, 4, 4),
    (5000, 2, 2),
    (5000, 4, 4),
)


def parse_metrics(output_text):
    metrics = {}
    for line in output_text.splitlines():
        for key, value in re.findall(r"([a-zA-Z0-9_]+)=([^\s]+)", line):
            metrics[key] = value
    return metrics


def run_scenario(args, scenario_index, requested_total, grid_columns, grid_rows):
    zone_count = grid_columns * grid_rows
    entities_per_zone = int(math.ceil(requested_total / float(zone_count)))
    actual_total = entities_per_zone * zone_count
    base_port = args.base_port + (scenario_index * args.port_stride) if args.spawn_local_workers else args.base_port

    command = [
        sys.executable,
        LOAD_TEST_PATH,
        "--workers",
        str(args.workers),
        "--base-port",
        str(base_port),
        "--grid-columns",
        str(grid_columns),
        "--grid-rows",
        str(grid_rows),
        "--entities-per-zone",
        str(entities_per_zone),
        "--ticks",
        str(args.ticks),
        "--tick-interval",
        str(args.tick_interval),
        "--timeout-ms",
        str(args.timeout_ms),
        "--result-mode",
        args.result_mode,
    ]

    if args.spawn_local_workers:
        command.append("--spawn-local-workers")
    if args.worker_delay_ms > 0:
        command.extend(["--worker-delay-ms", str(args.worker_delay_ms)])
    if args.worker_jitter_ms > 0:
        command.extend(["--worker-jitter-ms", str(args.worker_jitter_ms)])
    if args.worker_drop_rate > 0:
        command.extend(["--worker-drop-rate", str(args.worker_drop_rate)])

    result = subprocess.run(command, capture_output=True, text=True)
    metrics = parse_metrics(result.stdout)

    return {
        "scenario": f"{requested_total} @ {grid_columns}x{grid_rows}",
        "requested_total": requested_total,
        "actual_total": actual_total,
        "grid": f"{grid_columns}x{grid_rows}",
        "zone_count": zone_count,
        "entities_per_zone": entities_per_zone,
        "base_port": base_port,
        "returncode": result.returncode,
        "stdout": result.stdout,
        "stderr": result.stderr,
        "metrics": metrics,
    }


def print_result_table(results):
    print("Benchmark Matrix")
    print("")
    print(
        "{:<14} {:>8} {:>8} {:>9} {:>9} {:>9} {:>9} {:>12} {:>12}".format(
            "scenario",
            "actual",
            "ok",
            "timeouts",
            "p50_ms",
            "p95_ms",
            "mean_ms",
            "throughput",
            "rx_bytes",
        )
    )

    for result in results:
        metrics = result["metrics"]
        if result["returncode"] != 0:
            print(
                "{:<14} {:>8} {:>8} {:>9} {:>9} {:>9} {:>9} {:>12} {:>12}".format(
                    result["scenario"],
                    result["actual_total"],
                    "ERR",
                    "-",
                    "-",
                    "-",
                    "-",
                    "-",
                    "-",
                )
            )
            continue

        print(
            "{:<14} {:>8} {:>8} {:>9} {:>9} {:>9} {:>9} {:>12} {:>12}".format(
                result["scenario"],
                result["actual_total"],
                metrics.get("successful_results", "?"),
                metrics.get("timeouts", "?"),
                metrics.get("latency_p50_ms", "?"),
                metrics.get("latency_p95_ms", "?"),
                metrics.get("latency_mean_ms", "?"),
                metrics.get("throughput_entities_per_s", "?"),
                metrics.get("network_bytes_received", "?"),
            )
        )


def print_failures(results):
    failures = [result for result in results if result["returncode"] != 0]
    if not failures:
        return

    print("")
    print("Errores")
    for result in failures:
        print(f"- {result['scenario']} fallo con codigo {result['returncode']} usando base-port {result['base_port']}")
        if result["stderr"].strip():
            print(result["stderr"].strip())
        elif result["stdout"].strip():
            print(result["stdout"].strip())


def main():
    parser = argparse.ArgumentParser(description="Benchmark matrix for the distributed Unity/Raspberry prototype")
    parser.add_argument("--workers", type=int, default=2)
    parser.add_argument("--base-port", type=int, default=7002)
    parser.add_argument("--port-stride", type=int, default=10)
    parser.add_argument("--ticks", type=int, default=60)
    parser.add_argument("--tick-interval", type=float, default=0.05)
    parser.add_argument("--timeout-ms", type=int, default=700)
    parser.add_argument("--result-mode", choices=("full", "delta"), default="delta")
    parser.add_argument("--spawn-local-workers", action="store_true")
    parser.add_argument("--worker-delay-ms", type=float, default=0.0)
    parser.add_argument("--worker-jitter-ms", type=float, default=0.0)
    parser.add_argument("--worker-drop-rate", type=float, default=0.0)
    args = parser.parse_args()

    results = []
    for scenario_index, scenario in enumerate(SCENARIOS):
        results.append(run_scenario(args, scenario_index, scenario[0], scenario[1], scenario[2]))

    print_result_table(results)
    print_failures(results)


if __name__ == "__main__":
    main()
