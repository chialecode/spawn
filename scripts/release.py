"""Release planner, signed release builder, and isolated spawn-test install drill for SPAWN.

Requires Python 3.12+ standard library only (zero pip dependencies).
`--plan` runs on both Linux (ubuntu-24.04) and Windows without Windows-only imports.
`--build` and `--drill-install` require Windows + .NET 10 SDK + Inno Setup 7.

Usage examples:
  python3 -B scripts/release.py --plan
  python -B scripts/release.py --build --version 0.1.1
  python -B scripts/release.py --drill-install --version 0.1.1
"""

from __future__ import annotations

import argparse
import ctypes
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
import tempfile
import zipfile

DEFAULT_BASE_VERSION = "0.1.1"
DEFAULT_REPO_URL = "https://github.com/chialecode/spawn"
SIGNING_KEY_ENV_VAR = "SPAWN_UPDATE_SIGNING_KEY"

BELOW_NORMAL_PRIORITY_CLASS = 0x00004000
CREATE_NO_WINDOW = 0x08000000

_SEMVER_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")
_SHA256_HEX_RE = re.compile(r"^[0-9a-f]{64}$")
_SHA256SUM_LINE_RE = re.compile(r"^([0-9a-f]{64})  ([^/\\]+)$")


@dataclass(frozen=True)
class ReleasePlan:
    release: bool
    version: str
    previous_tag: str
    commit_count: int
    base_version: str


def _configure_windows_quiet_mode() -> None:
    if os.name != "nt":
        return
    try:
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.SetErrorMode(0x0001 | 0x0002 | 0x8000)
        k32.SetPriorityClass(k32.GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS)
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
    merged_env = os.environ.copy() if env is None else dict(env)
    merged_env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    merged_env["DOTNET_NOLOGO"] = "1"
    merged_env["MSBUILDDISABLENODEREUSE"] = "1"
    return subprocess.run(
        cmd,
        cwd=str(cwd),
        env=merged_env,
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=flags,
        timeout=timeout,
        check=False,
    )


def _redact_secret(text: str, secret_value: str | None) -> str:
    if not text or not secret_value:
        return text
    redacted = text.replace(secret_value, "<redacted>")
    try:
        resolved = str(Path(secret_value).resolve())
        if resolved and resolved != secret_value:
            redacted = redacted.replace(resolved, "<redacted>")
    except OSError:
        pass
    return redacted


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fp:
        while chunk := fp.read(1024 * 1024):
            h.update(chunk)
    return h.hexdigest()


def parse_semver_triple(version_str: str) -> tuple[int, int, int]:
    m = _SEMVER_RE.fullmatch(version_str.strip())
    if not m:
        raise ValueError(f"Expected strict Major.Minor.Patch version, got {version_str!r}")
    return int(m.group(1)), int(m.group(2)), int(m.group(3))


def read_next_base_version(repo_root: Path) -> str:
    """Read NEXT_BASE_VERSION from scripts/package.py without importing Windows-only modules."""
    package_py = repo_root / "scripts" / "package.py"
    if package_py.is_file():
        text = package_py.read_text(encoding="utf-8")
        m = re.search(r'^NEXT_BASE_VERSION\s*=\s*"(\d+\.\d+\.\d+)"', text, re.MULTILINE)
        if m:
            return m.group(1)
    return DEFAULT_BASE_VERSION


