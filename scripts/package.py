"""Per-user NSIS 3.13 packager and automatic SPAWN Dev installer.

Requires Python 3.12+ standard library only (zero pip dependencies) + .NET 10 SDK. The pinned
NSIS 3.13 build is fetched on first use (SHA-256 checked) into the Git-ignored .tools/nsis-3.13/.

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
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import time
from typing import Callable
import urllib.request
import uuid
import zipfile

if os.name == "nt":
    import winreg

NEXT_BASE_VERSION = "0.1.1"
QUIT_TIMEOUT_SECONDS = 30.0
QUIT_POLL_SECONDS = 0.25
# While SPAWN Dev shows a window the user is working in it: check every 2 s, give up after 10 min.
WINDOW_DEFER_TIMEOUT_SECONDS = 600.0
WINDOW_DEFER_POLL_SECONDS = 2.0
UNINSTALL_REMOVAL_TIMEOUT_SECONDS = 60.0

REPO_ROOT = Path(__file__).resolve().parents[1]
APP_ID_PREFIX = "io.github.chialecode."
UNINSTALL_KEY_PREFIX = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\"
NSIS_VERSION = "3.13"
NSIS_ZIP_URL = "https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip"
NSIS_ZIP_SHA256 = "ba63dffc4410ee89193e1cb5a41989991bd77c61068da17e3156d136b7b0b3d8"
NSIS_CACHE_DIR = REPO_ROOT / ".tools" / f"nsis-{NSIS_VERSION}"
NSIS_DOWNLOAD_TIMEOUT_SECONDS = 120.0
# Installer exit codes besides NSIS's own 0 (done), 1 (cancelled) and 2 (aborted); see installer/spawn.nsi.
INSTALLER_EXIT_REASONS = {
    1: "installer cancelled",
    2: "installer aborted",
    4: "SPAWN is still running or busy after 30 s (installer did not force-kill it)",
    5: "SPAWN is running but its executable to ask to quit was not found",
    6: "another installer of this identity is running",
    7: "the target folder holds another program's Uninstall.exe",
    8: "a file could not be written (held open by another program); nothing was registered",
}

BELOW_NORMAL_PRIORITY_CLASS = 0x00004000
CREATE_NO_WINDOW = 0x08000000
CREATE_UNICODE_ENVIRONMENT = 0x00000400
DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000
ERROR_ACCESS_DENIED = 5

TH32CS_SNAPPROCESS = 0x00000002
PROCESS_QUERY_LIMITED_INFORMATION = 0x00001000
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

_APP_OVERRIDE_ENV_VARS = frozenset(
    {
        "SPAWN_CHANNEL",
        "SPAWN_IDENTITY",
        "SPAWN_DATA_DIR",
        "SPAWN_VAULT_DIR",
        "SPAWN_HEADLESS_TEST",
        "SPAWN_TEST_HEADLESS",
        "SPAWN_MUTE_AUDIO",
    }
)


@dataclass(frozen=True)
class IdentityProfile:
    identity: str
    channel: str
    display_name: str
    dev_like: bool
    default_shortcuts: bool

    @property
    def app_id(self) -> str:
        """Uninstall key name and application identity: io.github.chialecode.<identity>."""
        return APP_ID_PREFIX + self.identity


IDENTITY_PROFILES: dict[str, IdentityProfile] = {
    "spawn": IdentityProfile(
        identity="spawn",
        channel="release",
        display_name="SPAWN",
        dev_like=False,
        default_shortcuts=True,
    ),
    "spawn-dev": IdentityProfile(
        identity="spawn-dev",
        channel="dev",
        display_name="SPAWN Dev",
        dev_like=True,
        default_shortcuts=True,
    ),
    "spawn-test": IdentityProfile(
        identity="spawn-test",
        channel="release",
        display_name="SPAWN Test",
        dev_like=False,
        default_shortcuts=False,
    ),
    "spawn-dev-test": IdentityProfile(
        identity="spawn-dev-test",
        channel="dev",
        display_name="SPAWN Dev Test",
        dev_like=True,
        default_shortcuts=False,
    ),
}


def _configure_windows_quiet_mode() -> None:
    if os.name != "nt":
        return
    try:
        kernel32 = _get_kernel32()
        # SEM_FAILCRITICALERRORS (0x0001) | SEM_NOGPFAULTERRORBOX (0x0002)
        kernel32.SetErrorMode(0x0001 | 0x0002)
        kernel32.SetPriorityClass(kernel32.GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS)
    except OSError:
        pass


def _run_quiet(
    cmd: list[str] | str,
    *,
    cwd: Path,
    env: dict[str, str] | None = None,
    base_env: dict[str, str] | None = None,
    timeout: float = 300.0,
) -> subprocess.CompletedProcess[str]:
    flags = (BELOW_NORMAL_PRIORITY_CLASS | CREATE_NO_WINDOW) if os.name == "nt" else 0
    merged_env = dict(base_env) if base_env is not None else os.environ.copy()
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


def build_clean_app_env(
    profile: IdentityProfile,
    source_env: dict[str, str] | None = None,
) -> dict[str, str]:
    """Return an environment safe for invoking installed SPAWN binaries.

    For real non-test identities (`spawn`, `spawn-dev`), strips test/override
    variables (`SPAWN_DATA_DIR`, `SPAWN_CHANNEL`, `SPAWN_IDENTITY`,
    `SPAWN_HEADLESS_TEST`, etc.) so `--quit-for-update` and detached fallback
    launches always address the real installed identity rather than a caller's
    test sandbox. Test identities (`*-test`) retain caller overrides.
    """
    env = dict(source_env) if source_env is not None else os.environ.copy()
    if profile.identity.endswith("-test"):
        return env
    for key in list(env.keys()):
        if key.upper() in _APP_OVERRIDE_ENV_VARS:
            env.pop(key, None)
    return env


def find_makensis(
    env: dict[str, str] | None = None,
    *,
    cache_dir: Path | None = None,
    fetch: bool = True,
    download: Callable[[str], bytes] | None = None,
) -> Path:
    """Return makensis.exe of the pinned NSIS build, fetching the verified archive once if missing.

    SPAWN_MAKENSIS_PATH overrides the location; installer/spawn.nsi itself rejects any version
    but NSIS_VERSION.
    """
    effective_env = env if env is not None else os.environ
    override = effective_env.get("SPAWN_MAKENSIS_PATH", "").strip()
    if override:
        candidate = Path(override)
        if not candidate.is_file():
            raise FileNotFoundError(f"SPAWN_MAKENSIS_PATH does not name a file: {override}")
        return candidate

    target = cache_dir if cache_dir is not None else NSIS_CACHE_DIR
    makensis = target / "makensis.exe"
    if makensis.is_file():
        return makensis
    if not fetch:
        raise FileNotFoundError(
            f"NSIS {NSIS_VERSION} not found at .tools/{target.name}/makensis.exe; "
            "run scripts/package.py once to fetch it, or set SPAWN_MAKENSIS_PATH"
        )
    fetch_pinned_nsis(target, download=download)
    if not makensis.is_file():
        raise RuntimeError(f"NSIS {NSIS_VERSION} archive did not contain makensis.exe")
    return makensis


def _download_bytes(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "spawn-package-script"})
    with urllib.request.urlopen(request, timeout=NSIS_DOWNLOAD_TIMEOUT_SECONDS) as response:
        return response.read()


def fetch_pinned_nsis(target: Path, *, download: Callable[[str], bytes] | None = None) -> None:
    """Download the pinned NSIS zip, check its SHA-256 and unpack it into `target` atomically."""
    print(f"[package] fetching NSIS {NSIS_VERSION} (pinned SHA-256) into .tools/{target.name}...")
    try:
        data = (download or _download_bytes)(NSIS_ZIP_URL)
    except OSError as exc:
        raise RuntimeError(
            f"Could not download NSIS {NSIS_VERSION} from {NSIS_ZIP_URL}: {exc}. "
            "Download it by hand and set SPAWN_MAKENSIS_PATH, or retry when online."
        ) from exc
    digest = hashlib.sha256(data).hexdigest()
    if digest != NSIS_ZIP_SHA256:
        raise RuntimeError(
            f"NSIS {NSIS_VERSION} archive SHA-256 mismatch: expected {NSIS_ZIP_SHA256}, got {digest}; not using it"
        )

    root = f"nsis-{NSIS_VERSION}/"
    target.parent.mkdir(parents=True, exist_ok=True)
    staging = target.parent / f".{target.name}.staging-{uuid.uuid4().hex}"
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            for info in archive.infolist():
                name = info.filename.replace("\\", "/")
                if not name.startswith(root):
                    raise RuntimeError(f"Unexpected entry in NSIS archive: {info.filename!r}")
                rel = PurePosixPath(name[len(root):])
                if not rel.parts:
                    continue
                if rel.is_absolute() or any(part == ".." or ":" in part for part in rel.parts):
                    raise RuntimeError(f"Unsafe path in NSIS archive: {info.filename!r}")
                dest = staging.joinpath(*rel.parts)
                if info.is_dir():
                    dest.mkdir(parents=True, exist_ok=True)
                    continue
                dest.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(info) as src, open(dest, "wb") as dst:
                    shutil.copyfileobj(src, dst)
        if target.exists():
            shutil.rmtree(target)
        os.replace(staging, target)
    finally:
        if staging.exists():
            shutil.rmtree(staging, ignore_errors=True)


def nsis_escape(text: str) -> str:
    """Escape text for an NSIS double-quoted string ($ starts a variable)."""
    return text.replace("$", "$$")


def render_remove_files_nsh(app_dir: Path) -> str:
    """Return the SPAWN_REMOVE_INSTALLED_FILES macro: every staged file, then its folders deepest first.

    Each file goes through SPAWN_REMOVE_FILE (installer/spawn.nsi), which moves a file Windows
    still holds into .retired instead of leaving it behind.
    """
    files: list[PurePosixPath] = []
    folders: set[PurePosixPath] = set()
    for path in app_dir.rglob("*"):
        if not path.is_file():
            continue
        rel = PurePosixPath(path.relative_to(app_dir).as_posix())
        files.append(rel)
        for parent in rel.parents:
            if parent != PurePosixPath("."):
                folders.add(parent)
    if not files:
        raise RuntimeError(f"No files to install in {app_dir}")

    def win(rel: PurePosixPath) -> str:
        return nsis_escape(str(rel).replace("/", "\\"))

    def flat(rel: PurePosixPath) -> str:
        return nsis_escape("_".join(rel.parts))

    lines = [
        "; Generated by scripts/package.py: the files this version installs. Do not edit.",
        "!macro SPAWN_REMOVE_INSTALLED_FILES",
    ]
    lines += [
        f'  !insertmacro SPAWN_REMOVE_FILE "{win(rel)}" "{flat(rel)}"'
        for rel in sorted(files, key=str)
    ]
    lines += [
        f'  RMDir "$INSTDIR\\{win(rel)}"'
        for rel in sorted(folders, key=lambda d: (-len(d.parts), str(d)))
    ]
    lines.append("!macroend")
    return "\n".join(lines) + "\n"


def compile_nsis_installer(
    makensis_exe: Path,
    nsi_path: Path,
    *,
    profile: IdentityProfile,
    version: str,
    numeric_version: str,
    icon_file: Path,
    app_dir: Path,
    out_file: Path,
    work_dir: Path,
    create_shortcuts: bool,
    fast_compression: bool,
    cwd: Path,
) -> None:
    """Write the uninstall file list into work_dir and compile installer/spawn.nsi into out_file."""
    work_dir.mkdir(parents=True, exist_ok=True)
    files_nsh = work_dir / "installed-files.nsh"
    files_nsh.write_text(render_remove_files_nsh(app_dir), encoding="utf-8", newline="\n")
    cmd = [
        str(makensis_exe),
        "-V2",
        # Warnings fail the build: the script must stay warning-free.
        "-WX",
        "-INPUTCHARSET",
        "UTF8",
        "-OUTPUTCHARSET",
        "UTF8",
        f"-DSPAWN_IDENTITY={profile.identity}",
        f"-DSPAWN_APP_ID={profile.app_id}",
        f"-DSPAWN_DISPLAY_NAME={profile.display_name}",
        f"-DSPAWN_VERSION={version}",
        f"-DSPAWN_FILE_VERSION={numeric_version}",
        f"-DSPAWN_ICON={icon_file}",
        f"-DSPAWN_SOURCE_DIR={app_dir}",
        f"-DSPAWN_OUT_FILE={out_file}",
        f"-DSPAWN_FILES_NSH={files_nsh}",
        f"-DSPAWN_CREATE_SHORTCUTS={'1' if create_shortcuts else '0'}",
        f"-DSPAWN_DESKTOP_ON_UPDATE={'1' if profile.dev_like else '0'}",
    ]
    if fast_compression:
        cmd.append("-DSPAWN_FAST_COMPRESSION")
    cmd.append(str(nsi_path))
    proc = _run_quiet(cmd, cwd=cwd, timeout=600.0)
    if proc.returncode != 0:
        raise RuntimeError(f"makensis failed (exit {proc.returncode}):\n{proc.stdout}\n{proc.stderr}")
    if not out_file.is_file() or out_file.stat().st_size == 0:
        raise RuntimeError(f"makensis succeeded but installer not found at {out_file}")


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


def resolve_current_stage(repo_root: Path) -> str:
    reg_path = repo_root / "docs" / "governance" / "document-registry.json"
    if reg_path.is_file():
        try:
            data = json.loads(reg_path.read_text(encoding="utf-8-sig"))
            stage = str(data.get("currentStage") or "").strip().lower()
            if re.fullmatch(r"m[1-9]\d*", stage):
                return stage
        except (OSError, ValueError):
            pass
    return "m3"


def resolve_third_party_notices(repo_root: Path) -> Path:
    root_notices = repo_root / "THIRD-PARTY-NOTICES.txt"
    if not root_notices.is_file():
        raise FileNotFoundError(
            f"Required compliance notice file missing at repository root: {root_notices}"
        )
    return root_notices


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fp:
        while chunk := fp.read(1024 * 1024):
            h.update(chunk)
    return h.hexdigest()


_NATIVE_PDB_PREFIXES = (
    b"Microsoft C/C++ ",
)
_PORTABLE_PDB_MAGIC = b"BSJB"


def is_native_pdb(pdb_path: Path) -> bool:
    """Return True if pdb_path is a native (non-Portable/non-managed) PDB such as libSkiaSharp.pdb."""
    try:
        with pdb_path.open("rb") as fp:
            header = fp.read(32)
    except OSError:
        return False
    if header.startswith(_PORTABLE_PDB_MAGIC):
        return False
    if any(header.startswith(prefix) for prefix in _NATIVE_PDB_PREFIXES):
        return True
    stem = pdb_path.stem.lower()
    return not (stem == "spawn" or stem.startswith("spawn.") or stem.startswith("spawn-"))


def strip_native_pdbs(stage_app: Path) -> list[str]:
    """Delete all native *.pdb files recursively under stage_app (applies to ALL channels)."""
    removed: list[str] = []
    for pdb_path in sorted(stage_app.rglob("*.pdb"), key=lambda p: p.relative_to(stage_app).as_posix()):
        if pdb_path.is_file() and is_native_pdb(pdb_path):
            rel = pdb_path.relative_to(stage_app).as_posix()
            pdb_path.unlink()
            removed.append(rel)
    return removed


def strip_and_archive_release_pdbs(stage_app: Path, symbols_zip_path: Path) -> list[str]:
    """Archive all remaining *.pdb files under stage_app into symbols_zip_path and delete them.

    Asserts zero *.pdb files remain anywhere under stage_app afterward.
    """
    pdbs = sorted(
        (p for p in stage_app.rglob("*.pdb") if p.is_file()),
        key=lambda p: p.relative_to(stage_app).as_posix(),
    )
    archived: list[str] = []
    symbols_zip_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(symbols_zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for pdb_path in pdbs:
            rel = pdb_path.relative_to(stage_app).as_posix()
            zf.write(pdb_path, arcname=rel)
            pdb_path.unlink()
            archived.append(rel)

    remaining = sorted(p.relative_to(stage_app).as_posix() for p in stage_app.rglob("*.pdb") if p.is_file())
    if remaining:
        raise RuntimeError(
            f"Release PDB stripping violation: *.pdb files remain in {stage_app}: {remaining}"
        )
    return archived


def resolve_next_release_version(repo_root: Path, base_version: str = NEXT_BASE_VERSION) -> str:
    """Compute next release version via release.py tag logic, falling back to base_version."""
    try:
        scripts_dir = str(Path(__file__).resolve().parent)
        if scripts_dir not in sys.path:
            sys.path.insert(0, scripts_dir)
        import release as release_mod

        plan = release_mod.compute_release_plan(repo_root, base_version=base_version)
        return plan.version
    except Exception:
        return base_version


def compute_dev_version(
    repo_root: Path,
    *,
    now_utc: datetime | None = None,
    base_version: str = NEXT_BASE_VERSION,
) -> str:
    """Compute <next-release-version>-dev.<YYYYMMDDHHMM> using release.py next-version logic."""
    ts = (now_utc or datetime.now(UTC)).strftime("%Y%m%d%H%M")
    next_rel = resolve_next_release_version(repo_root, base_version=base_version)
    return f"{next_rel}-dev.{ts}"


def should_skip_dev_auto_install(env: dict[str, str] | None = None) -> str | None:
    """Return skip reason when D-32 local dev auto-install must be skipped, else None."""
    source = os.environ if env is None else env
    if source.get("SPAWN_DEV_AUTO_INSTALL", "").strip() == "0":
        return "SPAWN_DEV_AUTO_INSTALL=0: skipped local auto-install"
    if source.get("CI", "").strip().lower() == "true":
        return "CI=true: skipped local auto-install on CI runner"
    return None


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
    self_contained: bool | None = None,
    output_dir: Path | None = None,
) -> tuple[Path, Path]:
    """Build executables and the NSIS installer into dist/<identity>/ via atomic staging."""
    rel_ico, dev_ico = ensure_icons(repo_root)
    icon_file = dev_ico if profile.dev_like else rel_ico
    makensis_path = find_makensis()
    commit_sha = resolve_git_short_sha(repo_root)
    now_utc = datetime.now(UTC)
    build_time_utc = now_utc.strftime("%Y-%m-%dT%H:%M:%SZ")
    numeric_version = compute_numeric_version(version)
    desktop_exe = f"{profile.identity}.exe"
    installer_base = f"{profile.identity}-setup-{version}-x64"
    effective_self_contained = (not profile.dev_like) if self_contained is None else bool(self_contained)

    final_dir = output_dir.resolve() if output_dir is not None else (repo_root / "dist" / profile.identity)
    dist_root = final_dir.parent
    dist_root.mkdir(parents=True, exist_ok=True)
    stage_dir = dist_root / f".staging-{profile.identity}-{now_utc.strftime('%Y%m%d%H%M%S')}-{os.getpid()}"
    stage_app = stage_dir / "app"
    stage_obj_root = stage_dir / ".msbuild-out"

    if stage_dir.exists():
        shutil.rmtree(stage_dir, ignore_errors=True)
    stage_app.mkdir(parents=True, exist_ok=True)

    try:
        sc_value = "true" if effective_self_contained else "false"
        common_props = [
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            sc_value,
            "-nologo",
            # Never leave reusable MSBuild or Roslyn VBCSCompiler nodes behind: they inherit
            # redirected pipes and can keep subprocess.run(..., capture_output=True) waiting forever.
            "-nr:false",
            "-p:UseSharedCompilation=false",
            f"-p:SpawnChannel={profile.channel}",
            f"-p:SpawnIdentity={profile.identity}",
            f"-p:SpawnVersion={version}",
            f"-p:SpawnCommitSha={commit_sha}",
            f"-p:SpawnBuildTimeUtc={build_time_utc}",
            f"-p:Version={numeric_version}",
            f"-p:InformationalVersion={version}+{commit_sha}",
            f"-p:SpawnApplicationIcon={icon_file}",
        ]

        print(
            f"[package] publishing Spawn.OverlayHost ({profile.identity} {version}, "
            f"self-contained={sc_value})..."
        )
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
            timeout=600.0,
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
            timeout=600.0,
        )
        if pub_host.returncode != 0:
            raise RuntimeError(
                f"dotnet publish Spawn.Host failed (exit {pub_host.returncode}):\n"
                f"{pub_host.stdout}\n{pub_host.stderr}"
            )

        stage_trainer_worker = stage_app / "Workers" / "Trainer"
        stage_trainer_worker.mkdir(parents=True, exist_ok=True)
        print("[package] publishing Spawn.TrainerWorker -> Workers\\Trainer\\Spawn.TrainerWorker.exe...")
        pub_trainer = _run_quiet(
            [
                "dotnet",
                "publish",
                "src/Spawn.TrainerWorker/Spawn.TrainerWorker.csproj",
                *common_props,
                "-o",
                str(stage_trainer_worker),
            ],
            cwd=repo_root,
            timeout=600.0,
        )
        if pub_trainer.returncode != 0:
            raise RuntimeError(
                f"dotnet publish Spawn.TrainerWorker failed (exit {pub_trainer.returncode}):\n"
                f"{pub_trainer.stdout}\n{pub_trainer.stderr}"
            )

        if stage_obj_root.exists():
            shutil.rmtree(stage_obj_root, ignore_errors=True)

        # Strip native *.pdb (e.g., libSkiaSharp.pdb) in ALL channels.
        stripped_native_pdbs = strip_native_pdbs(stage_app)
        if stripped_native_pdbs:
            print(f"[package] stripped {len(stripped_native_pdbs)} native *.pdb file(s): {stripped_native_pdbs}")

        symbols_zip_name: str | None = None
        symbols_zip_staged: Path | None = None
        if not profile.dev_like:
            # Release channel: strip ALL remaining *.pdb recursively (including Workers\Trainer\)
            # and archive them into <identity>-symbols-<version>-x64.zip outside the installer.
            symbols_zip_name = f"{profile.identity}-symbols-{version}-x64.zip"
            symbols_zip_staged = stage_dir / symbols_zip_name
            archived_pdbs = strip_and_archive_release_pdbs(stage_app, symbols_zip_staged)
            if not archived_pdbs:
                raise RuntimeError(f"Expected managed *.pdb files to archive into {symbols_zip_name}, found none.")
            print(
                f"[package] archived {len(archived_pdbs)} release *.pdb file(s) -> "
                f"{symbols_zip_name} ({symbols_zip_staged.stat().st_size // 1024} KiB)"
            )
        else:
            # Dev channel: verify managed Spawn.*.pdb remain while zero native *.pdb remain.
            remaining_native = [
                p.relative_to(stage_app).as_posix()
                for p in stage_app.rglob("*.pdb")
                if p.is_file() and is_native_pdb(p)
            ]
            if remaining_native:
                raise RuntimeError(f"Native *.pdb files remain in dev stage_app: {remaining_native}")
            expected_managed_pdbs = [
                stage_app / "Spawn.Core.pdb",
                stage_app / "Spawn.OverlayHost.pdb",
                stage_trainer_worker / "Spawn.TrainerWorker.pdb",
            ]
            missing_managed = [str(p.relative_to(stage_app)) for p in expected_managed_pdbs if not p.is_file()]
            if missing_managed:
                raise RuntimeError(f"Expected managed *.pdb files missing in dev stage_app: {missing_managed}")

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
        root_notices_path = resolve_third_party_notices(repo_root)

        for dest_folder in (stage_app, stage_dir):
            (dest_folder / "VERSION.txt").write_text(version_txt, encoding="utf-8", newline="\n")
            shutil.copy2(root_notices_path, dest_folder / "THIRD-PARTY-NOTICES.txt")

        # Verify published executables, self-contained runtime presence, and strict Clean Host / Worker isolation
        host_exe_in_app = stage_app / desktop_exe
        overlay_exe_in_app = stage_app / "Spawn.OverlayHost.exe"
        trainer_exe_in_app = stage_trainer_worker / "Spawn.TrainerWorker.exe"
        if not host_exe_in_app.is_file() or not overlay_exe_in_app.is_file():
            raise RuntimeError(f"Expected published Host/Overlay executables missing in {stage_app}")
        if not trainer_exe_in_app.is_file():
            raise RuntimeError(f"Expected isolated TrainerWorker executable missing at {trainer_exe_in_app}")
        if effective_self_contained:
            if not (stage_app / "coreclr.dll").is_file():
                raise RuntimeError(f"Self-contained runtime (coreclr.dll) missing in {stage_app}")
            if not (stage_trainer_worker / "coreclr.dll").is_file():
                raise RuntimeError(f"Self-contained runtime (coreclr.dll) missing in {stage_trainer_worker}")
        leaked_worker_root_files = sorted(p.name for p in stage_app.glob("Spawn.TrainerWorker*"))
        if leaked_worker_root_files:
            raise RuntimeError(
                f"Clean Host packaging isolation violation: Spawn.TrainerWorker artifacts flattened into "
                f"{stage_app}: {leaked_worker_root_files}"
            )
        if not profile.dev_like:
            leaked_release_pdbs = sorted(
                p.relative_to(stage_app).as_posix() for p in stage_app.rglob("*.pdb") if p.is_file()
            )
            if leaked_release_pdbs:
                raise RuntimeError(f"Release stage_app must contain zero *.pdb files: {leaked_release_pdbs}")

        nsi_path = repo_root / "installer" / "spawn.nsi"
        setup_exe_staged = stage_dir / f"{installer_base}.exe"
        nsis_work_dir = stage_dir / ".nsis"
        print(f"[package] compiling NSIS {NSIS_VERSION} installer {installer_base}.exe...")
        compile_nsis_installer(
            makensis_path,
            nsi_path,
            profile=profile,
            version=version,
            numeric_version=numeric_version,
            icon_file=icon_file,
            app_dir=stage_app,
            out_file=setup_exe_staged,
            work_dir=nsis_work_dir,
            create_shortcuts=create_shortcuts,
            # Dev builds skip solid compression to build faster; both use zlib (see spawn.nsi).
            fast_compression=profile.dev_like,
            cwd=repo_root,
        )
        shutil.rmtree(nsis_work_dir, ignore_errors=True)

        checksum_targets = [
            (f"{installer_base}.exe", setup_exe_staged),
        ]
        if symbols_zip_name is not None and symbols_zip_staged is not None:
            checksum_targets.append((symbols_zip_name, symbols_zip_staged))
        checksum_targets.extend(
            [
                ("VERSION.txt", stage_dir / "VERSION.txt"),
                ("THIRD-PARTY-NOTICES.txt", stage_dir / "THIRD-PARTY-NOTICES.txt"),
                (f"app/{desktop_exe}", host_exe_in_app),
                ("app/Spawn.OverlayHost.exe", overlay_exe_in_app),
                ("app/Workers/Trainer/Spawn.TrainerWorker.exe", trainer_exe_in_app),
            ]
        )
        sha_lines = [f"{sha256_file(fpath)}  {rel_name}" for rel_name, fpath in checksum_targets]
        (stage_dir / "SHA256SUMS.txt").write_text("\n".join(sha_lines) + "\n", encoding="utf-8", newline="\n")

        # Atomic directory replacement into final_dir
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


_KERNEL32_CACHE = None


def _get_kernel32():
    """Return a cached kernel32 WinDLL(use_last_error=True) with explicit 64-bit HANDLE argtypes/restype."""
    global _KERNEL32_CACHE
    if _KERNEL32_CACHE is not None:
        return _KERNEL32_CACHE

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.SetErrorMode.argtypes = [wintypes.UINT]
    k32.SetErrorMode.restype = wintypes.UINT

    k32.GetCurrentProcess.argtypes = []
    k32.GetCurrentProcess.restype = wintypes.HANDLE

    k32.SetPriorityClass.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    k32.SetPriorityClass.restype = wintypes.BOOL

    k32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE

    k32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
    k32.Process32FirstW.restype = wintypes.BOOL

    k32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
    k32.Process32NextW.restype = wintypes.BOOL

    k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    k32.OpenProcess.restype = wintypes.HANDLE

    k32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        wintypes.LPWSTR,
        ctypes.POINTER(wintypes.DWORD),
    ]
    k32.QueryFullProcessImageNameW.restype = wintypes.BOOL

    k32.IsProcessInJob.argtypes = [
        wintypes.HANDLE,
        wintypes.HANDLE,
        ctypes.POINTER(wintypes.BOOL),
    ]
    k32.IsProcessInJob.restype = wintypes.BOOL

    k32.CloseHandle.argtypes = [wintypes.HANDLE]
    k32.CloseHandle.restype = wintypes.BOOL

    k32.CreateProcessW.argtypes = [
        wintypes.LPCWSTR,
        wintypes.LPWSTR,
        wintypes.LPVOID,
        wintypes.LPVOID,
        wintypes.BOOL,
        wintypes.DWORD,
        wintypes.LPVOID,
        wintypes.LPCWSTR,
        ctypes.POINTER(_STARTUPINFOW),
        ctypes.POINTER(_PROCESS_INFORMATION),
    ]
    k32.CreateProcessW.restype = wintypes.BOOL

    _KERNEL32_CACHE = k32
    return k32


def find_running_processes_by_exe(target_exe: Path) -> list[tuple[int, int]]:
    """Enumerate (pid, ppid) pairs matching target_exe strictly via OpenProcess(0x1000)."""
    if os.name != "nt":
        return []
    target_norm = _normalize_win_path(target_exe)
    target_name = target_exe.name.lower()

    k32 = _get_kernel32()

    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == INVALID_HANDLE_VALUE or not snap:
        return []

    matches: list[tuple[int, int]] = []
    try:
        entry = _PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(_PROCESSENTRY32W)
        if not k32.Process32FirstW(snap, ctypes.byref(entry)):
            return []
        while True:
            exe_short = entry.szExeFile.lower()
            pid = int(entry.th32ProcessID)
            ppid = int(entry.th32ParentProcessID)
            if pid > 4 and exe_short == target_name:
                h_proc = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
                if h_proc:
                    try:
                        buf_len = wintypes.DWORD(1024)
                        buf = ctypes.create_unicode_buffer(1024)
                        if k32.QueryFullProcessImageNameW(h_proc, 0, buf, ctypes.byref(buf_len)):
                            if _normalize_win_path(buf.value) == target_norm:
                                matches.append((pid, ppid))
                    finally:
                        k32.CloseHandle(h_proc)
            if not k32.Process32NextW(snap, ctypes.byref(entry)):
                break
    finally:
        k32.CloseHandle(snap)
    return matches


def find_running_pids_by_exe(target_exe: Path) -> list[int]:
    """Enumerate PIDs matching target_exe strictly via OpenProcess(0x1000)."""
    return [pid for pid, _ in find_running_processes_by_exe(target_exe)]


def get_process_parent_pid(pid: int) -> int | None:
    """Look up the parent PID of pid via Toolhelp32 snapshot."""
    if os.name != "nt" or pid <= 0:
        return None
    k32 = _get_kernel32()
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == INVALID_HANDLE_VALUE or not snap:
        return None
    try:
        entry = _PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(_PROCESSENTRY32W)
        if not k32.Process32FirstW(snap, ctypes.byref(entry)):
            return None
        while True:
            if int(entry.th32ProcessID) == pid:
                return int(entry.th32ParentProcessID)
            if not k32.Process32NextW(snap, ctypes.byref(entry)):
                break
    finally:
        k32.CloseHandle(snap)
    return None


def get_process_image_path(pid: int) -> str | None:
    """Read the full executable image path for pid strictly via OpenProcess(0x1000)."""
    if os.name != "nt" or pid <= 0:
        return None
    k32 = _get_kernel32()
    h_proc = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h_proc:
        return None
    try:
        buf_len = wintypes.DWORD(1024)
        buf = ctypes.create_unicode_buffer(1024)
        if k32.QueryFullProcessImageNameW(h_proc, 0, buf, ctypes.byref(buf_len)):
            return buf.value
        return None
    finally:
        k32.CloseHandle(h_proc)


def is_current_process_in_any_job() -> bool:
    """Report whether the current process belongs to any Windows Job Object."""
    if os.name != "nt":
        return False
    k32 = _get_kernel32()
    in_job = wintypes.BOOL(False)
    ctypes.set_last_error(0)
    if not k32.IsProcessInJob(k32.GetCurrentProcess(), None, ctypes.byref(in_job)):
        return False
    return bool(in_job.value)


def is_process_in_any_job(pid: int) -> bool:
    """Report whether pid belongs to any Windows Job Object via OpenProcess(0x1000)."""
    if os.name != "nt" or pid <= 0:
        return False
    k32 = _get_kernel32()
    h_proc = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h_proc:
        return False
    try:
        in_job = wintypes.BOOL(False)
        ctypes.set_last_error(0)
        if not k32.IsProcessInJob(h_proc, None, ctypes.byref(in_job)):
            return False
        return bool(in_job.value)
    finally:
        k32.CloseHandle(h_proc)


def _windows_system_root() -> str:
    for key in ("SystemRoot", "WINDIR"):
        val = os.environ.get(key, "").strip()
        if val:
            return val
    return ""


def is_explorer_image(image_path: str | None, system_root: str | None = None) -> bool:
    """Report whether image_path is %SystemRoot%\\explorer.exe (case-insensitive)."""
    raw_path = (image_path or "").strip()
    root = (system_root if system_root is not None else _windows_system_root()).strip()
    if not raw_path or not root:
        return False
    want = os.path.normcase(os.path.normpath(os.path.join(root, "explorer.exe")))
    actual = os.path.normcase(os.path.normpath(raw_path))
    return actual == want


def verify_parent_is_explorer(
    pid: int,
    reported_ppid: int | None = None,
    *,
    self_pid: int | None = None,
    system_root: str | None = None,
    lookup_parent=None,
    lookup_image=None,
) -> bool:
    """Verify that pid's live parent process is %SystemRoot%\\explorer.exe (outside this process tree)."""
    if pid <= 0:
        return False
    parent_fn = lookup_parent or get_process_parent_pid
    image_fn = lookup_image or get_process_image_path
    cur_pid = os.getpid() if self_pid is None else self_pid

    actual_ppid = parent_fn(pid)
    if actual_ppid is not None:
        if reported_ppid and actual_ppid != reported_ppid:
            return False
        parent_pid = actual_ppid
    else:
        parent_pid = reported_ppid or 0

    if parent_pid <= 0 or parent_pid == cur_pid or parent_pid == pid:
        return False

    parent_img = image_fn(parent_pid)
    return is_explorer_image(parent_img, system_root=system_root)


