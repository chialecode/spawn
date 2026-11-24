"""Everyday silent low-priority quick gate for SPAWN (Level 1 Gate).

Requires Python 3.12+ standard library only.
Executes in strict sequence with BELOW_NORMAL_PRIORITY_CLASS | CREATE_NO_WINDOW on Windows
and isolated temporary data directories (SPAWN_DATA_DIR / SPAWN_VAULT_DIR):
1. python -B scripts/check_docs.py
2. python -B scripts/test_release.py (offline release plan, manifest, signing & PDB tests)
3. python -B scripts/test_package.py (offline NSIS packaging and dev auto-install tests)
4. dotnet build Spawn.sln -c Release -nologo -v quiet
5. dotnet test Spawn.sln -c Release --no-build -nologo --verbosity minimal
   (offline network tripwire: HTTP(S)_PROXY -> dead local port)
6. git diff --check
"""

from __future__ import annotations

import argparse
import ctypes
import json
import locale
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
BUDGET_SECONDS = 300.0

BELOW_NORMAL_PRIORITY_CLASS = 0x00004000
CREATE_NO_WINDOW = 0x08000000
SEM_FAILCRITICALERRORS = 0x0001
SEM_NOGPFAULTERRORBOX = 0x0002
SEM_NOOPENFILEERRORBOX = 0x8000

CREATION_FLAGS = (BELOW_NORMAL_PRIORITY_CLASS | CREATE_NO_WINDOW) if sys.platform == "win32" else 0


def configure_silent_process_host() -> None:
    """Suppress Windows crash/error popups and lower current runner priority."""
    if sys.platform != "win32":
        return
    try:
        kernel32 = ctypes.windll.kernel32
        kernel32.SetErrorMode(
            SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX
        )
        current_process = kernel32.GetCurrentProcess()
        kernel32.SetPriorityClass(current_process, BELOW_NORMAL_PRIORITY_CLASS)
    except OSError:
        pass


def configure_console_streams() -> None:
    """Never crash on legacy console code pages (e.g. GBK/cp936): replace unencodable characters."""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            try:
                reconfigure(errors="replace")
            except (OSError, ValueError):
                pass


def decode_child_output(raw: bytes | None) -> str:
    """Decode child output as UTF-8 first, then fall back to the active ANSI/OEM code page (e.g. GBK)."""
    if not raw:
        return ""
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        fallback = locale.getpreferredencoding(False) or "mbcs"
        return raw.decode(fallback, errors="replace")


def build_isolated_env(temp_root: Path) -> dict[str, str]:
    """Build child process environment with strict temp data isolation and quiet flags."""
    data_dir = temp_root / "data"
    vault_dir = temp_root / "vault"
    data_dir.mkdir(parents=True, exist_ok=True)
    vault_dir.mkdir(parents=True, exist_ok=True)

    env = os.environ.copy()
    env.update(
        {
            "PYTHONDONTWRITEBYTECODE": "1",
            "PYTHONUTF8": "1",
            "PYTHONIOENCODING": "utf-8",
            "DOTNET_CLI_UI_LANGUAGE": "en-US",
            "VSLANG": "1033",
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
            "MSBUILDDISABLENODEREUSE": "1",
            "SPAWN_CHANNEL": "test",
            "SPAWN_DATA_DIR": str(data_dir),
            "SPAWN_VAULT_DIR": str(vault_dir),
            "SPAWN_TEST_HEADLESS": "1",
            "SPAWN_HEADLESS_TEST": "1",
            "SPAWN_MUTE_AUDIO": "1",
        }
    )
    return env


NETWORK_TRIPWIRE_PROXY = "http://127.0.0.1:9"


def build_network_tripwire_env(base_env: dict[str, str]) -> dict[str, str]:
    """Route every proxy-aware HTTP client in the test run to a dead local port so accidental real-network
    access fails loudly instead of silently depending on the Internet (tests must be 100% offline)."""
    env = dict(base_env)
    for key in ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"):
        env[key] = NETWORK_TRIPWIRE_PROXY
    for key in ("NO_PROXY", "no_proxy"):
        env.pop(key, None)
    env["SPAWN_NETWORK_TRIPWIRE"] = NETWORK_TRIPWIRE_PROXY
    return env


def parse_dotnet_test_counts(output: str) -> dict[str, int]:
    """Extract aggregated passed/failed/skipped/total/suites counts from dotnet test output."""
    passed = 0
    failed = 0
    skipped = 0
    total = 0
    suites = 0

    # Matches English and Chinese vstest/dotnet test minimal summary lines.
    en_pattern = re.compile(
        r"Passed!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
        re.I,
    )
    en_fail_pattern = re.compile(
        r"Failed!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
        re.I,
    )
    zh_pattern = re.compile(
        r"(?:已通过|通过|失败)!\s*-\s*失败:\s*(\d+)[，,]\s*(?:已)?通过:\s*(\d+)[，,]\s*已跳过:\s*(\d+)[，,]\s*总计:\s*(\d+)"
    )

    for line in output.splitlines():
        m = en_pattern.search(line) or en_fail_pattern.search(line) or zh_pattern.search(line)
        if m:
            suites += 1
            failed += int(m.group(1))
            passed += int(m.group(2))
            skipped += int(m.group(3))
            total += int(m.group(4))

    if suites == 0:
        return {}
    return {
        "suites": suites,
        "passed": passed,
        "failed": failed,
        "skipped": skipped,
        "total": total,
    }


