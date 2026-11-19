"""Per-user Inno Setup 7 packager and automatic SPAWN Dev installer.

Requires Python 3.12+ standard library only (zero pip dependencies) + .NET 10 SDK + Inno Setup 7.

Usage examples:
  python -B scripts/package.py --channel dev
  python -B scripts/package.py --install-dev
  python -B scripts/package.py --identity spawn-dev-test --install --no-relaunch
  python -B scripts/package.py --identity spawn-dev-test --uninstall
"""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
from dataclasses import dataclass
from datetime import UTC, datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

if os.name == "nt":
    import winreg

NEXT_BASE_VERSION = "0.1.1"
QUIT_TIMEOUT_SECONDS = 30.0
QUIT_POLL_SECONDS = 0.25

BELOW_NORMAL_PRIORITY_CLASS = 0x00004000
CREATE_NO_WINDOW = 0x08000000
DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000

TH32CS_SNAPPROCESS = 0x00000002
PROCESS_QUERY_LIMITED_INFORMATION = 0x00001000
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


@dataclass(frozen=True)
class IdentityProfile:
    identity: str
    channel: str
    display_name: str
    app_guid: str
    aumid: str
    dev_like: bool
    default_shortcuts: bool


IDENTITY_PROFILES: dict[str, IdentityProfile] = {
    "spawn": IdentityProfile(
        identity="spawn",
        channel="release",
        display_name="SPAWN",
        app_guid="B7E91A42-6D30-4C95-9E18-2F4A8C705101",
        aumid="ChialeCode.Spawn",
        dev_like=False,
        default_shortcuts=True,
    ),
    "spawn-dev": IdentityProfile(
        identity="spawn-dev",
        channel="dev",
        display_name="SPAWN Dev",
        app_guid="B7E91A42-6D30-4C95-9E18-2F4A8C705102",
        aumid="ChialeCode.Spawn.Dev",
        dev_like=True,
        default_shortcuts=True,
    ),
    "spawn-test": IdentityProfile(
        identity="spawn-test",
        channel="release",
        display_name="SPAWN Test",
        app_guid="B7E91A42-6D30-4C95-9E18-2F4A8C705103",
        aumid="ChialeCode.Spawn.Test",
        dev_like=False,
        default_shortcuts=False,
    ),
    "spawn-dev-test": IdentityProfile(
        identity="spawn-dev-test",
        channel="dev",
        display_name="SPAWN Dev Test",
        app_guid="B7E91A42-6D30-4C95-9E18-2F4A8C705104",
        aumid="ChialeCode.Spawn.DevTest",
        dev_like=True,
        default_shortcuts=False,
    ),
}


def _configure_windows_quiet_mode() -> None:
    if os.name != "nt":
        return
    try:
        kernel32 = ctypes.windll.kernel32
        # SEM_FAILCRITICALERRORS (0x0001) | SEM_NOGPFAULTERRORBOX (0x0002)
        kernel32.SetErrorMode(0x0001 | 0x0002)
        kernel32.SetPriorityClass(kernel32.GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS)
    except OSError:
        pass


def _run_quiet(
    cmd: list[str],
    *,
    cwd: Path,
    env: dict[str, str] | None = None,
    timeout: float = 300.0,
) -> subprocess.CompletedProcess[str]:
    flags = (BELOW_NORMAL_PRIORITY_CLASS | CREATE_NO_WINDOW) if os.name == "nt" else 0
    merged_env = os.environ.copy()
    merged_env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    merged_env["DOTNET_NOLOGO"] = "1"
    merged_env["MSBUILDDISABLENODEREUSE"] = "1"
    if env:
        merged_env.update(env)
    return subprocess.run(
        cmd,
        cwd=str(cwd),
        env=merged_env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=flags,
        timeout=timeout,
        check=False,
    )


def find_iscc() -> Path:
    override = os.environ.get("SPAWN_ISCC_PATH", "").strip()
    if override and Path(override).is_file():
        return Path(override)

    found = shutil.which("ISCC") or shutil.which("iscc")
    if found:
        return Path(found)

    candidates: list[Path] = []
    local_app_data = os.environ.get("LOCALAPPDATA", "")
    if local_app_data:
        candidates.append(Path(local_app_data) / "Programs" / "Inno Setup 7" / "ISCC.exe")
    for env_key in ("ProgramFiles(x86)", "ProgramFiles"):
        pf = os.environ.get(env_key, "")
        if pf:
            candidates.append(Path(pf) / "Inno Setup 7" / "ISCC.exe")

    for cand in candidates:
        if cand.is_file():
            return cand

    raise FileNotFoundError(
        "Inno Setup 7 compiler (ISCC.exe) not found in PATH, %LOCALAPPDATA%\\Programs\\Inno Setup 7, "
        "or %ProgramFiles(x86)%\\Inno Setup 7."
    )