def resolve_shell_launch_job_state(
    pid: int,
    reported_ppid: int | None = None,
    *,
    verify_explorer=None,
    pid_in_job=None,
) -> tuple[int, bool, bool]:
    """Compute (pid, in_job, via_shell) for a process spawned via Explorer ShellExecute.

    On Windows 10/11, Explorer / Program Compatibility Assistant routinely places
    ShellExecute-launched GUI apps into a system shell job object (or an app may
    create its own job object). Raw IsProcessInJob(h, NULL) checks membership in
    ANY job and would falsely report in_job=True even though the process is
    explorer.exe's child and NOT in the caller/agent command job. Once explorer.exe
    is verified as the parent, report in_job=False, via_shell=True directly.
    """
    verifier = verify_explorer or verify_parent_is_explorer
    job_probe = pid_in_job or is_process_in_any_job
    if verifier(pid, reported_ppid):
        return pid, False, True
    return pid, bool(job_probe(pid)), False


def _build_unicode_env_block(env: dict[str, str] | None):
    if env is None:
        return None
    items = sorted(env.items(), key=lambda kv: kv[0].upper())
    joined = "\0".join(f"{k}={v}" for k, v in items) + "\0\0"
    return ctypes.create_unicode_buffer(joined)


def lookup_uninstall_entry(app_id: str) -> dict[str, str] | None:
    """Read HKCU\\...\\Uninstall\\<app_id> (io.github.chialecode.<identity>), or None when absent."""
    if os.name != "nt":
        return None
    subkey = UNINSTALL_KEY_PREFIX + app_id
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