def compute_release_plan(
    repo_root: Path,
    *,
    base_version: str | None = None,
    head_ref: str = "HEAD",
) -> ReleasePlan:
    """Compute whether a new release should be built and what its version is.

    Takes <major>.<minor> from NEXT_BASE_VERSION, reads git tags `v<major>.<minor>.*`,
    picks the highest integer patch + 1 (or NEXT_BASE_VERSION if no matching tags exist),
    and sets release=True iff no matching tag exists or first-parent commit count > 0.
    """
    resolved_base = (base_version or read_next_base_version(repo_root)).strip()
    major, minor, _ = parse_semver_triple(resolved_base)

    tag_pattern = f"v{major}.{minor}.*"
    tag_proc = _run_quiet(["git", "tag", "-l", tag_pattern], cwd=repo_root, timeout=15.0)
    if tag_proc.returncode != 0:
        raise RuntimeError(f"git tag -l failed (exit {tag_proc.returncode}): {tag_proc.stderr.strip()}")

    exact_tag_re = re.compile(rf"^v{major}\.{minor}\.(\d+)$")
    matching_patches: list[int] = []
    for raw_line in tag_proc.stdout.splitlines():
        m = exact_tag_re.fullmatch(raw_line.strip())
        if m:
            matching_patches.append(int(m.group(1)))

    if not matching_patches:
        return ReleasePlan(
            release=True,
            version=resolved_base,
            previous_tag="",
            commit_count=0,
            base_version=resolved_base,
        )

    max_patch = max(matching_patches)
    previous_tag = f"v{major}.{minor}.{max_patch}"
    next_version = f"{major}.{minor}.{max_patch + 1}"

    count_proc = _run_quiet(
        ["git", "rev-list", "--first-parent", "--count", f"{previous_tag}..{head_ref}"],
        cwd=repo_root,
        timeout=15.0,
    )
    if count_proc.returncode != 0:
        raise RuntimeError(
            f"git rev-list --first-parent --count failed (exit {count_proc.returncode}): {count_proc.stderr.strip()}"
        )

    commit_count = int(count_proc.stdout.strip())
    return ReleasePlan(
        release=(commit_count > 0),
        version=next_version,
        previous_tag=previous_tag,
        commit_count=commit_count,
        base_version=resolved_base,
    )


def format_plan_lines(plan: ReleasePlan) -> str:
    return (
        f"release={'true' if plan.release else 'false'}\n"
        f"version={plan.version}\n"
        f"previous_tag={plan.previous_tag}\n"
    )


def emit_plan_output(plan: ReleasePlan, *, github_output_path: Path | None = None) -> str:
    output = format_plan_lines(plan)
    sys.stdout.write(output)
    sys.stdout.flush()

    target = github_output_path
    if target is None:
        raw_gh_out = os.environ.get("GITHUB_OUTPUT", "").strip()
        if raw_gh_out:
            target = Path(raw_gh_out)
    if target is not None:
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("a", encoding="utf-8", newline="\n") as fp:
            fp.write(output)
    return output


def expected_release_download_assets(version: str) -> list[str]:
    parse_semver_triple(version)
    return [
        f"spawn-setup-{version}-x64.exe",
        f"spawn-symbols-{version}-x64.zip",
        "update-windows-x64.json",
        "update-windows-x64.json.sig",
        "VERSION.txt",
        "THIRD-PARTY-NOTICES.txt",
    ]


def expected_release_dist_files(version: str) -> list[str]:
    return [
        *expected_release_download_assets(version),
        "notes.md",
        "SHA256SUMS",
    ]


def build_update_manifest(
    *,
    version: str,
    installer_name: str,
    installer_size: int,
    installer_sha256: str,
    published_utc: str,
    repo_url: str = DEFAULT_REPO_URL,
) -> dict[str, object]:
    parse_semver_triple(version)
    expected_name = f"spawn-setup-{version}-x64.exe"
    if installer_name != expected_name:
        raise ValueError(f"Installer name {installer_name!r} does not match expected {expected_name!r}")
    if installer_size <= 0:
        raise ValueError(f"Installer size must be positive, got {installer_size}")
    if not _SHA256_HEX_RE.fullmatch(installer_sha256):
        raise ValueError(f"Invalid lowercase SHA-256 hex digest: {installer_sha256!r}")

    base_url = repo_url.rstrip("/")
    return {
        "schemaVersion": 1,
        "channel": "release",
        "version": version,
        "installer": {
            "name": installer_name,
            "url": f"{base_url}/releases/download/v{version}/{installer_name}",
            "size": installer_size,
            "sha256": installer_sha256,
        },
        "publishedUtc": published_utc,
        "notesUrl": f"{base_url}/releases/tag/v{version}",
    }


