#!/usr/bin/env python3
"""Wall-clock and peak-memory comparison of the Go oasdiff and the C# oasdelta binaries.

usage: bench.py --go GO_BINARY --cs CS_BINARY --specs DIR [--fixtures DIR] [--runs N] [--warmup N]
                [--json OUT.json] [--markdown OUT.md]

DIR holds base.yaml/revision.yaml/base.json/revision.json from tools/gen_bench_spec.py. Each
(input, command) case runs WARMUP untimed times and RUNS timed times per binary, interleaving
the two binaries so that background noise hits both equally. Wall time covers the whole process
(start-up included); peak memory is the peak resident set (Linux/macOS: wait4 rusage) or peak
working set (Windows: GetProcessMemoryInfo). stdout of the two binaries is compared byte for byte.
No third-party packages are needed; runs on Linux, macOS and Windows.
"""
import argparse
import datetime
import json
import os
import platform
import statistics
import subprocess
import sys
import time

WINDOWS = os.name == "nt"

if WINDOWS:
    import ctypes
    from ctypes import wintypes

    class _PMC(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD),
            ("PageFaultCount", wintypes.DWORD),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
        ]

    _psapi = ctypes.WinDLL("psapi")
    _psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PMC), wintypes.DWORD]
    _psapi.GetProcessMemoryInfo.restype = wintypes.BOOL


def run_once(binary, args, cwd):
    """Returns (seconds, peak_mb, exit_code, stdout)."""
    t0 = time.perf_counter()
    p = subprocess.Popen([binary, *args], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, cwd=cwd)
    out = p.stdout.read()
    if WINDOWS:
        code = p.wait()
        elapsed = time.perf_counter() - t0
        pmc = _PMC()
        pmc.cb = ctypes.sizeof(_PMC)
        if not _psapi.GetProcessMemoryInfo(int(p._handle), ctypes.byref(pmc), pmc.cb):
            raise ctypes.WinError()
        peak = pmc.PeakWorkingSetSize / (1024 * 1024)
    else:
        _, status, ru = os.wait4(p.pid, 0)
        elapsed = time.perf_counter() - t0
        code = os.waitstatus_to_exitcode(status)
        p.returncode = code
        # ru_maxrss is KiB on Linux and bytes on macOS
        peak = ru.ru_maxrss / (1024 * 1024) if sys.platform == "darwin" else ru.ru_maxrss / 1024
    p.stdout.close()
    return elapsed, peak, code, out


def stats(samples):
    times = [s[0] * 1000 for s in samples]
    return {
        "median_ms": round(statistics.median(times), 2),
        "mean_ms": round(statistics.fmean(times), 2),
        "min_ms": round(min(times), 2),
        "stddev_ms": round(statistics.stdev(times), 2) if len(times) > 1 else 0.0,
        "peak_mb": round(max(s[1] for s in samples), 1),
        "exit_code": samples[0][2],
    }


def version(binary):
    try:
        return subprocess.run([binary, "--version"], capture_output=True, text=True, timeout=30).stdout.strip()
    except Exception as e:  # noqa: BLE001
        return f"unknown ({e})"