def launch_detached_background(
    exe_path: Path,
    args: list[str],
    *,
    allow_desktop_shell: bool = True,
    clean_env: dict[str, str] | None = None,
) -> tuple[int | None, bool, bool]:
    """Launch exe_path with args outside the current command runner Job Object.

    Returns (pid, in_job, via_shell).
    1. For interactive non-test identities (`allow_desktop_shell=True`), delegates to
       explorer.exe out-of-proc Shell COM (`CLSID_ShellWindows` SWC_DESKTOP=8 ->
       `Document.Application.ShellExecute`) so explorer.exe itself parents the tray
       process cleanly outside any caller/agent job hierarchy, and verifies that the
       spawned process's parent is `%SystemRoot%\\explorer.exe` before reporting
       `in_job=False, via_shell=True`.
    2. When the desktop shell is unavailable (or disabled for isolated test identities),
       falls back to `CreateProcessW` with `CREATE_BREAKAWAY_FROM_JOB`, retrying
       without breakaway on `ERROR_ACCESS_DENIED`.
    """
    if os.name != "nt":
        raise RuntimeError("Background tray launch requires Windows.")

    k32 = _get_kernel32()
    self_in_job = is_current_process_in_any_job()

    # 1. Preferred path: marshal via out-of-proc explorer.exe CLSID_ShellWindows (SWC_DESKTOP=8 ->
    # Document.Application.ShellExecute) so explorer.exe itself parents the tray process outside any
    # command runner Job Object (including nested jobs where CREATE_BREAKAWAY_FROM_JOB is insufficient).
    if allow_desktop_shell:
        before_pids = {pid for pid, _ in find_running_processes_by_exe(exe_path)}
        ps_exe = shutil.which("powershell") or "powershell.exe"
        if not shutil.which(ps_exe):
            ps_exe = shutil.which("pwsh") or "powershell.exe"
        ps_script = (
            "$ErrorActionPreference = 'Stop'; "
            "$t = [Type]::GetTypeFromCLSID([Guid]'9BA05972-F6A8-11CF-A442-00A0C90A8F39'); "
            "$sw = [Activator]::CreateInstance($t); "
            "$m = [System.Reflection.Missing]::Value; "
            "$d = $t.InvokeMember('FindWindowSW', [System.Reflection.BindingFlags]::InvokeMethod, $null, $sw, @($m, $m, 8, 0, 1)); "
            "if ($null -eq $d) { exit 2 }; "
            "$doc = $d.GetType().InvokeMember('Document', [System.Reflection.BindingFlags]::GetProperty, $null, $d, $null); "
            "$app = $doc.GetType().InvokeMember('Application', [System.Reflection.BindingFlags]::GetProperty, $null, $doc, $null); "
            "$null = $app.GetType().InvokeMember('ShellExecute', [System.Reflection.BindingFlags]::InvokeMethod, $null, $app, "
            "@($env:SPAWN_LAUNCH_EXE, $env:SPAWN_LAUNCH_ARGS, $env:SPAWN_LAUNCH_CWD, 'open', 1))"
        )
        proc = _run_quiet(
            [
                ps_exe,
                "-NoProfile",
                "-NonInteractive",
                "-Sta",
                "-ExecutionPolicy",
                "Bypass",
                "-WindowStyle",
                "Hidden",
                "-Command",
                ps_script,
            ],
            cwd=exe_path.parent,
            env={
                "SPAWN_LAUNCH_EXE": str(exe_path),
                "SPAWN_LAUNCH_ARGS": subprocess.list2cmdline(args),
                "SPAWN_LAUNCH_CWD": str(exe_path.parent),
            },
            timeout=20.0,
        )
        if proc.returncode == 0:
            deadline = time.monotonic() + 15.0
            fallback_candidate: tuple[int, int] | None = None
            fallback_seen_at: float | None = None
            while time.monotonic() < deadline:
                current = find_running_processes_by_exe(exe_path)
                all_pids = {pid for pid, _ in current}
                candidates = [
                    (pid, ppid)
                    for pid, ppid in current
                    if pid not in before_pids and ppid not in all_pids
                ]
                for pid, ppid in candidates:
                    if verify_parent_is_explorer(pid, ppid):
                        return pid, False, True
                if candidates:
                    if fallback_candidate is None:
                        fallback_candidate = candidates[0]
                        fallback_seen_at = time.monotonic()
                    elif fallback_seen_at is not None and (time.monotonic() - fallback_seen_at) >= 1.0:
                        return resolve_shell_launch_job_state(fallback_candidate[0], fallback_candidate[1])
                time.sleep(0.2)
            if fallback_candidate is not None:
                return resolve_shell_launch_job_state(fallback_candidate[0], fallback_candidate[1])

    # 2. Fallback: direct CreateProcessW with CREATE_BREAKAWAY_FROM_JOB (when desktop shell is unavailable)
    cmd_line = subprocess.list2cmdline([str(exe_path), *args])
    env_block = _build_unicode_env_block(clean_env)
    env_flag = CREATE_UNICODE_ENVIRONMENT if env_block is not None else 0
    si = _STARTUPINFOW()
    si.cb = ctypes.sizeof(_STARTUPINFOW)

    def _spawn_direct(extra_flags: int) -> tuple[int, bool, int]:
        cmd_buf = ctypes.create_unicode_buffer(cmd_line)
        pi = _PROCESS_INFORMATION()
        ctypes.set_last_error(0)
        ok = k32.CreateProcessW(
            str(exe_path),
            cmd_buf,
            None,
            None,
            False,
            DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW | env_flag | extra_flags,
            env_block,
            str(exe_path.parent),
            ctypes.byref(si),
            ctypes.byref(pi),
        )
        if not ok:
            return 0, False, ctypes.get_last_error()
        try:
            if not self_in_job:
                # Caller is not inside any job object, so the child cannot be trapped in the caller's job.
                return int(pi.dwProcessId), False, 0
            in_job = wintypes.BOOL(False)
            k32.IsProcessInJob(pi.hProcess, None, ctypes.byref(in_job))
            return int(pi.dwProcessId), bool(in_job.value), 0
        finally:
            k32.CloseHandle(pi.hThread)
            k32.CloseHandle(pi.hProcess)

    pid_b, in_job_b, err_b = _spawn_direct(CREATE_BREAKAWAY_FROM_JOB)
    if pid_b > 0:
        return pid_b, in_job_b, False

    if err_b == ERROR_ACCESS_DENIED:
        pid_n, in_job_n, _ = _spawn_direct(0)
        if pid_n > 0:
            return pid_n, in_job_n, False

    raise RuntimeError(f"Failed to launch {exe_path} in background tray (lastError={err_b}).")


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