def sign_manifest_file(
    repo_root: Path,
    manifest_path: Path,
    sig_path: Path,
    *,
    env: dict[str, str] | None = None,
) -> None:
    """Sign manifest_path via scripts/sign_update.cs using SPAWN_UPDATE_SIGNING_KEY (never logged)."""
    child_env = os.environ.copy() if env is None else dict(env)
    key_path_str = child_env.get(SIGNING_KEY_ENV_VAR, "").strip()
    if not key_path_str:
        raise RuntimeError(f"Environment variable {SIGNING_KEY_ENV_VAR} must be set to sign release manifest.")
    if not Path(key_path_str).is_file():
        raise RuntimeError(f"Signing key referenced by {SIGNING_KEY_ENV_VAR} does not exist.")

    sign_script = repo_root / "scripts" / "sign_update.cs"
    if not sign_script.is_file():
        raise FileNotFoundError(f"Signing tool missing: {sign_script}")

    proc = _run_quiet(
        ["dotnet", "run", str(sign_script), "--", "sign", str(manifest_path), str(sig_path)],
        cwd=repo_root,
        env=child_env,
        timeout=120.0,
    )
    if proc.returncode != 0:
        safe_out = _redact_secret(proc.stdout.strip(), key_path_str)
        safe_err = _redact_secret(proc.stderr.strip(), key_path_str)
        raise RuntimeError(
            f"sign_update.cs sign failed (exit {proc.returncode}): {safe_out} {safe_err}".strip()
        )
    if not sig_path.is_file() or sig_path.stat().st_size == 0:
        raise RuntimeError(f"sign_update.cs completed but signature file is missing or empty: {sig_path.name}")


def verify_manifest_signature(
    repo_root: Path,
    manifest_path: Path,
    sig_path: Path,
    public_key_path: Path,
) -> None:
    """Verify DER ECDSA P-256 / SHA-256 signature via scripts/sign_update.cs."""
    sign_script = repo_root / "scripts" / "sign_update.cs"
    if not sign_script.is_file():
        raise FileNotFoundError(f"Signing tool missing: {sign_script}")
    if not public_key_path.is_file():
        raise FileNotFoundError(f"Public signing key missing: {public_key_path}")

    proc = _run_quiet(
        [
            "dotnet",
            "run",
            str(sign_script),
            "--",
            "verify",
            str(manifest_path),
            str(sig_path),
            str(public_key_path),
        ],
        cwd=repo_root,
        timeout=120.0,
    )
    if proc.returncode != 0:
        raise RuntimeError(
            f"Manifest signature verification failed (exit {proc.returncode}): "
            f"{proc.stdout.strip()} {proc.stderr.strip()}".strip()
        )


