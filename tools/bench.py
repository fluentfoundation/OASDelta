#!/usr/bin/env python3
"""Wall-clock and peak-memory comparison of the Go oasdiff and the C# oasdelta binaries.

usage: bench.py --go GO_BINARY --cs CS_BINARY --specs DIR [--hyperfine PATH] [--fixtures DIR] [--runs N]
                [--warmup N] [--real-world TESTDATA_DIR [--real-world-runs N]] [--json OUT.json] [--markdown OUT.md]

DIR holds base.yaml/revision.yaml/base.json/revision.json from tools/gen_bench_spec.py. With --real-world, pairs of
real API releases from a checkout of OASDelta-TestData are added.

This script measures nothing itself. For each (input, command) case:

1. Output check, untimed: each binary runs once with stdout to a file. The two files and exit codes are compared:
   "identical" (byte for byte), "reordered" (the same lines, or the same JSON array entries, in a different order) or
   "different". For a reordered case each binary runs a few more times, to record how many distinct outputs each
   prints: oasdiff prints some changes in Go's random map order.
2. Timing: hyperfine (https://github.com/sharkdp/hyperfine, 2.0 or later) runs WARMUP untimed and RUNS timed runs
   per binary, with no shell in between and stdout discarded. Which binary goes first alternates from case to case.
3. Peak memory: on Linux and macOS, hyperfine's peak resident set size over the timed runs. hyperfine cannot measure
   it on Windows, so there each binary runs MEMORY_RUNS more times, untimed, and the peak working set comes from
   GetProcessMemoryInfo.

No third-party Python packages are needed; runs on Linux, macOS and Windows.
"""
import argparse
import collections
import datetime
import filecmp
import hashlib
import json
import os
import platform
import shlex
import shutil
import subprocess
import sys
import tempfile

WINDOWS = os.name == "nt"
MB = 1024 * 1024
MEMORY_RUNS = 3  # Windows only: untimed runs per binary for the peak working set
ORDER_RUNS = 5  # runs per binary that count distinct outputs, for a reordered case

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


def run_to_file(binary, args, cwd, out_path):
    """Runs once with stdout to out_path (or discarded if None). Returns (exit code, Windows peak working set in MB or
    None). The peak working set is the process's own, whatever the parent holds; this is never timed."""
    with open(out_path or os.devnull, "wb") as out:
        p = subprocess.Popen([binary, *args], stdout=out, stderr=subprocess.DEVNULL, cwd=cwd)
        code = p.wait()
        peak = None
        if WINDOWS:
            pmc = _PMC()
            pmc.cb = ctypes.sizeof(_PMC)
            if not _psapi.GetProcessMemoryInfo(int(p._handle), ctypes.byref(pmc), pmc.cb):
                raise ctypes.WinError()
            peak = pmc.PeakWorkingSetSize / MB
    return code, peak


def same_entries(a_path, b_path):
    """The same JSON array entries, or failing that the same lines, in any order."""
    with open(a_path, "rb") as f:
        a = f.read()
    with open(b_path, "rb") as f:
        b = f.read()
    if a.lstrip()[:1] == b"[" and b.lstrip()[:1] == b"[":
        try:
            ja, jb = json.loads(a), json.loads(b)
            key = lambda e: json.dumps(e, sort_keys=True, ensure_ascii=False)  # noqa: E731
            return sorted(map(key, ja)) == sorted(map(key, jb))
        except ValueError:
            pass
    return sorted(a.splitlines()) == sorted(b.splitlines())


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def distinct_outputs(binary, args, cwd, tmp, first):
    """How many different outputs the binary prints over ORDER_RUNS runs (the first one already made)."""
    seen = {digest(first)}
    path = os.path.join(tmp, "order.out")
    for _ in range(ORDER_RUNS - 1):
        run_to_file(binary, args, cwd, path)
        seen.add(digest(path))
    return len(seen)