def request_graceful_quit_and_wait(
    exe_path: Path,
    timeout_sec: float = QUIT_TIMEOUT_SECONDS,
    *,
    clean_env: dict[str, str] | None = None,
) -> tuple[bool, int]:
    """Ask running instance (and companion OverlayHost / TrainerWorker) to exit via --quit-for-update; never force-kills."""
    companion_overlay = exe_path.parent / "Spawn.OverlayHost.exe"
    companion_trainer = exe_path.parent / "Workers" / "Trainer" / "Spawn.TrainerWorker.exe"
    host_pids = find_running_pids_by_exe(exe_path)
    overlay_pids = find_running_pids_by_exe(companion_overlay)
    trainer_pids = find_running_pids_by_exe(companion_trainer)
    if not host_pids and not overlay_pids and not trainer_pids:
        return False, 0

    t0 = time.monotonic()
    if host_pids and exe_path.is_file():
        # The helper itself waits up to 25 s for the running copy to exit; allow it the full wait.
        try:
            _run_quiet(
                [str(exe_path), "--quit-for-update"],
                cwd=exe_path.parent,
                base_env=clean_env,
                timeout=timeout_sec,
            )
        except subprocess.TimeoutExpired:
            pass

    deadline = t0 + timeout_sec
    while time.monotonic() < deadline:
        if (
            not find_running_pids_by_exe(exe_path)
            and not find_running_pids_by_exe(companion_overlay)
            and not find_running_pids_by_exe(companion_trainer)
        ):
            return True, int((time.monotonic() - t0) * 1000)
        time.sleep(QUIT_POLL_SECONDS)

    raise TimeoutError(
        f"{exe_path.name} (or companion Spawn.OverlayHost.exe / Workers\\Trainer\\Spawn.TrainerWorker.exe) "
        f"is still running after {int(timeout_sec)}s; installation aborted without force-kill."
    )