def generate_release_notes(
    repo_root: Path,
    *,
    version: str,
    commit_sha: str,
    published_utc: str,
    installer_name: str,
    installer_sha256: str,
    symbols_zip_name: str,
    previous_tag: str | None = None,
) -> str:
    """Generate markdown release notes from first-parent commit subjects since previous_tag."""
    resolved_prev = previous_tag
    if resolved_prev is None:
        try:
            resolved_prev = compute_release_plan(repo_root).previous_tag
        except Exception:
            resolved_prev = ""

    if resolved_prev:
        git_cmd = ["git", "log", "--first-parent", "--pretty=format:- %s (%h)", f"{resolved_prev}..HEAD"]
    else:
        git_cmd = ["git", "log", "--first-parent", "--pretty=format:- %s (%h)", "-n", "50", "HEAD"]

    log_proc = _run_quiet(git_cmd, cwd=repo_root, timeout=15.0)
    subjects: list[str] = []
    if log_proc.returncode == 0:
        subjects = [line.strip() for line in log_proc.stdout.splitlines() if line.strip()]
    if not subjects:
        subjects = ["- Initial release."]

    compare_line = (
        f"- 对比基线：`{resolved_prev}..HEAD`\n" if resolved_prev else "- 对比基线：首个正式版构建\n"
    )
    body = (
        f"# SPAWN v{version}\n\n"
        f"- 通道：`release`（Windows x64 自包含安装器，无需预装 .NET 10 桌面运行时）\n"
        f"- 提交：`{commit_sha}`\n"
        f"- 构建时间：`{published_utc}`\n"
        f"{compare_line}\n"
        f"## 变更摘要\n\n"
        + "\n".join(subjects)
        + "\n\n"
        f"## 发布资产与校验\n\n"
        f"- 安装包：`{installer_name}`（SHA-256: `{installer_sha256}`）\n"
        f"- 调试符号：`{symbols_zip_name}`\n"
        f"- 更新清单：`update-windows-x64.json` + ECDSA P-256 DER 签名 `update-windows-x64.json.sig`\n"
        f"- 完整性校验：`SHA256SUMS`（`sha256sum -c SHA256SUMS`）\n"
    )
    return body


def write_and_verify_sha256sums(release_dir: Path, filenames: list[str]) -> Path:
    lines: list[str] = []
    for name in filenames:
        if "/" in name or "\\" in name or name in {".", ".."}:
            raise ValueError(f"SHA256SUMS requires bare filenames, got {name!r}")
        fpath = release_dir / name
        if not fpath.is_file():
            raise FileNotFoundError(f"Missing release artifact for SHA256SUMS: {fpath}")
        lines.append(f"{sha256_file(fpath)}  {name}")

    sums_path = release_dir / "SHA256SUMS"
    sums_path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    verify_sha256sums(sums_path)
    return sums_path


def verify_sha256sums(sums_path: Path) -> dict[str, str]:
    """Parse and verify a GNU sha256sum-compatible SHA256SUMS file."""
    if not sums_path.is_file():
        raise FileNotFoundError(f"Missing SHA256SUMS file: {sums_path}")
    verified: dict[str, str] = {}
    for line_no, raw_line in enumerate(sums_path.read_text(encoding="utf-8").splitlines(), start=1):
        line = raw_line.strip()
        if not line:
            continue
        m = _SHA256SUM_LINE_RE.fullmatch(line)
        if not m:
            raise ValueError(f"Malformed SHA256SUMS line {line_no}: {raw_line!r}")
        expected_hex, filename = m.group(1), m.group(2)
        target = sums_path.parent / filename
        if not target.is_file():
            raise FileNotFoundError(f"SHA256SUMS target missing: {filename}")
        actual_hex = sha256_file(target)
        if actual_hex != expected_hex:
            raise RuntimeError(
                f"SHA256SUMS mismatch for {filename}: expected {expected_hex}, got {actual_hex}"
            )
        verified[filename] = actual_hex
    if not verified:
        raise ValueError(f"SHA256SUMS is empty: {sums_path}")
    return verified


def parse_version_txt(version_txt_path: Path) -> dict[str, str]:
    result: dict[str, str] = {}
    for raw_line in version_txt_path.read_text(encoding="utf-8").splitlines():
        if "=" in raw_line:
            k, v = raw_line.split("=", 1)
            result[k.strip()] = v.strip()
    return result