def resolve_git_short_sha(repo_root: Path) -> str:
    proc = _run_quiet(["git", "rev-parse", "--short=12", "HEAD"], cwd=repo_root, timeout=10.0)
    if proc.returncode == 0 and proc.stdout.strip():
        return proc.stdout.strip()
    return "unknown"


def compute_numeric_version(version_str: str) -> str:
    m = re.match(r"^(\d+)\.(\d+)\.(\d+)", version_str.strip())
    if not m:
        return "0.1.1.0"
    return f"{m.group(1)}.{m.group(2)}.{m.group(3)}.0"


def build_third_party_notices(repo_root: Path) -> str:
    props_path = repo_root / "Directory.Packages.props"
    packages: list[tuple[str, str]] = []
    if props_path.is_file():
        try:
            tree = ET.parse(props_path)
            for elem in tree.iter():
                if elem.tag.endswith("PackageVersion"):
                    inc = elem.attrib.get("Include", "").strip()
                    ver = elem.attrib.get("Version", "").strip()
                    if inc:
                        packages.append((inc, ver))
        except ET.ParseError:
            pass

    lines = [
        "SPAWN — Third-Party Open-Source Notices",
        "=======================================",
        "",
        "SPAWN itself is licensed under the Apache License, Version 2.0 (see LICENSE).",
        "This distribution dynamically references the following permissive open-source",
        "NuGet packages, each retaining its upstream copyright and license terms:",
        "",
    ]
    for pkg, ver in sorted(packages):
        lines.append(f"  - {pkg} {ver}")
    lines.extend(
        [
            "",
            "Optional offline Steam emulator components (Detanup01/gbe_fork, LGPL-3.0) are",
            "never statically linked or bundled inside SPAWN executables; when downloaded",
            "on user demand into the local cache, they remain dynamically replaceable DLLs",
            "accompanied by their upstream LGPL-3.0 license text.",
            "",
        ]
    )
    return "\n".join(lines)


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fp:
        while chunk := fp.read(1024 * 1024):
            h.update(chunk)
    return h.hexdigest()


def ensure_icons(repo_root: Path) -> tuple[Path, Path]:
    assets_dir = repo_root / "assets"
    rel_ico = assets_dir / "icon.ico"
    dev_ico = assets_dir / "icon-dev.ico"
    if not rel_ico.is_file() or not dev_ico.is_file():
        proc = _run_quiet([sys.executable, "-B", "scripts/generate_icons.py"], cwd=repo_root, timeout=120.0)
        if proc.returncode != 0:
            raise RuntimeError(f"generate_icons.py failed:\n{proc.stdout}\n{proc.stderr}")
    return rel_ico, dev_ico