def retire_locked_binaries(install_dir: Path, identity: str) -> int:
    """Move any kernel SEC_IMAGE-cached DLL/EXE out of install_dir before the installer runs.

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
        if fpath.name.lower() == "uninstall.exe":
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


def is_app_window_open(exe_path: Path) -> bool:
    """True when a process running exe_path shows a visible, unowned, non-tool top-level window.

    A window hidden to the tray is not visible; a minimized one still counts as open.
    """
    if os.name != "nt":
        return False

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    gw_owner = 4
    gwl_exstyle = -20
    ws_ex_toolwindow = 0x00000080

    enum_proc_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows.argtypes = [enum_proc_type, wintypes.LPARAM]
    user32.EnumWindows.restype = wintypes.BOOL
    user32.IsWindowVisible.argtypes = [wintypes.HWND]
    user32.IsWindowVisible.restype = wintypes.BOOL
    user32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
    user32.GetWindow.restype = wintypes.HWND
    user32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
    user32.GetWindowLongW.restype = wintypes.LONG
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.GetWindowThreadProcessId.restype = wintypes.DWORD
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        wintypes.LPWSTR,
        ctypes.POINTER(wintypes.DWORD),
    ]
    kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL

    wanted = os.path.normcase(os.path.abspath(str(exe_path)))
    image_by_pid: dict[int, str] = {}
    found = False

    def image_of(pid: int) -> str:
        if pid not in image_by_pid:
            image = ""
            handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
            if handle:
                try:
                    size = wintypes.DWORD(32768)
                    buf = ctypes.create_unicode_buffer(size.value)
                    if kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
                        image = os.path.normcase(buf.value)
                finally:
                    kernel32.CloseHandle(handle)
            image_by_pid[pid] = image
        return image_by_pid[pid]

    def on_window(hwnd: int, _lparam: int) -> bool:
        nonlocal found
        if not user32.IsWindowVisible(hwnd) or user32.GetWindow(hwnd, gw_owner):
            return True
        if user32.GetWindowLongW(hwnd, gwl_exstyle) & ws_ex_toolwindow:
            return True
        pid = wintypes.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value and image_of(pid.value) == wanted:
            found = True
            return False
        return True

    user32.EnumWindows(enum_proc_type(on_window), 0)
    return found


def wait_while_window_open(
    exe_path: Path,
    *,
    timeout_s: float = WINDOW_DEFER_TIMEOUT_SECONDS,
    poll_s: float = WINDOW_DEFER_POLL_SECONDS,
    is_window_open: Callable[[Path], bool] | None = None,
    sleep: Callable[[float], None] = time.sleep,
    monotonic: Callable[[], float] = time.monotonic,
) -> tuple[bool, bool, int | None]:
    """Wait while exe_path shows a window: returns (was_open, still_open, waited_ms)."""
    check = is_window_open or is_app_window_open
    if not check(exe_path):
        return False, False, None
    start = monotonic()
    while check(exe_path):
        waited = monotonic() - start
        if waited >= timeout_s:
            return True, True, int(waited * 1000)
        sleep(min(poll_s, timeout_s - waited))
    return True, False, int((monotonic() - start) * 1000)


def new_install_receipt(profile: IdentityProfile, new_version: str) -> dict[str, object]:
    """Install receipt with the unified camelCase fields, each present from the start."""
    return {
        "identity": profile.identity,
        "displayName": profile.display_name,
        "installed": False,
        "skipped": False,
        "reason": None,
        "error": None,
        "firstInstall": False,
        "oldVersion": None,
        "newVersion": new_version,
        "installLocation": None,
        "wasRunning": False,
        "windowOpen": False,
        "deferred": False,
        "deferMs": None,
        "quitMs": None,
        "relaunched": False,
        "background": False,
        "inJob": False,
        "guidance": None,
        "elapsedMs": 0,
        "at": datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }


def build_silent_install_command(setup_exe: Path, install_dir: Path | None) -> str:
    """Command line for a silent NSIS install; /D= must come last and unquoted (NSIS reads it raw)."""
    cmd = subprocess.list2cmdline([str(setup_exe), "/S"])
    if install_dir is not None:
        cmd += f" /D={install_dir}"
    return cmd


def install_package(
    repo_root: Path,
    profile: IdentityProfile,
    setup_exe: Path,
    expected_version: str,
    *,
    install_dir_override: Path | None,
    relaunch_background: bool,
    receipt_path: Path,
    is_dev_auto_install: bool | None = None,
) -> dict[str, object]:
    """Silently install setup_exe for profile and write the receipt.

    Installs on first use too. For the automatic SPAWN Dev install, waits while its window is
    open (up to 10 minutes, then skips with reason window_open); a copy running in the tray is
    asked to --quit-for-update (never force-killed).
    """
    t0 = time.monotonic()
    receipt = new_install_receipt(profile, expected_version)

    def save_and_return() -> dict[str, object]:
        receipt["elapsedMs"] = int((time.monotonic() - t0) * 1000)
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        receipt_path.write_text(
            json.dumps(receipt, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        return receipt

    effective_dev_auto = (profile.identity == "spawn-dev") if is_dev_auto_install is None else bool(is_dev_auto_install)
    if effective_dev_auto:
        skip_reason = should_skip_dev_auto_install()
        if skip_reason is not None:
            receipt["skipped"] = True
            receipt["reason"] = skip_reason
            print(f"[install-dev] {skip_reason}")
            return save_and_return()

    if os.name != "nt":
        receipt["error"] = "Local installer execution requires Windows."
        save_and_return()
        raise RuntimeError(str(receipt["error"]))

    try:
        prior = lookup_uninstall_entry(profile.app_id)
        prior_loc = ""
        if prior is not None:
            receipt["oldVersion"] = prior.get("DisplayVersion") or None
            prior_loc = prior.get("InstallLocation", "").strip()
        receipt["firstInstall"] = prior is None

        default_loc = (
            install_dir_override
            if install_dir_override is not None
            else Path(os.environ["LOCALAPPDATA"]) / "Programs" / profile.identity
        )
        current_loc = Path(prior_loc) if prior_loc else default_loc
        receipt["installLocation"] = str(current_loc)
        probe_exe = current_loc / f"{profile.identity}.exe"

        if effective_dev_auto:
            # An open window means the user is working in SPAWN Dev: wait for it to close or hide
            # to the tray, bounded, before asking it to quit.
            was_open, still_open, waited_ms = wait_while_window_open(probe_exe)
            if was_open:
                receipt["windowOpen"] = True
                receipt["deferred"] = True
                receipt["deferMs"] = waited_ms
            if still_open:
                receipt["skipped"] = True
                receipt["reason"] = "window_open"
                receipt["guidance"] = (
                    f"The {profile.display_name} window stayed open for {int(WINDOW_DEFER_TIMEOUT_SECONDS)}s, "
                    "so this build was not installed. Close the window (or hide it to the tray) and run "
                    "`python -B scripts/package.py --install-dev --install-only` again."
                )
                print(f"[install-dev] skipped: {receipt['guidance']}")
                return save_and_return()

        clean_app_env = build_clean_app_env(profile)
        was_running, quit_ms = request_graceful_quit_and_wait(probe_exe, clean_env=clean_app_env)
        receipt["wasRunning"] = was_running
        if was_running:
            receipt["quitMs"] = quit_ms

        retired_files = retire_locked_binaries(probe_exe.parent, profile.identity)
        if retired_files:
            receipt["retiredLockedFiles"] = retired_files

        setup_cmd = build_silent_install_command(setup_exe, install_dir_override)
        print(f"[install-dev] silently installing {profile.display_name} {expected_version}...")
        proc = _run_quiet(setup_cmd, cwd=repo_root, base_env=clean_app_env, timeout=180.0)
        if proc.returncode != 0:
            receipt["reason"] = f"installer_failed_exit_{proc.returncode}"
            why = INSTALLER_EXIT_REASONS.get(proc.returncode, "installer failed")
            raise RuntimeError(f"Installer exited with code {proc.returncode} ({why}).")

        after = lookup_uninstall_entry(profile.app_id)
        if not after:
            receipt["reason"] = "uninstall_entry_missing"
            raise RuntimeError(f"HKCU Uninstall entry {profile.app_id} missing after silent install.")
        actual_ver = after.get("DisplayVersion", "")
        if actual_ver != expected_version:
            receipt["reason"] = "version_mismatch"
            raise RuntimeError(
                f"Installed DisplayVersion mismatch: expected {expected_version!r}, got {actual_ver!r}"
            )

        installed_loc = Path(after.get("InstallLocation", "") or str(default_loc))
        installed_exe = installed_loc / f"{profile.identity}.exe"
        installed_trainer = installed_loc / "Workers" / "Trainer" / "Spawn.TrainerWorker.exe"
        installed_notices = installed_loc / "THIRD-PARTY-NOTICES.txt"
        if not installed_exe.is_file():
            raise RuntimeError(f"Installed executable missing at {installed_exe}")
        if not installed_trainer.is_file():
            raise RuntimeError(f"Installed isolated TrainerWorker missing at {installed_trainer}")
        if not installed_notices.is_file():
            raise RuntimeError(f"Installed THIRD-PARTY-NOTICES.txt missing at {installed_notices}")
        leaked_installed_root = sorted(p.name for p in installed_loc.glob("Spawn.TrainerWorker*"))
        if leaked_installed_root:
            raise RuntimeError(
                f"Installed root violates Clean Host isolation (Spawn.TrainerWorker* found in {installed_loc}): "
                f"{leaked_installed_root}"
            )
        if not profile.dev_like:
            installed_pdbs = sorted(
                p.relative_to(installed_loc).as_posix() for p in installed_loc.rglob("*.pdb") if p.is_file()
            )
            if installed_pdbs:
                raise RuntimeError(
                    f"Release installation must contain zero *.pdb files in {installed_loc}: {installed_pdbs}"
                )

        bridge_file = write_dev_workspace_bridge(repo_root, profile, expected_version)
        receipt["installed"] = True
        receipt["installLocation"] = str(installed_loc)
        receipt["installedExe"] = str(installed_exe)
        receipt["installedTrainerWorker"] = str(installed_trainer)
        receipt["installedNotices"] = str(installed_notices)
        if bridge_file is not None:
            receipt["devWorkspaceBridge"] = str(bridge_file)

        if relaunch_background:
            allow_shell = not profile.identity.endswith("-test")
            pid, in_job, via_shell = launch_detached_background(
                installed_exe,
                ["--background"],
                allow_desktop_shell=allow_shell,
                clean_env=clean_app_env,
            )
            receipt["relaunched"] = True
            receipt["background"] = True
            receipt["viaShell"] = via_shell
            receipt["inJob"] = in_job
            if pid is not None:
                receipt["pid"] = pid
            print(
                f"[install-dev] {profile.display_name} {expected_version} installed & running in tray "
                f"(pid={pid}, viaShell={via_shell}, inJob={in_job})"
            )
        else:
            print(f"[install-dev] {profile.display_name} {expected_version} installed (relaunch skipped).")

        return save_and_return()
    except Exception as exc:
        receipt["error"] = str(exc)
        if receipt["reason"] is None:
            receipt["reason"] = "install_failed"
        save_and_return()
        raise


def uninstall_identity(repo_root: Path, profile: IdentityProfile, *, allow_release: bool) -> int:
    if profile.identity == "spawn" and not allow_release:
        raise RuntimeError("Refusing silent uninstall of production 'spawn' without --confirm-release-uninstall.")

    entry = lookup_uninstall_entry(profile.app_id)
    if not entry:
        print(f"[uninstall] {profile.identity} is not currently installed.")
        return 0

    loc = entry.get("InstallLocation", "").strip()
    install_dir = Path(loc) if loc else Path(os.environ["LOCALAPPDATA"]) / "Programs" / profile.identity
    request_graceful_quit_and_wait(install_dir / f"{profile.identity}.exe", clean_env=build_clean_app_env(profile))
    unins_exe = install_dir / "Uninstall.exe"
    if not unins_exe.is_file():
        raise FileNotFoundError(f"Uninstaller not found at {unins_exe}")

    proc = _run_quiet([str(unins_exe), "/S"], cwd=repo_root, timeout=120.0)
    if proc.returncode != 0:
        why = INSTALLER_EXIT_REASONS.get(proc.returncode, "uninstaller failed")
        raise RuntimeError(f"Uninstaller exited with code {proc.returncode} ({why}).")

    # The NSIS uninstaller copies itself to %TEMP% and returns at once: wait until it removed the
    # uninstall entry and itself.
    deadline = time.monotonic() + UNINSTALL_REMOVAL_TIMEOUT_SECONDS
    while lookup_uninstall_entry(profile.app_id) is not None or unins_exe.exists():
        if time.monotonic() >= deadline:
            raise RuntimeError(
                f"{profile.identity} uninstall did not finish within {int(UNINSTALL_REMOVAL_TIMEOUT_SECONDS)}s "
                f"(uninstall entry or {unins_exe.name} still present)."
            )
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
    parser.add_argument("--install-dir", type=Path, default=None, help="Optional custom /D= target (for isolated tests).")
    parser.add_argument("--no-shortcuts", action="store_true", help="Disable Start Menu and Desktop shortcut creation.")
    parser.add_argument("--no-relaunch", action="store_true", help="Do not launch --background after install.")
    parser.add_argument(
        "--self-contained",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="Publish self-contained win-x64 runtime (default: true for release, false for dev).",
    )
    parser.add_argument("--receipt-path", type=Path, default=None)
    parser.add_argument(
        "--fetch-nsis",
        action="store_true",
        help="Only fetch the pinned NSIS build into .tools/ (SHA-256 checked) and print makensis.exe.",
    )
    args = parser.parse_args()

    if args.fetch_nsis:
        print(find_makensis())
        return 0

    repo_root = REPO_ROOT

    if args.install_dev and args.identity is None:
        identity_key = "spawn-dev"
    elif args.identity is not None:
        identity_key = args.identity
    else:
        identity_key = "spawn-dev" if args.channel == "dev" else "spawn"

    profile = IDENTITY_PROFILES[identity_key]

    if args.uninstall:
        return uninstall_identity(repo_root, profile, allow_release=args.confirm_release_uninstall)

    if args.version:
        version = args.version
    elif profile.dev_like:
        version = compute_dev_version(repo_root)
    else:
        version = resolve_next_release_version(repo_root)

    default_receipt_name = "install-dev.json" if profile.identity == "spawn-dev" else f"install-{profile.identity}.json"
    stage_dir_name = resolve_current_stage(repo_root)
    receipt_path = args.receipt_path or (repo_root / ".local" / "reports" / stage_dir_name / default_receipt_name)

    if args.install_dev:
        skip_reason = should_skip_dev_auto_install()
        if skip_reason is not None:
            skipped_receipt = new_install_receipt(profile, version)
            skipped_receipt["skipped"] = True
            skipped_receipt["reason"] = skip_reason
            receipt_path.parent.mkdir(parents=True, exist_ok=True)
            receipt_path.write_text(
                json.dumps(skipped_receipt, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
                newline="\n",
            )
            print(f"[install-dev] {skip_reason}")
            return 0

    create_shortcuts = profile.default_shortcuts and (not args.no_shortcuts)
    self_contained = (not profile.dev_like) if args.self_contained is None else bool(args.self_contained)

    if args.install_only:
        setup_exe, version = find_existing_installer(repo_root, profile)
    else:
        _, setup_exe = build_package(
            repo_root,
            profile,
            version,
            create_shortcuts=create_shortcuts,
            self_contained=self_contained,
        )

    if args.install_dev or args.install or args.install_only:
        install_package(
            repo_root,
            profile,
            setup_exe,
            version,
            # /D= needs an absolute folder.
            install_dir_override=args.install_dir.resolve() if args.install_dir else None,
            relaunch_background=(not args.no_relaunch),
            receipt_path=receipt_path,
            is_dev_auto_install=(args.install_dev or profile.identity == "spawn-dev"),
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