def assemble_release_from_package(
    repo_root: Path,
    *,
    version: str,
    package_dir: Path,
    output_dir: Path,
    public_key_path: Path,
    repo_url: str = DEFAULT_REPO_URL,
    published_utc: str | None = None,
    previous_tag: str | None = None,
    env: dict[str, str] | None = None,
) -> Path:
    """Assemble, sign, verify, and atomically publish dist/spawn/ from a staged package_dir."""
    parse_semver_triple(version)
    now_utc = datetime.now(UTC)
    resolved_published_utc = published_utc or now_utc.strftime("%Y-%m-%dT%H:%M:%SZ")

    installer_name = f"spawn-setup-{version}-x64.exe"
    symbols_zip_name = f"spawn-symbols-{version}-x64.zip"

    required_from_pkg = (
        installer_name,
        symbols_zip_name,
        "VERSION.txt",
        "THIRD-PARTY-NOTICES.txt",
    )
    for fname in required_from_pkg:
        if not (package_dir / fname).is_file():
            raise FileNotFoundError(f"Missing expected packaged file {fname} in {package_dir}")

    version_meta = parse_version_txt(package_dir / "VERSION.txt")
    if version_meta.get("channel") != "release":
        raise RuntimeError(f"VERSION.txt channel must be 'release', got {version_meta.get('channel')!r}")
    if version_meta.get("version") != version:
        raise RuntimeError(
            f"VERSION.txt version mismatch: expected {version!r}, got {version_meta.get('version')!r}"
        )
    commit_sha = version_meta.get("commit", "unknown")

    output_dir = output_dir.resolve()
    parent_dir = output_dir.parent
    parent_dir.mkdir(parents=True, exist_ok=True)
    stage_final = parent_dir / f".release-final-{version}-{now_utc.strftime('%Y%m%d%H%M%S')}-{os.getpid()}"
    if stage_final.exists():
        shutil.rmtree(stage_final, ignore_errors=True)
    stage_final.mkdir(parents=True, exist_ok=True)

    try:
        for fname in required_from_pkg:
            shutil.copy2(package_dir / fname, stage_final / fname)

        installer_path = stage_final / installer_name
        installer_size = installer_path.stat().st_size
        installer_sha256 = sha256_file(installer_path)

        manifest_obj = build_update_manifest(
            version=version,
            installer_name=installer_name,
            installer_size=installer_size,
            installer_sha256=installer_sha256,
            published_utc=resolved_published_utc,
            repo_url=repo_url,
        )
        manifest_path = stage_final / "update-windows-x64.json"
        manifest_path.write_text(
            json.dumps(manifest_obj, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
            newline="\n",
        )

        sig_path = stage_final / "update-windows-x64.json.sig"
        sign_manifest_file(repo_root, manifest_path, sig_path, env=env)
        verify_manifest_signature(repo_root, manifest_path, sig_path, public_key_path)

        notes_text = generate_release_notes(
            repo_root,
            version=version,
            commit_sha=commit_sha,
            published_utc=resolved_published_utc,
            installer_name=installer_name,
            installer_sha256=installer_sha256,
            symbols_zip_name=symbols_zip_name,
            previous_tag=previous_tag,
        )
        (stage_final / "notes.md").write_text(notes_text, encoding="utf-8", newline="\n")

        download_assets = expected_release_download_assets(version)
        write_and_verify_sha256sums(stage_final, download_assets)

        actual_files = sorted(p.name for p in stage_final.iterdir())
        expected_files = sorted(expected_release_dist_files(version))
        if actual_files != expected_files:
            raise RuntimeError(
                f"Release staging file mismatch: expected {expected_files}, got {actual_files}"
            )

        backup_dir = parent_dir / f".release-old-{version}-{now_utc.strftime('%Y%m%d%H%M%S')}-{os.getpid()}"
        if output_dir.exists():
            output_dir.rename(backup_dir)
        stage_final.rename(output_dir)
        if backup_dir.exists():
            shutil.rmtree(backup_dir, ignore_errors=True)

        return output_dir
    except Exception:
        if stage_final.exists():
            shutil.rmtree(stage_final, ignore_errors=True)
        raise


def build_release(
    repo_root: Path,
    *,
    version: str,
    public_key_path: Path | None = None,
    output_dir: Path | None = None,
    repo_url: str = DEFAULT_REPO_URL,
) -> Path:
    """Build self-contained release installer + symbols + signed manifest atomically into dist/spawn/."""
    parse_semver_triple(version)

    # Validate signing key presence before spending time on dotnet publish + Inno Setup.
    key_path_str = os.environ.get(SIGNING_KEY_ENV_VAR, "").strip()
    if not key_path_str:
        raise RuntimeError(f"Environment variable {SIGNING_KEY_ENV_VAR} must be set before --build.")
    if not Path(key_path_str).is_file():
        raise RuntimeError(f"Signing key referenced by {SIGNING_KEY_ENV_VAR} does not exist.")

    resolved_pub_key = (
        public_key_path.resolve()
        if public_key_path is not None
        else (repo_root / "installer" / "update-signing-public.pem")
    )
    if not resolved_pub_key.is_file():
        raise FileNotFoundError(f"Public signing key not found: {resolved_pub_key}")

    resolved_output = output_dir.resolve() if output_dir is not None else (repo_root / "dist" / "spawn")
    resolved_output.parent.mkdir(parents=True, exist_ok=True)

    scripts_dir = str(Path(__file__).resolve().parent)
    if scripts_dir not in sys.path:
        sys.path.insert(0, scripts_dir)
    import package as pkg

    now_utc = datetime.now(UTC)
    work_stage = resolved_output.parent / f".release-{version}-{now_utc.strftime('%Y%m%d%H%M%S')}-{os.getpid()}"
    pkg_stage_out = work_stage / "pkg"
    work_stage.mkdir(parents=True, exist_ok=True)

    try:
        profile = pkg.IDENTITY_PROFILES["spawn"]
        pkg_dir, _ = pkg.build_package(
            repo_root,
            profile,
            version,
            create_shortcuts=True,
            self_contained=True,
            output_dir=pkg_stage_out,
        )

        # Double-check zero *.pdb in staged app before assembling release directory
        staged_app = pkg_dir / "app"
        leaked_pdbs = sorted(p.relative_to(staged_app).as_posix() for p in staged_app.rglob("*.pdb") if p.is_file())
        if leaked_pdbs:
            raise RuntimeError(f"Release package app/ still contains *.pdb files: {leaked_pdbs}")

        final_dir = assemble_release_from_package(
            repo_root,
            version=version,
            package_dir=pkg_dir,
            output_dir=resolved_output,
            public_key_path=resolved_pub_key,
            repo_url=repo_url,
            published_utc=now_utc.strftime("%Y-%m-%dT%H:%M:%SZ"),
        )
        installer_bytes = (final_dir / f"spawn-setup-{version}-x64.exe").stat().st_size
        symbols_bytes = (final_dir / f"spawn-symbols-{version}-x64.zip").stat().st_size
        print(
            f"[release] ready -> {final_dir} "
            f"(installer={installer_bytes} bytes / {installer_bytes / (1024 * 1024):.2f} MiB, "
            f"symbols={symbols_bytes} bytes / {symbols_bytes / 1024:.1f} KiB)"
        )
        return final_dir
    finally:
        if work_stage.exists():
            shutil.rmtree(work_stage, ignore_errors=True)


def run_isolated_install_drill(
    repo_root: Path,
    *,
    version: str | None = None,
) -> dict[str, object]:
    """Build, silently install, verify, and silently uninstall isolated `spawn-test` in a temp dir.

    Runs silently with BELOW_NORMAL_PRIORITY_CLASS | CREATE_NO_WINDOW and temp SPAWN_DATA_DIR/VAULT_DIR,
    even when CI=true or SPAWN_DEV_AUTO_INSTALL=0 is set. Never touches real `spawn` or `spawn-dev`.
    """
    if os.name != "nt":
        raise RuntimeError("Isolated spawn-test install drill requires Windows.")

    resolved_version = version or compute_release_plan(repo_root).version
    parse_semver_triple(resolved_version)

    scripts_dir = str(Path(__file__).resolve().parent)
    if scripts_dir not in sys.path:
        sys.path.insert(0, scripts_dir)
    import package as pkg

    profile = pkg.IDENTITY_PROFILES["spawn-test"]
    saved_env_keys = (
        "SPAWN_DATA_DIR",
        "SPAWN_VAULT_DIR",
        "SPAWN_HEADLESS_TEST",
        "SPAWN_TEST_HEADLESS",
        "SPAWN_MUTE_AUDIO",
    )
    saved_env = {k: os.environ.get(k) for k in saved_env_keys}

    with tempfile.TemporaryDirectory(prefix="spawn-release-drill-") as tmp_str:
        tmp_root = Path(tmp_str)
        install_dir = tmp_root / "Programs" / "spawn-test"
        data_dir = tmp_root / "data"
        vault_dir = tmp_root / "vault"
        pkg_out_dir = tmp_root / "dist-spawn-test"
        receipt_path = tmp_root / "install-spawn-test.json"
        data_dir.mkdir(parents=True, exist_ok=True)
        vault_dir.mkdir(parents=True, exist_ok=True)

        os.environ["SPAWN_DATA_DIR"] = str(data_dir)
        os.environ["SPAWN_VAULT_DIR"] = str(vault_dir)
        os.environ["SPAWN_HEADLESS_TEST"] = "1"
        os.environ["SPAWN_TEST_HEADLESS"] = "1"
        os.environ["SPAWN_MUTE_AUDIO"] = "1"

        try:
            print(f"[drill] building self-contained spawn-test {resolved_version}...")
            pkg_dir, setup_exe = pkg.build_package(
                repo_root,
                profile,
                resolved_version,
                create_shortcuts=False,
                self_contained=True,
                output_dir=pkg_out_dir,
            )

            symbols_zip = pkg_dir / f"spawn-test-symbols-{resolved_version}-x64.zip"
            if not symbols_zip.is_file():
                raise RuntimeError(f"Missing symbols zip in spawn-test drill: {symbols_zip}")
            with zipfile.ZipFile(symbols_zip, "r") as zf:
                zip_names = zf.namelist()
            if not any(name.endswith("Spawn.TrainerWorker.pdb") for name in zip_names):
                raise RuntimeError(f"Symbols zip missing Spawn.TrainerWorker.pdb: {zip_names}")
            if any("libskiasharp.pdb" in name.lower() for name in zip_names):
                raise RuntimeError(f"Symbols zip must not contain native libSkiaSharp.pdb: {zip_names}")

            print(f"[drill] silently installing spawn-test -> {install_dir}...")
            receipt = pkg.install_package(
                repo_root,
                profile,
                setup_exe,
                resolved_version,
                install_dir_override=install_dir,
                relaunch_background=False,
                receipt_path=receipt_path,
                is_dev_auto_install=False,
            )
            if not receipt.get("installed"):
                raise RuntimeError(f"spawn-test install drill did not install: {receipt}")

            entry = pkg.lookup_uninstall_entry(profile.app_guid)
            if not entry or entry.get("DisplayVersion") != resolved_version:
                raise RuntimeError(
                    f"HKCU Uninstall entry version mismatch for spawn-test: {entry}"
                )

            installed_exe = install_dir / "spawn-test.exe"
            installed_overlay = install_dir / "Spawn.OverlayHost.exe"
            installed_trainer = install_dir / "Workers" / "Trainer" / "Spawn.TrainerWorker.exe"
            installed_version_txt = install_dir / "VERSION.txt"
            installed_notices = install_dir / "THIRD-PARTY-NOTICES.txt"
            for required in (
                installed_exe,
                installed_overlay,
                installed_trainer,
                installed_version_txt,
                installed_notices,
                install_dir / "coreclr.dll",
                install_dir / "Workers" / "Trainer" / "coreclr.dll",
            ):
                if not required.is_file():
                    raise RuntimeError(f"Required installed release file missing: {required}")

            installed_ver_map = parse_version_txt(installed_version_txt)
            if installed_ver_map.get("channel") != "release" or installed_ver_map.get("version") != resolved_version:
                raise RuntimeError(f"Installed VERSION.txt mismatch: {installed_ver_map}")

            installed_pdbs = sorted(
                p.relative_to(install_dir).as_posix() for p in install_dir.rglob("*.pdb") if p.is_file()
            )
            if installed_pdbs:
                raise RuntimeError(f"Installed release directory contains *.pdb files: {installed_pdbs}")

            leaked_root_worker = sorted(p.name for p in install_dir.glob("Spawn.TrainerWorker*"))
            if leaked_root_worker:
                raise RuntimeError(f"Installed release root leaked TrainerWorker files: {leaked_root_worker}")

            marker = data_dir / "db" / "drill-preserve.marker"
            marker.parent.mkdir(parents=True, exist_ok=True)
            marker.write_text("preserve-user-data\n", encoding="utf-8", newline="\n")

            print("[drill] silently uninstalling spawn-test...")
            pkg.uninstall_identity(repo_root, profile, allow_release=False)

            if pkg.lookup_uninstall_entry(profile.app_guid) is not None:
                raise RuntimeError("HKCU Uninstall entry still present after spawn-test silent uninstall.")
            if not marker.is_file():
                raise RuntimeError("Silent uninstall deleted isolated user data marker!")

            result: dict[str, object] = {
                "ok": True,
                "identity": profile.identity,
                "version": resolved_version,
                "installerBytes": setup_exe.stat().st_size,
                "symbolsZipBytes": symbols_zip.stat().st_size,
                "symbolsCount": len(zip_names),
                "pdbCountInstalled": 0,
                "selfContainedVerified": True,
                "workerIsolatedVerified": True,
                "uninstalledCleanly": True,
                "userDataPreserved": True,
            }
            print("[drill] PASS " + json.dumps(result, ensure_ascii=False))
            return result
        finally:
            try:
                if pkg.lookup_uninstall_entry(profile.app_guid) is not None:
                    pkg.uninstall_identity(repo_root, profile, allow_release=False)
            except Exception:
                pass
            for k, old_val in saved_env.items():
                if old_val is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = old_val


def main() -> int:
    _configure_windows_quiet_mode()
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--plan", action="store_true", help="Compute next release version and release=true|false.")
    mode.add_argument("--build", action="store_true", help="Build and sign release artifacts into dist/spawn/.")
    mode.add_argument(
        "--drill-install",
        action="store_true",
        help="Run isolated temp-directory spawn-test release install & uninstall verification drill.",
    )
    parser.add_argument("--version", default=None, help="Target Major.Minor.Patch version (required for --build).")
    parser.add_argument(
        "--public-key",
        type=Path,
        default=None,
        help="Optional public key PEM override for verifying signed manifest (default: installer/update-signing-public.pem).",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=None,
        help="Optional release output directory override (default: dist/spawn).",
    )
    parser.add_argument(
        "--repo-url",
        default=DEFAULT_REPO_URL,
        help=f"GitHub repository URL prefix (default: {DEFAULT_REPO_URL}).",
    )
    parser.add_argument("--root", type=Path, default=None, help="Optional repository root override.")
    args = parser.parse_args()

    repo_root = args.root.resolve() if args.root is not None else Path(__file__).resolve().parents[1]

    if args.plan:
        plan = compute_release_plan(repo_root)
        emit_plan_output(plan)
        return 0

    if args.build:
        if not args.version:
            parser.error("--build requires --version <Major.Minor.Patch>")
        build_release(
            repo_root,
            version=args.version,
            public_key_path=args.public_key,
            output_dir=args.output_dir,
            repo_url=args.repo_url,
        )
        return 0

    if args.drill_install:
        run_isolated_install_drill(repo_root, version=args.version)
        return 0

    return 2


if __name__ == "__main__":
    raise SystemExit(main())