def cpu_name():
    try:
        if sys.platform.startswith("linux"):
            with open("/proc/cpuinfo") as f:
                for line in f:
                    if line.startswith("model name"):
                        return line.split(":", 1)[1].strip()
        if WINDOWS:
            out = subprocess.run(
                ["powershell", "-NoProfile", "-Command", "(Get-CimInstance Win32_Processor | Select-Object -First 1).Name"],
                capture_output=True, text=True, timeout=30,
            ).stdout.strip()
            if out:
                return out
    except Exception:  # noqa: BLE001
        pass
    return platform.processor() or "unknown"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--go", required=True, help="Go oasdiff binary")
    ap.add_argument("--cs", required=True, help="C# oasdelta binary")
    ap.add_argument("--specs", required=True, help="directory with the generated base/revision specs")
    ap.add_argument("--fixtures", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests", "fixtures"))
    ap.add_argument("--runs", type=int, default=10)
    ap.add_argument("--warmup", type=int, default=2)
    ap.add_argument("--json", help="write results as JSON")
    ap.add_argument("--markdown", help="write (append) a Markdown table, e.g. $GITHUB_STEP_SUMMARY")
    ap.add_argument("--label", default=None, help="platform label for the report (default: OS name)")
    a = ap.parse_args()

    go, cs = os.path.abspath(a.go), os.path.abspath(a.cs)
    specs, fixtures = os.path.abspath(a.specs), os.path.abspath(a.fixtures)

    inputs = [
        ("small", "Small YAML (openapi-test1 → test3, 10 KB)", fixtures, "openapi-test1.yaml", "openapi-test3.yaml"),
        ("large-yaml", "Large YAML (synthetic, ~1.5 MB)", specs, "base.yaml", "revision.yaml"),
        ("large-json", "Large JSON (synthetic, ~1.1 MB)", specs, "base.json", "revision.json"),
    ]
    commands = [["diff"], ["summary"], ["breaking"], ["changelog", "-f", "json"], ["changelog", "-f", "html"]]

    label = a.label or {"win32": "Windows", "darwin": "macOS"}.get(sys.platform, "Linux")
    meta = {
        "label": label,
        "os": f"{platform.system()} {platform.release()}",
        "arch": platform.machine(),
        "cpu": cpu_name(),
        "cpu_count": os.cpu_count(),
        "go_version": version(go),
        "cs_version": version(cs),
        "go_binary_mb": round(os.path.getsize(go) / (1024 * 1024), 1),
        "cs_binary_mb": round(os.path.getsize(cs) / (1024 * 1024), 1),
        "runs": a.runs,
        "warmup": a.warmup,
        "date": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "commit": os.environ.get("GITHUB_SHA", ""),
        "run_url": (
            f"{os.environ['GITHUB_SERVER_URL']}/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
            if "GITHUB_RUN_ID" in os.environ else ""
        ),
    }
    sizes = {key: os.path.getsize(os.path.join(d, b)) + os.path.getsize(os.path.join(d, r)) for key, _, d, b, r in inputs}

    print(f"{label}: {meta['cpu']} ({meta['cpu_count']} cores); {meta['go_version']} vs {meta['cs_version']}", flush=True)
    print(f"{'input':<12}{'command':<20}{'go ms':>10}{'c# ms':>10}{'speed-up':>10}{'go MB':>9}{'c# MB':>9}  output", flush=True)
    results = []
    for key, title, cwd, base, rev in inputs:
        for cmd in commands:
            args = [*cmd, base, rev]
            for _ in range(a.warmup):
                run_once(go, args, cwd)
                run_once(cs, args, cwd)
            gs, cs_ = [], []
            for _ in range(a.runs):
                gs.append(run_once(go, args, cwd))
                cs_.append(run_once(cs, args, cwd))
            g, c = stats(gs), stats(cs_)
            identical = gs[0][3] == cs_[0][3] and g["exit_code"] == c["exit_code"]
            row = {
                "input": key,
                "input_title": title,
                "input_bytes": sizes[key],
                "command": " ".join(cmd),
                "go": g,
                "cs": c,
                "speedup": round(g["median_ms"] / c["median_ms"], 2),
                "memory_ratio": round(g["peak_mb"] / c["peak_mb"], 2),
                "identical": identical,
            }
            results.append(row)
            print(
                f"{key:<12}{row['command']:<20}{g['median_ms']:>10.1f}{c['median_ms']:>10.1f}{row['speedup']:>9.2f}x"
                f"{g['peak_mb']:>9.1f}{c['peak_mb']:>9.1f}  {'identical' if identical else 'DIFFERENT'}",
                flush=True,
            )

    doc = {"meta": meta, "results": results}
    if a.json:
        with open(a.json, "w", encoding="utf-8", newline="\n") as f:
            json.dump(doc, f, indent=2)
            f.write("\n")
    if a.markdown:
        with open(a.markdown, "a", encoding="utf-8", newline="\n") as f:
            f.write(f"## Benchmarks: {label}\n\n")
            f.write(f"{meta['cpu']}, {meta['cpu_count']} cores. `{meta['go_version']}` vs `{meta['cs_version']}`. ")
            f.write(f"Median of {a.runs} runs after {a.warmup} warm-ups; memory is peak RSS / working set.\n\n")
            f.write("| Input | Command | Go (ms) | C# (ms) | Speed-up | Go (MB) | C# (MB) | Output |\n")
            f.write("|---|---|---:|---:|---:|---:|---:|---|\n")
            for r in results:
                f.write(
                    f"| {r['input']} | `{r['command']}` | {r['go']['median_ms']:.1f} | {r['cs']['median_ms']:.1f} | "
                    f"**{r['speedup']:.2f}×** | {r['go']['peak_mb']:.1f} | {r['cs']['peak_mb']:.1f} | "
                    f"{'identical' if r['identical'] else 'differs'} |\n"
                )
            f.write(f"\nBinary size: Go {meta['go_binary_mb']} MB, C# {meta['cs_binary_mb']} MB.\n\n")


if __name__ == "__main__":
    main()