def build_package(
    repo_root: Path,
    profile: IdentityProfile,
    version: str,
    *,
    create_shortcuts: bool,
    self_contained: bool,
) -> tuple[Path, Path]:
    """Build executables and Inno Setup installer into dist/<identity>/ via atomic staging."""
    rel_ico, dev_ico = ensure_icons(repo_root)
    icon_file = dev_ico if profile.dev_like else rel_ico
    iscc_path = find_iscc()
    commit_sha = resolve_git_short_sha(repo_root)
    now_utc = datetime.now(UTC)
    build_time_utc = now_utc.strftime("%Y-%m-%dT%H:%M:%SZ")
    numeric_version = compute_numeric_version(version)
    desktop_exe = f"{profile.identity}.exe"
    installer_base = f"{profile.identity}-setup-{version}-x64"

    dist_root = repo_root / "dist"
    dist_root.mkdir(parents=True, exist_ok=True)
    stage_dir = dist_root / f".staging-{profile.identity}-{now_utc.strftime('%Y%m%d%H%M%S')}-{os.getpid()}"
    stage_app = stage_dir / "app"
    stage_obj_root = stage_dir / ".msbuild-out"

    if stage_dir.exists():
        shutil.rmtree(stage_dir, ignore_errors=True)
    stage_app.mkdir(parents=True, exist_ok=True)

    try:
        sc_value = "true" if self_contained else "false"
        common_props = [
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            sc_value,
            "-nologo",
            # Never leave reusable MSBuild worker nodes behind: they inherit redirected pipes and
            # keep parent `dotnet test` / quick_gate runs hanging or holding files open.
            "-nr:false",
            f"-p:SpawnChannel={profile.channel}",
            f"-p:SpawnIdentity={profile.identity}",
            f"-p:SpawnVersion={version}",
            f"-p:SpawnCommitSha={commit_sha}",
            f"-p:SpawnBuildTimeUtc={build_time_utc}",
            f"-p:Version={numeric_version}",
            f"-p:InformationalVersion={version}+{commit_sha}",
            f"-p:SpawnApplicationIcon={icon_file}",
        ]

        print(f"[package] publishing Spawn.OverlayHost ({profile.identity} {version})...")
        pub_overlay = _run_quiet(
            [
                "dotnet",
                "publish",
                "src/Spawn.OverlayHost/Spawn.OverlayHost.csproj",
                *common_props,
                "-o",
                str(stage_app),
            ],
            cwd=repo_root,
        )
        if pub_overlay.returncode != 0:
            raise RuntimeError(
                f"dotnet publish Spawn.OverlayHost failed (exit {pub_overlay.returncode}):\n"
                f"{pub_overlay.stdout}\n{pub_overlay.stderr}"
            )

        print(f"[package] publishing Spawn.Host -> {desktop_exe}...")
        pub_host = _run_quiet(
            [
                "dotnet",
                "publish",
                "src/Spawn.Host/Spawn.Host.csproj",
                *common_props,
                f"-p:SpawnHostAssemblyName={profile.identity}",
                "-o",
                str(stage_app),
            ],
            cwd=repo_root,
        )
        if pub_host.returncode != 0:
            raise RuntimeError(
                f"dotnet publish Spawn.Host failed (exit {pub_host.returncode}):\n"
                f"{pub_host.stdout}\n{pub_host.stderr}"
            )

        if stage_obj_root.exists():
            shutil.rmtree(stage_obj_root, ignore_errors=True)

        # Copy icons & compliance metadata into both app/ and package root
        app_assets = stage_app / "assets"
        app_assets.mkdir(parents=True, exist_ok=True)
        for asset_name in ("icon.ico", "icon.png", "icon-dev.ico", "icon-dev.png"):
            src_asset = repo_root / "assets" / asset_name
            if src_asset.is_file():
                shutil.copy2(src_asset, app_assets / asset_name)

        license_file = repo_root / "LICENSE"
        if license_file.is_file():
            shutil.copy2(license_file, stage_app / "LICENSE")

        version_txt = (
            f"product={profile.display_name}\n"
            f"identity={profile.identity}\n"
            f"channel={profile.channel}\n"
            f"version={version}\n"
            f"commit={commit_sha}\n"
            f"builtUtc={build_time_utc}\n"
        )
        notices_txt = build_third_party_notices(repo_root)

        for dest_folder in (stage_app, stage_dir):
            (dest_folder / "VERSION.txt").write_text(version_txt, encoding="utf-8", newline="\n")
            (dest_folder / "THIRD-PARTY-NOTICES.txt").write_text(notices_txt, encoding="utf-8", newline="\n")

        # Also place top-level copies of main executables in staging for direct hash/inspection
        host_exe_in_app = stage_app / desktop_exe
        overlay_exe_in_app = stage_app / "Spawn.OverlayHost.exe"
        if not host_exe_in_app.is_file() or not overlay_exe_in_app.is_file():
            raise RuntimeError(f"Expected published executables missing in {stage_app}")

        compression_mode = "lzma2/fast" if profile.dev_like else "lzma2/max"
        iss_path = repo_root / "installer" / "spawn.iss"
        print(f"[package] compiling Inno Setup 7 installer {installer_base}.exe...")
        iscc_cmd = [
            str(iscc_path),
            "--quiet",
            f"/DIdentity={profile.identity}",
            f"/DAppGuid={profile.app_guid}",
            f"/DDisplayName={profile.display_name}",
            f"/DVersion={version}",
            f"/DNumericVersion={numeric_version}",
            f"/DDesktopExe={desktop_exe}",
            f"/DIconFile={icon_file}",
            f"/DSourceDir={stage_app}",
            f"/DOutputDir={stage_dir}",
            f"/DOutputBase={installer_base}",
            f"/DAumid={profile.aumid}",
            f"/DCompressionMode={compression_mode}",
            f"/DCreateShortcuts={'1' if create_shortcuts else '0'}",
            f"/DDesktopOnUpdate={'1' if profile.dev_like else '0'}",
            str(iss_path),
        ]
        iscc_proc = _run_quiet(iscc_cmd, cwd=repo_root)
        if iscc_proc.returncode != 0:
            raise RuntimeError(
                f"ISCC.exe failed (exit {iscc_proc.returncode}):\n{iscc_proc.stdout}\n{iscc_proc.stderr}"
            )

        setup_exe_staged = stage_dir / f"{installer_base}.exe"
        if not setup_exe_staged.is_file():
            raise RuntimeError(f"ISCC succeeded but installer not found at {setup_exe_staged}")

        checksum_targets = [
            (f"{installer_base}.exe", setup_exe_staged),
            ("VERSION.txt", stage_dir / "VERSION.txt"),
            ("THIRD-PARTY-NOTICES.txt", stage_dir / "THIRD-PARTY-NOTICES.txt"),
            (f"app/{desktop_exe}", host_exe_in_app),
            ("app/Spawn.OverlayHost.exe", overlay_exe_in_app),
        ]
        sha_lines = [f"{sha256_file(fpath)}  {rel_name}" for rel_name, fpath in checksum_targets]
        (stage_dir / "SHA256SUMS.txt").write_text("\n".join(sha_lines) + "\n", encoding="utf-8", newline="\n")

        # Atomic directory replacement into dist/<identity>/
        final_dir = dist_root / profile.identity
        old_backup = dist_root / f".old-{profile.identity}-{now_utc.strftime('%Y%m%d%H%M%S')}-{os.getpid()}"
        if final_dir.exists():
            final_dir.rename(old_backup)
        stage_dir.rename(final_dir)
        if old_backup.exists():
            shutil.rmtree(old_backup, ignore_errors=True)

        final_setup = final_dir / f"{installer_base}.exe"
        print(f"[package] ready -> {final_setup} ({final_setup.stat().st_size // 1024} KiB)")
        return final_dir, final_setup
    except Exception:
        if stage_dir.exists():
            shutil.rmtree(stage_dir, ignore_errors=True)
        raise