def sanitize_output(text: str) -> str:
    """Strip audible bell characters so terminal output stays 100% silent."""
    return text.replace("\x07", "")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=Path,
        default=ROOT,
        help="Repository root directory.",
    )
    args = parser.parse_args()
    repo_root = args.root.resolve()

    configure_silent_process_host()
    configure_console_streams()
    started = time.perf_counter()
    steps: list[dict[str, object]] = []
    test_summary: dict[str, int] = {}

    commands: tuple[tuple[str, list[str]], ...] = (
        ("check_docs", [sys.executable, "-B", "scripts/check_docs.py"]),
        ("test_release", [sys.executable, "-B", "scripts/test_release.py"]),
        ("test_package", [sys.executable, "-B", "scripts/test_package.py"]),
        ("dotnet_build", ["dotnet", "build", "Spawn.sln", "-c", "Release", "-nologo", "-v", "quiet"]),
        (
            "dotnet_test",
            [
                "dotnet",
                "test",
                "Spawn.sln",
                "-c",
                "Release",
                "--no-build",
                "-nologo",
                "--verbosity",
                "minimal",
            ],
        ),
        ("git_diff_check", ["git", "diff", "--check"]),
    )

    with tempfile.TemporaryDirectory(prefix="spawn-gate-") as tmp_dir_str:
        env = build_isolated_env(Path(tmp_dir_str))

        for step_name, cmd in commands:
            step_start = time.perf_counter()
            shown_cmd = " ".join(cmd if cmd[0] != sys.executable else ["python", *cmd[1:]])
            print(f"[quick-gate] -> {step_name}: {shown_cmd}", flush=True)

            step_env = build_network_tripwire_env(env) if step_name in {"dotnet_test", "test_release", "test_package"} else env
            proc = subprocess.run(
                cmd,
                cwd=repo_root,
                env=step_env,
                stdin=subprocess.DEVNULL,
                capture_output=True,
                creationflags=CREATION_FLAGS,
                check=False,
            )
            proc_stdout = decode_child_output(proc.stdout)
            proc_stderr = decode_child_output(proc.stderr)

            # Also verify staged diff hygiene during git_diff_check.
            extra_code = 0
            extra_out = ""
            if step_name == "git_diff_check" and proc.returncode == 0:
                staged = subprocess.run(
                    ["git", "diff", "--cached", "--check"],
                    cwd=repo_root,
                    env=env,
                    stdin=subprocess.DEVNULL,
                    capture_output=True,
                    creationflags=CREATION_FLAGS,
                    check=False,
                )
                extra_code = staged.returncode
                extra_out = decode_child_output(staged.stdout) + decode_child_output(staged.stderr)

            elapsed = round(time.perf_counter() - step_start, 2)
            combined_stdout = sanitize_output(proc_stdout + extra_out).strip()
            combined_stderr = sanitize_output(proc_stderr).strip()
            exit_code = proc.returncode if proc.returncode != 0 else extra_code
            ok = exit_code == 0

            if combined_stdout:
                print(combined_stdout, flush=True)
            if combined_stderr:
                print(combined_stderr, file=sys.stderr, flush=True)

            step_record: dict[str, object] = {
                "name": step_name,
                "ok": ok,
                "exitCode": exit_code,
                "seconds": elapsed,
            }
            if step_name == "dotnet_test":
                counts = parse_dotnet_test_counts(f"{combined_stdout}\n{combined_stderr}")
                if counts:
                    test_summary = counts
                    step_record["tests"] = counts

            steps.append(step_record)
            status_word = "PASS" if ok else f"FAIL (exit {exit_code})"
            print(f"[quick-gate] <- {step_name}: {status_word} ({elapsed:.2f}s)", flush=True)

            if not ok:
                total_elapsed = round(time.perf_counter() - started, 2)
                summary = {
                    "status": "failed",
                    "gate": "quick-gate",
                    "seconds": total_elapsed,
                    "budgetSeconds": BUDGET_SECONDS,
                    "overBudget": total_elapsed > BUDGET_SECONDS,
                    "tests": test_summary,
                    "steps": steps,
                }
                print(json.dumps(summary, ensure_ascii=False), flush=True)
                return exit_code or 1

    total_elapsed = round(time.perf_counter() - started, 2)
    summary = {
        "status": "passed",
        "gate": "quick-gate",
        "seconds": total_elapsed,
        "budgetSeconds": BUDGET_SECONDS,
        "overBudget": total_elapsed > BUDGET_SECONDS,
        "tests": test_summary,
        "steps": steps,
    }
    print(json.dumps(summary, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