def check_output(go, cs, args, cwd, tmp):
    go_out, cs_out = os.path.join(tmp, "go.out"), os.path.join(tmp, "cs.out")
    go_code, go_peak = run_to_file(go, args, cwd, go_out)
    cs_code, cs_peak = run_to_file(cs, args, cwd, cs_out)
    result = {"go_exit": go_code, "cs_exit": cs_code, "go_peak": go_peak, "cs_peak": cs_peak}
    if go_code == cs_code and filecmp.cmp(go_out, cs_out, shallow=False):
        result["output"] = "identical"
    elif go_code == cs_code and same_entries(go_out, cs_out):
        result["output"] = "reordered"
        result["order_runs"] = ORDER_RUNS
        result["go_orders"] = distinct_outputs(go, args, cwd, tmp, go_out)
        result["cs_orders"] = distinct_outputs(cs, args, cwd, tmp, cs_out)
    else:
        result["output"] = "different"
    return result


def windows_peak(binary, args, cwd, first):
    peaks = [first] + [run_to_file(binary, args, cwd, None)[1] for _ in range(MEMORY_RUNS - 1)]
    return max(peaks)


def hyperfine(hf, cases, cwd, warmup, runs, metrics, tmp):
    """cases: [(name, argv)]. Returns {name: hyperfine v2 result}."""
    export = os.path.join(tmp, "hyperfine.json")
    cmd = [hf, "--shell=none", "--style=basic", f"--warmup={warmup}", f"--runs={runs}", "--ignore-failure",
           f"--metrics={metrics}", f"--export-json={export}"]
    for name, argv in cases:
        cmd += ["--command-name", name, " ".join(shlex.quote(a) for a in argv)]
    subprocess.run(cmd, cwd=cwd, check=True, stdout=subprocess.DEVNULL)
    with open(export, encoding="utf-8") as f:
        doc = json.load(f)
    if doc.get("schema_version") != 2:
        sys.exit(f"{hf}: expected hyperfine 2 (--export-json schema_version 2)")
    return {r["name"]: r for r in doc["results"]}


def measurement(r, code, windows_peak_mb):
    t = r["summary"]["time_wall_clock"]
    m = {
        "median_ms": round(t["median"] * 1000, 2),
        "mean_ms": round(t["mean"] * 1000, 2),
        "min_ms": round(t["min"] * 1000, 2),
        "stddev_ms": round(t["stddev"] * 1000, 2),
    }
    if windows_peak_mb is not None:
        m["peak_mb"] = round(windows_peak_mb, 1)
    else:
        m["peak_mb"] = round(r["summary"]["memory_peak_resident"]["max"] / MB, 1)
    m["exit_code"] = code
    return m


def tool_version(binary):
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


def memory_floor(hf, tmp):
    """hyperfine's own contribution to a peak RSS on Linux: what it reports for a program that does nothing."""
    true = shutil.which("true")
    if WINDOWS or not true:
        return None
    r = hyperfine(hf, [("true", [true])], tmp, 1, 5, "memory_peak_resident", tmp)["true"]
    return round(r["summary"]["memory_peak_resident"]["max"] / MB, 1)


# pairs from https://github.com/fluentfoundation/OASDelta-TestData (pairs.json ids)
REAL_WORLD = [
    ("github", "GitHub REST API (YAML, ~10 MB, Aug → Nov 2026)", "github-rest/api.github.com.yaml@2026-08-28..2026-11-06"),
    ("stripe", "Stripe API (JSON, ~8 MB, v2500 → v2598)", "stripe/spec3.json@v2500..v2598"),
    ("k8s-core", "Kubernetes core/v1 (JSON, ~2 MB, 1.36 → 1.37)", "kubernetes/core-v1.json@v1.36.0..v1.37.0"),
]