# ---------------------------------------------------------------------------
# Windows 0x1000 Safe Process Probe, Registry Reader & Job-Escaped Launcher
# ---------------------------------------------------------------------------


class _PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", wintypes.LONG),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", wintypes.WCHAR * 260),
    ]


class _STARTUPINFOW(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("lpReserved", wintypes.LPWSTR),
        ("lpDesktop", wintypes.LPWSTR),
        ("lpTitle", wintypes.LPWSTR),
        ("dwX", wintypes.DWORD),
        ("dwY", wintypes.DWORD),
        ("dwXSize", wintypes.DWORD),
        ("dwYSize", wintypes.DWORD),
        ("dwXCountChars", wintypes.DWORD),
        ("dwYCountChars", wintypes.DWORD),
        ("dwFillAttribute", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("wShowWindow", wintypes.WORD),
        ("cbReserved2", wintypes.WORD),
        ("lpReserved2", ctypes.POINTER(ctypes.c_byte)),
        ("hStdInput", wintypes.HANDLE),
        ("hStdOutput", wintypes.HANDLE),
        ("hStdError", wintypes.HANDLE),
    ]


class _PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("hProcess", wintypes.HANDLE),
        ("hThread", wintypes.HANDLE),
        ("dwProcessId", wintypes.DWORD),
        ("dwThreadId", wintypes.DWORD),
    ]


def _normalize_win_path(p: str | Path) -> str:
    try:
        return os.path.normcase(os.path.abspath(str(p))).rstrip("\\/")
    except OSError:
        return str(p).lower().rstrip("\\/")


def find_running_pids_by_exe(target_exe: Path) -> list[int]:
    """Enumerate PIDs matching target_exe strictly via OpenProcess(0x1000)."""
    if os.name != "nt":
        return []
    target_norm = _normalize_win_path(target_exe)
    target_name = target_exe.name.lower()

    k32 = ctypes.windll.kernel32
    k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    k32.OpenProcess.restype = wintypes.HANDLE
    k32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        wintypes.LPWSTR,
        ctypes.POINTER(wintypes.DWORD),
    ]
    k32.QueryFullProcessImageNameW.restype = wintypes.BOOL

    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == INVALID_HANDLE_VALUE or not snap:
        return []

    pids: list[int] = []
    try:
        entry = _PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(_PROCESSENTRY32W)
        if not k32.Process32FirstW(snap, ctypes.byref(entry)):
            return []
        while True:
            exe_short = entry.szExeFile.lower()
            pid = int(entry.th32ProcessID)
            if pid > 4 and exe_short == target_name:
                h_proc = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
                if h_proc:
                    try:
                        buf_len = wintypes.DWORD(1024)
                        buf = ctypes.create_unicode_buffer(1024)
                        if k32.QueryFullProcessImageNameW(h_proc, 0, buf, ctypes.byref(buf_len)):
                            if _normalize_win_path(buf.value) == target_norm:
                                pids.append(pid)
                    finally:
                        k32.CloseHandle(h_proc)
            if not k32.Process32NextW(snap, ctypes.byref(entry)):
                break
    finally:
        k32.CloseHandle(snap)
    return pids


def lookup_uninstall_entry(app_guid: str) -> dict[str, str] | None:
    if os.name != "nt":
        return None
    subkey = f"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{{{app_guid}}}_is1"
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey, 0, winreg.KEY_READ) as key:
            result: dict[str, str] = {}
            for field in ("DisplayName", "DisplayVersion", "InstallLocation", "QuietUninstallString", "UninstallString"):
                try:
                    val, _ = winreg.QueryValueEx(key, field)
                    if isinstance(val, str):
                        result[field] = val
                except FileNotFoundError:
                    pass
            return result
    except FileNotFoundError:
        return None