OUTPUT_LABEL = {"identical": "identical", "reordered": "same entries, other order", "different": "DIFFERENT"}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--go", required=True, help="Go oasdiff binary")
    ap.add_argument("--cs", required=True, help="C# oasdelta binary")
    ap.add_argument("--specs", required=True, help="directory with the generated base/revision specs")
    ap.add_argument("--hyperfine", default=shutil.which("hyperfine"), help="hyperfine 2 binary (default: on PATH)")
    ap.add_argument("--fixtures", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests", "fixtures"))
    ap.add_argument("--runs", type=int, default=10)
    ap.add_argument("--warmup", type=int, default=2)
    ap.add_argument("--json", help="write results as JSON")
    ap.add_argument("--markdown", help="write (append) a Markdown table, e.g. $GITHUB_STEP_SUMMARY")
    ap.add_argument("--label", default=None, help="platform label for the report (default: OS name)")
    ap.add_argument("--real-world", help="a checkout of OASDelta-TestData: also benchmark real API releases from it")
    ap.add_argument("--real-world-runs", type=int, default=5)
    ap.add_argument("--real-world-commit", help="the OASDelta-TestData commit, for the report")
    a = ap.parse_args()
    if not a.hyperfine:
        sys.exit("hyperfine not found: install hyperfine 2 (https://github.com/sharkdp/hyperfine) or pass --hyperfine")

    go, cs, hf = os.path.abspath(a.go), os.path.abspath(a.cs), os.path.abspath(a.hyperfine)
    specs, fixtures = os.path.abspath(a.specs), os.path.abspath(a.fixtures)

    commands = [["diff"], ["summary"], ["breaking"], ["changelog", "-f", "json"], ["changelog", "-f", "html"]]
    # (key, title, kind, cwd, base, revision, commands, runs, warmup)
    inputs = [
        ("small", "Small YAML (openapi-test1 → test3, 10 KB)", "synthetic", fixtures, "openapi-test1.yaml", "openapi-test3.yaml", commands, a.runs, a.warmup),
        ("large-yaml", "Large YAML (synthetic, ~1.5 MB)", "synthetic", specs, "base.yaml", "revision.yaml", commands, a.runs, a.warmup),
        ("large-json", "Large JSON (synthetic, ~1.1 MB)", "synthetic", specs, "base.json", "revision.json", commands, a.runs, a.warmup),
    ]
    if a.real_world:
        # real API releases from OASDelta-TestData: each case takes seconds, so fewer runs
        root = os.path.abspath(a.real_world)
        with open(os.path.join(root, "pairs.json"), encoding="utf-8") as f:
            pairs = {p["id"]: p for p in json.load(f)["pairs"]}
        rw_commands = [["diff"], ["breaking"], ["changelog", "-f", "json"]]
        for key, title, pid in REAL_WORLD:
            p = pairs[pid]
            cmds = [c for c in rw_commands if c[0] not in p.get("skip_commands", {})]
            inputs.append((key, title, "real-world", root, p["base"], p["revision"], cmds, a.real_world_runs, 1))

    metrics = "time_wall_clock" if WINDOWS else "time_wall_clock,memory_peak_resident"
    tmp = tempfile.mkdtemp(prefix="oasdelta-bench-")
    label = a.label or {"win32": "Windows", "darwin": "macOS"}.get(sys.platform, "Linux")
    meta = {
        "label": label,
        "os": f"{platform.system()} {platform.release()}",
        "arch": platform.machine(),
        "cpu": cpu_name(),
        "cpu_count": os.cpu_count(),
        "go_version": tool_version(go),
        "cs_version": tool_version(cs),
        "go_binary_mb": round(os.path.getsize(go) / MB, 1),
        "cs_binary_mb": round(os.path.getsize(cs) / MB, 1),
        "runs": a.runs,
        "warmup": a.warmup,
        "harness": tool_version(hf),
        "memory": (
            f"peak working set (GetProcessMemoryInfo), highest of {MEMORY_RUNS} untimed runs" if WINDOWS
            else "peak resident set size (hyperfine), highest of the timed runs"
        ),
        "memory_floor_mb": memory_floor(hf, tmp),
        "date": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "commit": os.environ.get("GITHUB_SHA", ""),
        "run_url": (
            f"{os.environ['GITHUB_SERVER_URL']}/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
            if "GITHUB_RUN_ID" in os.environ else ""
        ),
        "real_world_runs": a.real_world_runs if a.real_world else 0,
        "test_data_commit": a.real_world_commit or "",
    }
    sizes = {i[0]: os.path.getsize(os.path.join(i[3], i[4])) + os.path.getsize(os.path.join(i[3], i[5])) for i in inputs}

    print(f"{label}: {meta['cpu']} ({meta['cpu_count']} cores); {meta['go_version']} vs {meta['cs_version']}; {meta['harness']}", flush=True)
    print(f"{'input':<12}{'command':<20}{'go ms':>10}{'c# ms':>10}{'speed-up':>10}{'go MB':>9}{'c# MB':>9}  output", flush=True)
    results = []
    n = 0
    try:
        for key, title, kind, cwd, base, rev, cmds, runs, warmup in inputs:
            for cmd in cmds:
                args = [*cmd, base, rev]
                check = check_output(go, cs, args, cwd, tmp)
                gp = windows_peak(go, args, cwd, check["go_peak"]) if WINDOWS else None
                cp = windows_peak(cs, args, cwd, check["cs_peak"]) if WINDOWS else None
                # alternate which binary runs first, so that a slow spell on the runner does not always hit the same one
                order = [("go", [go, *args]), ("cs", [cs, *args])]
                timed = hyperfine(hf, order if n % 2 == 0 else order[::-1], cwd, warmup, runs, metrics, tmp)
                n += 1
                g = measurement(timed["go"], check["go_exit"], gp)
                c = measurement(timed["cs"], check["cs_exit"], cp)
                row = {
                    "input": key,
                    "input_title": title,
                    "kind": kind,
                    "runs": runs,
                    "input_bytes": sizes[key],
                    "command": " ".join(cmd),
                    "go": g,
                    "cs": c,
                    "speedup": round(g["median_ms"] / c["median_ms"], 2),
                    "memory_ratio": round(g["peak_mb"] / c["peak_mb"], 2),
                    "identical": check["output"] == "identical",
                    "output": check["output"],
                }
                if check["output"] == "reordered":
                    row["order_runs"], row["go_orders"], row["cs_orders"] = check["order_runs"], check["go_orders"], check["cs_orders"]
                results.append(row)
                note = OUTPUT_LABEL[check["output"]]
                if check["output"] == "reordered":
                    note += f" (distinct outputs in {ORDER_RUNS} runs: oasdiff {row['go_orders']}, oasdelta {row['cs_orders']})"
                print(
                    f"{key:<12}{row['command']:<20}{g['median_ms']:>10.1f}{c['median_ms']:>10.1f}{row['speedup']:>9.2f}x"
                    f"{g['peak_mb']:>9.1f}{c['peak_mb']:>9.1f}  {note}",
                    flush=True,
                )
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    doc = {"meta": meta, "results": results}
    if a.json:
        with open(a.json, "w", encoding="utf-8", newline="\n") as f:
            json.dump(doc, f, indent=2, ensure_ascii=False)
            f.write("\n")
    if a.markdown:
        outcomes = collections.Counter(r["output"] for r in results)
        with open(a.markdown, "a", encoding="utf-8", newline="\n") as f:
            f.write(f"## Benchmarks: {label}\n\n")
            f.write(f"{meta['cpu']}, {meta['cpu_count']} cores. `{meta['go_version']}` vs `{meta['cs_version']}`, timed by `{meta['harness']}`. ")
            f.write(f"Median of {a.runs} runs after {a.warmup} warm-ups (real-world: {a.real_world_runs} after 1). Memory: {meta['memory']}")
            if meta["memory_floor_mb"] is not None:
                f.write(f"; hyperfine reports {meta['memory_floor_mb']} MB for a program that does nothing")
            f.write(".\n\n")
            f.write("| Input | Command | Go (ms) | C# (ms) | Speed-up | Go (MB) | C# (MB) | Output |\n")
            f.write("|---|---|---:|---:|---:|---:|---:|---|\n")
            for r in results:
                f.write(
                    f"| {r['input']} | `{r['command']}` | {r['go']['median_ms']:.1f} | {r['cs']['median_ms']:.1f} | "
                    f"**{r['speedup']:.2f}×** | {r['go']['peak_mb']:.1f} | {r['cs']['peak_mb']:.1f} | {OUTPUT_LABEL[r['output']]} |\n"
                )
            f.write(f"\nOutput: {outcomes['identical']} identical, {outcomes['reordered']} same entries in another order, "
                    f"{outcomes['different']} different. Binary size: Go {meta['go_binary_mb']} MB, C# {meta['cs_binary_mb']} MB.\n\n")


if __name__ == "__main__":
    main()