def launch_detached_background(exe_path: Path, args: list[str]) -> tuple[int | None, bool]:
    """Launch exe_path --background outside the current command runner Job Object.

    Returns (pid, in_job). Tries CREATE_BREAKAWAY_FROM_JOB first; if the parent
    runner job forbids breakaway (ERROR_ACCESS_DENIED), delegates to Explorer
    Out-of-Process Shell COM (`Shell.Application.ShellExecute`) so explorer.exe
    parents the tray process cleanly outside the command job.
    """
    if os.name != "nt":
        raise RuntimeError("Background tray launch requires Windows.")

    k32 = ctypes.windll.kernel32
    self_in_job = wintypes.BOOL(False)
    k32.IsProcessInJob(k32.GetCurrentProcess(), None, ctypes.byref(self_in_job))

    # 1. Preferred when caller sits inside a runner Job Object (including nested jobs where
    # CREATE_BREAKAWAY_FROM_JOB only escapes the child job, not an outer KILL_ON_JOB_CLOSE job):
    # marshal via out-of-proc explorer.exe CLSID_ShellWindows (SWC_DESKTOP=8 -> Document.Application.ShellExecute)
    # so explorer.exe itself parents the tray process completely outside the command runner's job hierarchy.
    ps_exe = shutil.which("pwsh") or shutil.which("powershell") or "powershell.exe"
    ps_script = (
        "$t = [Type]::GetTypeFromCLSID([Guid]'9BA05972-F6A8-11CF-A442-00A0C90A8F39'); "
        "$sw = [Activator]::CreateInstance($t); "
        "$m = [System.Reflection.Missing]::Value; "
        "$d = $t.InvokeMember('FindWindowSW', [System.Reflection.BindingFlags]::InvokeMethod, $null, $sw, @($m, $m, 8, 0, 1)); "
        "if ($null -eq $d) { exit 2 }; "
        "$doc = $d.GetType().InvokeMember('Document', [System.Reflection.BindingFlags]::GetProperty, $null, $d, $null); "
        "$app = $doc.GetType().InvokeMember('Application', [System.Reflection.BindingFlags]::GetProperty, $null, $doc, $null); "
        "$null = $app.GetType().InvokeMember('ShellExecute', [System.Reflection.BindingFlags]::InvokeMethod, $null, $app, "
        "@($env:SPAWN_LAUNCH_EXE, $env:SPAWN_LAUNCH_ARGS, $env:SPAWN_LAUNCH_CWD, 'open', 0))"
    )
    proc = _run_quiet(
        [ps_exe, "-Sta", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", ps_script],
        cwd=exe_path.parent,
        env={
            "SPAWN_LAUNCH_EXE": str(exe_path),
            "SPAWN_LAUNCH_ARGS": subprocess.list2cmdline(args),
            "SPAWN_LAUNCH_CWD": str(exe_path.parent),
        },
        timeout=15.0,
    )
    if proc.returncode == 0:
        deadline = time.monotonic() + 6.0
        while time.monotonic() < deadline:
            pids = find_running_pids_by_exe(exe_path)
            if pids:
                return pids[0], False
            time.sleep(0.2)

    # 2. Fallback: direct CreateProcessW with CREATE_BREAKAWAY_FROM_JOB (when desktop shell is unavailable)
    cmd_line = subprocess.list2cmdline([str(exe_path), *args])
    cmd_buf = ctypes.create_unicode_buffer(cmd_line)
    si = _STARTUPINFOW()
    si.cb = ctypes.sizeof(_STARTUPINFOW)
    pi = _PROCESS_INFORMATION()

    creation_flags = (
        CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW
    )
    ok = k32.CreateProcessW(
        str(exe_path),
        cmd_buf,
        None,
        None,
        False,
        creation_flags,
        None,
        str(exe_path.parent),
        ctypes.byref(si),
        ctypes.byref(pi),
    )
    if ok:
        try:
            in_job = wintypes.BOOL(False)
            k32.IsProcessInJob(pi.hProcess, None, ctypes.byref(in_job))
            return int(pi.dwProcessId), bool(in_job.value)
        finally:
            k32.CloseHandle(pi.hThread)
            k32.CloseHandle(pi.hProcess)

    # Final direct detached fallback (in case Explorer shell is unavailable in headless session)
    pi2 = _PROCESS_INFORMATION()
    cmd_buf2 = ctypes.create_unicode_buffer(cmd_line)
    ok2 = k32.CreateProcessW(
        str(exe_path),
        cmd_buf2,
        None,
        None,
        False,
        DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW,
        None,
        str(exe_path.parent),
        ctypes.byref(si),
        ctypes.byref(pi2),
    )
    if ok2:
        try:
            in_job2 = wintypes.BOOL(True)
            k32.IsProcessInJob(pi2.hProcess, None, ctypes.byref(in_job2))
            return int(pi2.dwProcessId), bool(in_job2.value)
        finally:
            k32.CloseHandle(pi2.hThread)
            k32.CloseHandle(pi2.hProcess)

    raise RuntimeError(f"Failed to launch {exe_path} in background tray.")


def write_dev_workspace_bridge(
    repo_root: Path,
    profile: IdentityProfile,
    version: str,
    data_dir_override: Path | None = None,
) -> Path | None:
    """Write dev-workspace.json so installed SPAWN Dev finds .local/games & .local/secrets."""
    if not profile.dev_like:
        return None
    env_data = os.environ.get("SPAWN_DATA_DIR", "").strip()
    if data_dir_override is not None:
        data_root = data_dir_override
    elif env_data:
        data_root = Path(env_data)
    elif profile.identity.endswith("-test"):
        return None
    else:
        local_app_data = os.environ.get("LOCALAPPDATA", "").strip()
        if not local_app_data:
            return None
        data_root = Path(local_app_data) / profile.identity

    data_root.mkdir(parents=True, exist_ok=True)
    bridge_path = data_root / "dev-workspace.json"
    repo_str = str(repo_root.resolve())
    payload = {
        "workspaceRoot": repo_str,
        "repositoryRoot": repo_str,
        "repoRoot": repo_str,
        "identity": profile.identity,
        "version": version,
        "updatedUtc": datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    bridge_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    return bridge_path


def request_graceful_quit_and_wait(exe_path: Path, timeout_sec: float = QUIT_TIMEOUT_SECONDS) -> tuple[bool, int]:
    """Ask running instance (and companion Spawn.OverlayHost.exe) to exit via --quit-for-update; never force-kills."""
    companion_overlay = exe_path.parent / "Spawn.OverlayHost.exe"
    host_pids = find_running_pids_by_exe(exe_path)
    overlay_pids = find_running_pids_by_exe(companion_overlay)
    if not host_pids and not overlay_pids:
        return False, 0

    t0 = time.monotonic()
    if host_pids and exe_path.is_file():
        _run_quiet([str(exe_path), "--quit-for-update"], cwd=exe_path.parent, timeout=15.0)

    deadline = t0 + timeout_sec
    while time.monotonic() < deadline:
        if not find_running_pids_by_exe(exe_path) and not find_running_pids_by_exe(companion_overlay):
            return True, int((time.monotonic() - t0) * 1000)
        time.sleep(QUIT_POLL_SECONDS)

    raise TimeoutError(
        f"{exe_path.name} (or companion Spawn.OverlayHost.exe) is still running after {int(timeout_sec)}s; "
        "installation aborted without force-kill."
    )


def retire_locked_binaries(install_dir: Path, identity: str) -> int:
    """Move any kernel SEC_IMAGE-cached DLL/EXE out of install_dir before Inno overwrite.

    After a .NET/native process exits on Windows 11, kernel image sections or Defender
    scans can keep DeleteFileW blocked (ERROR_SHARING_VIOLATION=32) for several seconds
    on native DLLs such as e_sqlite3.dll while NTFS MoveFileW/rename succeeds immediately.
    """
    if not install_dir.is_dir():
        return 0

    # Opportunistically clean any stale retired folders from earlier upgrades
    temp_root = Path(os.environ.get("TEMP", str(install_dir)))
    for old_dir in temp_root.glob(f"spawn-retired-{identity}-*"):
        shutil.rmtree(old_dir, ignore_errors=True)
    local_retired = install_dir / ".retired"
    if local_retired.exists():
        shutil.rmtree(local_retired, ignore_errors=True)

    retired_count = 0
    stamp = datetime.now(UTC).strftime("%Y%m%d%H%M%S")
    retire_dest_root = temp_root / f"spawn-retired-{identity}-{stamp}-{os.getpid()}"

    for fpath in list(install_dir.rglob("*")):
        if not fpath.is_file():
            continue
        if fpath.name.lower() in {"unins000.exe", "unins000.dat"}:
            continue
        if ".retired" in fpath.parts:
            continue
        try:
            with fpath.open("r+b"):
                pass
        except PermissionError:
            rel = fpath.relative_to(install_dir)
            target = retire_dest_root / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            try:
                fpath.replace(target)
                retired_count += 1
            except OSError:
                fallback = local_retired / stamp / rel
                fallback.parent.mkdir(parents=True, exist_ok=True)
                try:
                    fpath.replace(fallback)
                    retired_count += 1
                except OSError:
                    pass
    return retired_count


def install_package(
    repo_root: Path,
    profile: IdentityProfile,
    setup_exe: Path,
    expected_version: str,
    *,
    install_dir_override: Path | None,
    relaunch_background: bool,
    receipt_path: Path,
) -> dict[str, object]:
    t0 = time.monotonic()
    started_at = datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ")
    receipt: dict[str, object] = {
        "identity": profile.identity,
        "displayName": profile.display_name,
        "installed": False,
        "skipped": False,
        "newVersion": expected_version,
        "wasRunning": False,
        "relaunched": False,
        "background": False,
        "at": started_at,
    }

    def save_and_return() -> dict[str, object]:
        receipt["elapsedMs"] = int((time.monotonic() - t0) * 1000)
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        receipt_path.write_text(
            json.dumps(receipt, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        return receipt

    if os.environ.get("SPAWN_DEV_AUTO_INSTALL", "").strip() == "0":
        receipt["skipped"] = True
        receipt["reason"] = "SPAWN_DEV_AUTO_INSTALL=0: skipped local auto-install"
        print(f"[install-dev] {receipt['reason']}")
        return save_and_return()

    if os.environ.get("CI", "").strip().lower() == "true":
        receipt["skipped"] = True
        receipt["reason"] = "CI=true: skipped local auto-install on CI runner"
        print(f"[install-dev] {receipt['reason']}")
        return save_and_return()

    if os.name != "nt":
        receipt["error"] = "Local installer execution requires Windows."
        save_and_return()
        raise RuntimeError(str(receipt["error"]))

    try:
        prior = lookup_uninstall_entry(profile.app_guid)
        if prior:
            receipt["oldVersion"] = prior.get("DisplayVersion", "")
            prior_loc = prior.get("InstallLocation", "").strip()
        else:
            prior_loc = ""

        default_loc = (
            install_dir_override
            if install_dir_override is not None
            else Path(os.environ["LOCALAPPDATA"]) / "Programs" / profile.identity
        )
        probe_exe = (Path(prior_loc) if prior_loc else default_loc) / f"{profile.identity}.exe"

        was_running, quit_ms = request_graceful_quit_and_wait(probe_exe)
        receipt["wasRunning"] = was_running
        if was_running:
            receipt["quitMs"] = quit_ms

        retired_files = retire_locked_binaries(probe_exe.parent, profile.identity)
        if retired_files:
            receipt["retiredLockedFiles"] = retired_files

        log_path = receipt_path.parent / f"inno-{profile.identity}-install.log"
        log_path.parent.mkdir(parents=True, exist_ok=True)
        setup_cmd = [
            str(setup_exe),
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            f"/LOG={log_path}",
        ]
        if install_dir_override is not None:
            setup_cmd.append(f"/DIR={install_dir_override}")

        print(f"[install-dev] silently installing {profile.display_name} {expected_version}...")
        proc = _run_quiet(setup_cmd, cwd=repo_root, timeout=180.0)
        if proc.returncode != 0:
            raise RuntimeError(
                f"Installer exited with code {proc.returncode}:\n{proc.stdout}\n{proc.stderr}"
            )

        after = lookup_uninstall_entry(profile.app_guid)
        if not after:
            raise RuntimeError(
                f"HKCU Uninstall entry for {{{profile.app_guid}}}_is1 missing after silent install."
            )
        actual_ver = after.get("DisplayVersion", "")
        if actual_ver != expected_version:
            raise RuntimeError(
                f"Installed DisplayVersion mismatch: expected {expected_version!r}, got {actual_ver!r}"
            )

        installed_loc = Path(after.get("InstallLocation", "") or str(default_loc))
        installed_exe = installed_loc / f"{profile.identity}.exe"
        if not installed_exe.is_file():
            raise RuntimeError(f"Installed executable missing at {installed_exe}")

        bridge_file = write_dev_workspace_bridge(repo_root, profile, expected_version)
        receipt["installed"] = True
        receipt["installLocation"] = str(installed_loc)
        receipt["installedExe"] = str(installed_exe)
        if bridge_file is not None:
            receipt["devWorkspaceBridge"] = str(bridge_file)

        if relaunch_background:
            pid, in_job = launch_detached_background(installed_exe, ["--background"])
            receipt["relaunched"] = True
            receipt["background"] = True
            receipt["inJob"] = in_job
            if pid is not None:
                receipt["pid"] = pid
            print(
                f"[install-dev] {profile.display_name} {expected_version} installed & running in tray "
                f"(pid={pid}, inJob={in_job})"
            )
        else:
            print(f"[install-dev] {profile.display_name} {expected_version} installed (relaunch skipped).")

        return save_and_return()
    except Exception as exc:
        receipt["error"] = str(exc)
        save_and_return()
        raise


def uninstall_identity(repo_root: Path, profile: IdentityProfile, *, allow_release: bool) -> int:
    if profile.identity == "spawn" and not allow_release:
        raise RuntimeError("Refusing silent uninstall of production 'spawn' without --confirm-release-uninstall.")

    entry = lookup_uninstall_entry(profile.app_guid)
    if not entry:
        print(f"[uninstall] {profile.identity} is not currently installed.")
        return 0

    loc = entry.get("InstallLocation", "").strip()
    if loc:
        exe_path = Path(loc) / f"{profile.identity}.exe"
        request_graceful_quit_and_wait(exe_path)
        unins_exe = Path(loc) / "unins000.exe"
    else:
        unins_exe = Path(os.environ["LOCALAPPDATA"]) / "Programs" / profile.identity / "unins000.exe"

    if not unins_exe.is_file():
        raise FileNotFoundError(f"Uninstaller not found at {unins_exe}")

    proc = _run_quiet(
        [str(unins_exe), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"],
        cwd=repo_root,
        timeout=120.0,
    )
    if proc.returncode != 0:
        raise RuntimeError(f"Uninstaller failed with code {proc.returncode}: {proc.stderr}")

    # Wait briefly for Inno Setup uninstaller background self-cleanup to remove the registry key
    deadline = time.monotonic() + 10.0
    while time.monotonic() < deadline:
        if lookup_uninstall_entry(profile.app_guid) is None:
            break
        time.sleep(0.2)

    print(f"[uninstall] {profile.display_name} ({profile.identity}) removed cleanly (user data preserved).")
    return 0


def find_existing_installer(repo_root: Path, profile: IdentityProfile) -> tuple[Path, str]:
    dist_dir = repo_root / "dist" / profile.identity
    version_file = dist_dir / "VERSION.txt"
    if not dist_dir.is_dir() or not version_file.is_file():
        raise FileNotFoundError(f"No packaged artifacts found in {dist_dir}; run without --install-only first.")
    ver_map: dict[str, str] = {}
    for line in version_file.read_text(encoding="utf-8").splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            ver_map[k.strip()] = v.strip()
    ver = ver_map.get("version", "")
    setup_exe = dist_dir / f"{profile.identity}-setup-{ver}-x64.exe"
    if not ver or not setup_exe.is_file():
        raise FileNotFoundError(f"Installer executable missing at {setup_exe}")
    return setup_exe, ver


def main() -> int:
    _configure_windows_quiet_mode()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--channel", choices=("dev", "release"), default="dev")
    parser.add_argument("--identity", choices=tuple(IDENTITY_PROFILES.keys()), default=None)
    parser.add_argument("--version", default=None, help="Explicit package version override.")
    parser.add_argument("--install-dev", action="store_true", help="Build and silently install/update SPAWN Dev.")
    parser.add_argument("--install", action="store_true", help="Silently install the selected --identity.")
    parser.add_argument("--install-only", action="store_true", help="Install existing dist/<identity> package without rebuilding.")
    parser.add_argument("--uninstall", action="store_true", help="Silently uninstall the selected --identity.")
    parser.add_argument("--confirm-release-uninstall", action="store_true")
    parser.add_argument("--install-dir", type=Path, default=None, help="Optional custom /DIR target (for isolated tests).")
    parser.add_argument("--no-shortcuts", action="store_true", help="Disable Start Menu and Desktop shortcut creation.")
    parser.add_argument("--no-relaunch", action="store_true", help="Do not launch --background after install.")
    parser.add_argument("--self-contained", action="store_true", help="Publish self-contained win-x64 runtime.")
    parser.add_argument("--receipt-path", type=Path, default=None)
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parents[1]

    if args.install_dev and args.identity is None:
        identity_key = "spawn-dev"
    elif args.identity is not None:
        identity_key = args.identity
    else:
        identity_key = "spawn-dev" if args.channel == "dev" else "spawn"

    profile = IDENTITY_PROFILES[identity_key]

    if args.uninstall:
        return uninstall_identity(repo_root, profile, allow_release=args.confirm_release_uninstall)

    utc_stamp = datetime.now(UTC).strftime("%Y%m%d%H%M")
    if args.version:
        version = args.version
    elif profile.dev_like:
        version = f"{NEXT_BASE_VERSION}-dev.{utc_stamp}"
    else:
        version = NEXT_BASE_VERSION

    create_shortcuts = profile.default_shortcuts and (not args.no_shortcuts)

    if args.install_only:
        setup_exe, version = find_existing_installer(repo_root, profile)
    else:
        _, setup_exe = build_package(
            repo_root,
            profile,
            version,
            create_shortcuts=create_shortcuts,
            self_contained=args.self_contained,
        )

    if args.install_dev or args.install or args.install_only:
        default_receipt_name = "install-dev.json" if profile.identity == "spawn-dev" else f"install-{profile.identity}.json"
        receipt_path = args.receipt_path or (repo_root / ".local" / "reports" / "m2" / default_receipt_name)
        install_package(
            repo_root,
            profile,
            setup_exe,
            version,
            install_dir_override=args.install_dir,
            relaunch_background=(not args.no_relaunch),
            receipt_path=receipt_path,
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
