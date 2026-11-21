"""Offline stdlib-only unittest suite for M3-W10 release planning, signing, manifest & PDB stripping.

Covers M3-A-19 and M3-A-20 with ephemeral test keys ONLY (zero real private keys, zero network,
zero foreground popups/focus steal).
"""

from __future__ import annotations

import ctypes
from datetime import UTC, datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile

REPO_ROOT = Path(__file__).resolve().parents[1]
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import package as pkg  # noqa: E402
import release as rel  # noqa: E402

BELOW_NORMAL_PRIORITY_CLASS = 0x00004000
CREATE_NO_WINDOW = 0x08000000
CREATION_FLAGS = (BELOW_NORMAL_PRIORITY_CLASS | CREATE_NO_WINDOW) if os.name == "nt" else 0


def _configure_quiet_test_host() -> None:
    if os.name != "nt":
        return
    try:
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.SetErrorMode(0x0001 | 0x0002 | 0x8000)
        k32.SetPriorityClass(k32.GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS)
    except OSError:
        pass


def _run_silent(
    cmd: list[str],
    *,
    cwd: Path,
    env: dict[str, str] | None = None,
    timeout: float = 120.0,
) -> subprocess.CompletedProcess[str]:
    merged_env = os.environ.copy() if env is None else dict(env)
    merged_env["PYTHONDONTWRITEBYTECODE"] = "1"
    merged_env["PYTHONUTF8"] = "1"
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
        creationflags=CREATION_FLAGS,
        timeout=timeout,
        check=False,
    )


def _find_openssl() -> str | None:
    found = shutil.which("openssl")
    if found:
        return found
    if os.name == "nt":
        for candidate in (
            r"C:\Program Files\Git\usr\bin\openssl.exe",
            r"C:\Program Files\Git\mingw64\bin\openssl.exe",
        ):
            if Path(candidate).is_file():
                return candidate
    return None


def _init_temp_git_repo(repo_dir: Path, base_version: str = "0.1.1") -> None:
    scripts = repo_dir / "scripts"
    scripts.mkdir(parents=True, exist_ok=True)
    (scripts / "package.py").write_text(
        f'NEXT_BASE_VERSION = "{base_version}"\n',
        encoding="utf-8",
        newline="\n",
    )
    for git_cmd in (
        ["git", "init", "-b", "main"],
        ["git", "config", "user.name", "SPAWN Test"],
        ["git", "config", "user.email", "spawn-test@example.invalid"],
        ["git", "config", "commit.gpgsign", "false"],
    ):
        proc = _run_silent(git_cmd, cwd=repo_dir, timeout=20.0)
        if proc.returncode != 0:
            raise AssertionError(f"{git_cmd} failed: {proc.stdout}\n{proc.stderr}")


def _git_commit(repo_dir: Path, rel_file: str, content: str, message: str) -> str:
    fpath = repo_dir / rel_file
    fpath.parent.mkdir(parents=True, exist_ok=True)
    fpath.write_text(content, encoding="utf-8", newline="\n")
    for git_cmd in (
        ["git", "add", rel_file],
        ["git", "commit", "-m", message],
    ):
        proc = _run_silent(git_cmd, cwd=repo_dir, timeout=20.0)
        if proc.returncode != 0:
            raise AssertionError(f"{git_cmd} failed: {proc.stdout}\n{proc.stderr}")
    rev = _run_silent(["git", "rev-parse", "--short=12", "HEAD"], cwd=repo_dir, timeout=20.0)
    assert rev.returncode == 0
    return rev.stdout.strip()


def _git_tag(repo_dir: Path, tag_name: str) -> None:
    proc = _run_silent(["git", "tag", tag_name], cwd=repo_dir, timeout=20.0)
    if proc.returncode != 0:
        raise AssertionError(f"git tag {tag_name} failed: {proc.stdout}\n{proc.stderr}")


class ReleasePlanAndVersionTests(unittest.TestCase):
    def test_plan_first_release_without_tags_uses_next_base_version(self) -> None:
        with tempfile.TemporaryDirectory(prefix="spawn-plan-first-") as td:
            repo = Path(td)
            _init_temp_git_repo(repo, base_version="0.1.1")
            _git_commit(repo, "README.md", "initial\n", "feat: initial")

            plan = rel.compute_release_plan(repo)
            self.assertTrue(plan.release)
            self.assertEqual(plan.version, "0.1.1")
            self.assertEqual(plan.previous_tag, "")

    def test_plan_zero_commits_since_tag_returns_release_false(self) -> None:
        with tempfile.TemporaryDirectory(prefix="spawn-plan-zero-") as td:
            repo = Path(td)
            _init_temp_git_repo(repo, base_version="0.1.1")
            _git_commit(repo, "a.txt", "1\n", "feat: release one")
            _git_tag(repo, "v0.1.1")

            plan = rel.compute_release_plan(repo)
            self.assertFalse(plan.release)
            self.assertEqual(plan.version, "0.1.2")
            self.assertEqual(plan.previous_tag, "v0.1.1")
            self.assertEqual(plan.commit_count, 0)

    def test_plan_out_of_order_integer_patches_v0_1_9_and_v0_1_10(self) -> None:
        with tempfile.TemporaryDirectory(prefix="spawn-plan-patch-") as td:
            repo = Path(td)
            _init_temp_git_repo(repo, base_version="0.1.1")
            _git_commit(repo, "a.txt", "1\n", "feat: patch 2")
            _git_tag(repo, "v0.1.2")
            _git_tag(repo, "v0.2.50")  # Different minor series: must be ignored
            _git_tag(repo, "v0.1.99-rc.1")  # Pre-release tag: must be ignored
            _git_commit(repo, "a.txt", "2\n", "feat: patch 9")
            _git_tag(repo, "v0.1.9")
            _git_commit(repo, "a.txt", "3\n", "feat: patch 10")
            _git_tag(repo, "v0.1.10")

            # At v0.1.10 with zero new commits -> release=false, next=0.1.11
            plan_zero = rel.compute_release_plan(repo)
            self.assertFalse(plan_zero.release)
            self.assertEqual(plan_zero.previous_tag, "v0.1.10")
            self.assertEqual(plan_zero.version, "0.1.11")

            # Add one new commit -> release=true, next=0.1.11
            _git_commit(repo, "a.txt", "4\n", "feat: next change after 0.1.10")
            plan_next = rel.compute_release_plan(repo)
            self.assertTrue(plan_next.release)
            self.assertEqual(plan_next.previous_tag, "v0.1.10")
            self.assertEqual(plan_next.version, "0.1.11")
            self.assertEqual(plan_next.commit_count, 1)

            # Verify CLI --plan writes both stdout and GITHUB_OUTPUT
            gh_output = repo / "github-output.txt"
            env = os.environ.copy()
            env["GITHUB_OUTPUT"] = str(gh_output)
            proc = _run_silent(
                [sys.executable, "-B", str(SCRIPTS_DIR / "release.py"), "--plan", "--root", str(repo)],
                cwd=REPO_ROOT,
                env=env,
            )
            self.assertEqual(proc.returncode, 0, f"{proc.stdout}\n{proc.stderr}")
            expected = "release=true\nversion=0.1.11\nprevious_tag=v0.1.10\n"
            self.assertEqual(proc.stdout.replace("\r\n", "\n"), expected)
            self.assertEqual(gh_output.read_text(encoding="utf-8"), expected)

    def test_dev_version_format_reuses_next_release_version_and_falls_back(self) -> None:
        fixed_now = datetime(2026, 11, 22, 14, 35, tzinfo=UTC)
        with tempfile.TemporaryDirectory(prefix="spawn-dev-ver-") as td:
            repo = Path(td)
            _init_temp_git_repo(repo, base_version="0.1.1")
            _git_commit(repo, "a.txt", "1\n", "feat: initial")
            self.assertEqual(
                pkg.compute_dev_version(repo, now_utc=fixed_now, base_version="0.1.1"),
                "0.1.1-dev.202611221435",
            )
            _git_tag(repo, "v0.1.9")
            _git_tag(repo, "v0.1.10")
            self.assertEqual(
                pkg.compute_dev_version(repo, now_utc=fixed_now, base_version="0.1.1"),
                "0.1.11-dev.202611221435",
            )

        with tempfile.TemporaryDirectory(prefix="spawn-dev-nogit-") as non_git_td:
            self.assertEqual(
                pkg.compute_dev_version(Path(non_git_td), now_utc=fixed_now, base_version="0.1.1"),
                "0.1.1-dev.202611221435",
            )

    def test_install_dev_honors_skip_guards(self) -> None:
        for env_key, env_val in (("SPAWN_DEV_AUTO_INSTALL", "0"), ("CI", "true")):
            with self.subTest(guard=f"{env_key}={env_val}"):
                with tempfile.TemporaryDirectory(prefix="spawn-skip-guard-") as td:
                    receipt = Path(td) / "install-dev.json"
                    env = os.environ.copy()
                    env.pop("SPAWN_DEV_AUTO_INSTALL", None)
                    env.pop("CI", None)
                    env[env_key] = env_val
                    proc = _run_silent(
                        [
                            sys.executable,
                            "-B",
                            str(SCRIPTS_DIR / "package.py"),
                            "--install-dev",
                            "--receipt-path",
                            str(receipt),
                        ],
                        cwd=REPO_ROOT,
                        env=env,
                        timeout=30.0,
                    )
                    self.assertEqual(proc.returncode, 0, f"{proc.stdout}\n{proc.stderr}")
                    self.assertTrue(receipt.is_file())
                    data = json.loads(receipt.read_text(encoding="utf-8"))
                    self.assertFalse(data["installed"])
                    self.assertTrue(data["skipped"])
                    self.assertIn(env_key, data["reason"])


class PdbStrippingAndArchivingTests(unittest.TestCase):
    def test_native_pdb_stripped_in_dev_while_managed_pdbs_kept(self) -> None:
        with tempfile.TemporaryDirectory(prefix="spawn-pdb-dev-") as td:
            stage_app = Path(td) / "app"
            worker_dir = stage_app / "Workers" / "Trainer"
            worker_dir.mkdir(parents=True, exist_ok=True)

            native_root = stage_app / "libSkiaSharp.pdb"
            native_worker = worker_dir / "nativeHelper.pdb"
            managed_core = stage_app / "Spawn.Core.pdb"
            managed_host = stage_app / "spawn-dev.pdb"
            managed_worker = worker_dir / "Spawn.TrainerWorker.pdb"

            native_root.write_bytes(b"Microsoft C/C++ MSF 7.00\r\n\x1aDS\x00\x00\x00fake-native")
            native_worker.write_bytes(b"Microsoft C/C++ MSF 7.00\r\n\x1aDS\x00\x00\x00fake-worker-native")
            managed_core.write_bytes(b"BSJB\x01\x00\x01\x00fake-managed-core")
            managed_host.write_bytes(b"BSJB\x01\x00\x01\x00fake-managed-host")
            managed_worker.write_bytes(b"BSJB\x01\x00\x01\x00fake-managed-worker")

            self.assertTrue(pkg.is_native_pdb(native_root))
            self.assertTrue(pkg.is_native_pdb(native_worker))
            self.assertFalse(pkg.is_native_pdb(managed_core))
            self.assertFalse(pkg.is_native_pdb(managed_host))
            self.assertFalse(pkg.is_native_pdb(managed_worker))

            removed = pkg.strip_native_pdbs(stage_app)
            self.assertEqual(removed, ["Workers/Trainer/nativeHelper.pdb", "libSkiaSharp.pdb"])
            self.assertFalse(native_root.exists())
            self.assertFalse(native_worker.exists())
            self.assertTrue(managed_core.is_file())
            self.assertTrue(managed_host.is_file())
            self.assertTrue(managed_worker.is_file())

    def test_release_strips_all_pdbs_recursively_and_archives_symbols_zip(self) -> None:
        with tempfile.TemporaryDirectory(prefix="spawn-pdb-rel-") as td:
            root = Path(td)
            stage_app = root / "app"
            worker_dir = stage_app / "Workers" / "Trainer"
            worker_dir.mkdir(parents=True, exist_ok=True)

            (stage_app / "spawn.exe").write_bytes(b"MZ-fake-host")
            (stage_app / "libSkiaSharp.pdb").write_bytes(b"Microsoft C/C++ MSF 7.00\r\n\x1aDS\x00\x00\x00")
            (stage_app / "spawn.pdb").write_bytes(b"BSJB\x01\x00\x01\x00host")
            (stage_app / "Spawn.Core.pdb").write_bytes(b"BSJB\x01\x00\x01\x00core")
            (worker_dir / "Spawn.TrainerWorker.exe").write_bytes(b"MZ-fake-worker")
            (worker_dir / "Spawn.TrainerWorker.pdb").write_bytes(b"BSJB\x01\x00\x01\x00worker")

            pkg.strip_native_pdbs(stage_app)
            symbols_zip = root / "spawn-symbols-0.1.1-x64.zip"
            archived = pkg.strip_and_archive_release_pdbs(stage_app, symbols_zip)

            self.assertEqual(
                archived,
                ["Spawn.Core.pdb", "Workers/Trainer/Spawn.TrainerWorker.pdb", "spawn.pdb"],
            )
            self.assertEqual(list(stage_app.rglob("*.pdb")), [])
            self.assertTrue((stage_app / "spawn.exe").is_file())
            self.assertTrue((worker_dir / "Spawn.TrainerWorker.exe").is_file())
            with zipfile.ZipFile(symbols_zip, "r") as zf:
                self.assertEqual(
                    sorted(zf.namelist()),
                    ["Spawn.Core.pdb", "Workers/Trainer/Spawn.TrainerWorker.pdb", "spawn.pdb"],
                )


class ManifestSigningAndChecksumsTests(unittest.TestCase):
    def test_manifest_fields_and_url_prefix_validation(self) -> None:
        sha = "a" * 64
        manifest = rel.build_update_manifest(
            version="0.1.5",
            installer_name="spawn-setup-0.1.5-x64.exe",
            installer_size=12345678,
            installer_sha256=sha,
            published_utc="2026-11-22T14:00:00Z",
        )
        self.assertEqual(manifest["schemaVersion"], 1)
        self.assertEqual(manifest["channel"], "release")
        self.assertEqual(manifest["version"], "0.1.5")
        self.assertEqual(manifest["publishedUtc"], "2026-11-22T14:00:00Z")
        self.assertEqual(manifest["notesUrl"], "https://github.com/chialecode/spawn/releases/tag/v0.1.5")
        inst = manifest["installer"]
        assert isinstance(inst, dict)
        self.assertEqual(inst["name"], "spawn-setup-0.1.5-x64.exe")
        self.assertEqual(
            inst["url"],
            "https://github.com/chialecode/spawn/releases/download/v0.1.5/spawn-setup-0.1.5-x64.exe",
        )
        self.assertEqual(inst["size"], 12345678)
        self.assertEqual(inst["sha256"], sha)

        with self.assertRaises(ValueError):
            rel.build_update_manifest(
                version="0.1.5",
                installer_name="wrong.exe",
                installer_size=100,
                installer_sha256=sha,
                published_utc="2026-11-22T14:00:00Z",
            )
        with self.assertRaises(ValueError):
            rel.build_update_manifest(
                version="0.1.5-dev.1",
                installer_name="spawn-setup-0.1.5-dev.1-x64.exe",
                installer_size=100,
                installer_sha256=sha,
                published_utc="2026-11-22T14:00:00Z",
            )

    def test_end_to_end_ephemeral_signing_dual_verify_tamper_and_zero_secret_leak(self) -> None:
        sign_cs = REPO_ROOT / "scripts" / "sign_update.cs"
        self.assertTrue(sign_cs.is_file())

        with tempfile.TemporaryDirectory(prefix="spawn-sign-e2e-") as td:
            root = Path(td)
            secret_sentinel = "EPHEMERAL_PRIVATE_KEY_SENTINEL_847291.pem"
            priv_key_1 = root / "secrets" / secret_sentinel
            pub_key_1 = root / "pub1.pem"
            priv_key_2 = root / "secrets" / "other-key.pem"
            pub_key_2 = root / "pub2.pem"

            for priv, pub in ((priv_key_1, pub_key_1), (priv_key_2, pub_key_2)):
                kg = _run_silent(
                    ["dotnet", "run", str(sign_cs), "--", "keygen", str(priv), str(pub)],
                    cwd=REPO_ROOT,
                )
                self.assertEqual(kg.returncode, 0, f"keygen failed:\n{kg.stdout}\n{kg.stderr}")
                self.assertNotIn(secret_sentinel, kg.stdout + kg.stderr)

            # Build synthetic packaged release inputs
            pkg_dir = root / "pkg"
            pkg_dir.mkdir(parents=True, exist_ok=True)
            version = "0.1.1"
            installer_name = f"spawn-setup-{version}-x64.exe"
            symbols_name = f"spawn-symbols-{version}-x64.zip"
            (pkg_dir / installer_name).write_bytes(b"MZ-synthetic-release-installer-payload" * 128)
            with zipfile.ZipFile(pkg_dir / symbols_name, "w", zipfile.ZIP_DEFLATED) as zf:
                zf.writestr("Spawn.Core.pdb", b"BSJB-test")
                zf.writestr("Workers/Trainer/Spawn.TrainerWorker.pdb", b"BSJB-worker")
            (pkg_dir / "VERSION.txt").write_text(
                "product=SPAWN\nidentity=spawn\nchannel=release\nversion=0.1.1\ncommit=abcdef123456\nbuiltUtc=2026-11-22T14:00:00Z\n",
                encoding="utf-8",
                newline="\n",
            )
            (pkg_dir / "THIRD-PARTY-NOTICES.txt").write_text("Apache-2.0 / MIT notices\n", encoding="utf-8", newline="\n")

            out_dir = root / "dist" / "spawn"
            sign_env = os.environ.copy()
            sign_env[rel.SIGNING_KEY_ENV_VAR] = str(priv_key_1)

            assembled = rel.assemble_release_from_package(
                REPO_ROOT,
                version=version,
                package_dir=pkg_dir,
                output_dir=out_dir,
                public_key_path=pub_key_1,
                published_utc="2026-11-22T14:00:00Z",
                previous_tag="",
                env=sign_env,
            )
            self.assertEqual(assembled, out_dir.resolve())

            # 1. Exact 8 output files
            expected_files = sorted(rel.expected_release_dist_files(version))
            self.assertEqual(sorted(p.name for p in out_dir.iterdir()), expected_files)

            manifest_path = out_dir / "update-windows-x64.json"
            sig_path = out_dir / "update-windows-x64.json.sig"
            sums_path = out_dir / "SHA256SUMS"

            manifest_raw = manifest_path.read_bytes()
            self.assertFalse(manifest_raw.startswith(b"\xef\xbb\xbf"))
            self.assertNotIn(b"\r", manifest_raw)
            manifest = json.loads(manifest_raw.decode("utf-8"))
            self.assertEqual(manifest["schemaVersion"], 1)
            self.assertEqual(manifest["channel"], "release")
            self.assertEqual(manifest["version"], version)
            self.assertEqual(
                manifest["installer"]["url"],
                f"https://github.com/chialecode/spawn/releases/download/v{version}/{installer_name}",
            )
            self.assertEqual(manifest["installer"]["size"], (out_dir / installer_name).stat().st_size)
            self.assertEqual(manifest["installer"]["sha256"], hashlib.sha256((out_dir / installer_name).read_bytes()).hexdigest())

            # 2. .NET verification succeeds
            rel.verify_manifest_signature(REPO_ROOT, manifest_path, sig_path, pub_key_1)

            # 3. OpenSSL independent cross-verification succeeds (when openssl is installed)
            openssl = _find_openssl()
            if openssl:
                ossl_ok = _run_silent(
                    [openssl, "dgst", "-sha256", "-verify", str(pub_key_1), "-signature", str(sig_path), str(manifest_path)],
                    cwd=REPO_ROOT,
                )
                self.assertEqual(ossl_ok.returncode, 0, f"openssl verify failed: {ossl_ok.stdout}\n{ossl_ok.stderr}")
                self.assertIn("Verified OK", ossl_ok.stdout)

            # 4. Wrong public key fails (.NET + OpenSSL)
            with self.assertRaises(RuntimeError):
                rel.verify_manifest_signature(REPO_ROOT, manifest_path, sig_path, pub_key_2)
            with self.assertRaises(RuntimeError):
                rel.verify_manifest_signature(
                    REPO_ROOT, manifest_path, sig_path, REPO_ROOT / "installer" / "update-signing-public.pem"
                )
            if openssl:
                ossl_wrong = _run_silent(
                    [openssl, "dgst", "-sha256", "-verify", str(pub_key_2), "-signature", str(sig_path), str(manifest_path)],
                    cwd=REPO_ROOT,
                )
                self.assertNotEqual(ossl_wrong.returncode, 0)

            # 5. Tampering a single byte of manifest fails (.NET + OpenSSL)
            tampered_manifest = root / "tampered-update-windows-x64.json"
            tampered_bytes = bytearray(manifest_raw)
            tampered_bytes[0] ^= 0x01
            tampered_manifest.write_bytes(bytes(tampered_bytes))
            with self.assertRaises(RuntimeError):
                rel.verify_manifest_signature(REPO_ROOT, tampered_manifest, sig_path, pub_key_1)
            if openssl:
                ossl_tampered = _run_silent(
                    [openssl, "dgst", "-sha256", "-verify", str(pub_key_1), "-signature", str(sig_path), str(tampered_manifest)],
                    cwd=REPO_ROOT,
                )
                self.assertNotEqual(ossl_tampered.returncode, 0)

            # 6. SHA256SUMS (LF-only, no BOM, for Linux sha256sum -c) passes intact and fails on tamper
            sums_raw = sums_path.read_bytes()
            self.assertFalse(sums_raw.startswith(b"\xef\xbb\xbf"))
            self.assertNotIn(b"\r", sums_raw)
            ver_raw = (out_dir / "VERSION.txt").read_bytes()
            self.assertFalse(ver_raw.startswith(b"\xef\xbb\xbf"))
            self.assertNotIn(b"\r", ver_raw)
            verified_map = rel.verify_sha256sums(sums_path)
            self.assertEqual(sorted(verified_map.keys()), sorted(rel.expected_release_download_assets(version)))
            (out_dir / "VERSION.txt").write_text("tampered\n", encoding="utf-8")
            with self.assertRaises(RuntimeError):
                rel.verify_sha256sums(sums_path)

            # 7. Zero private key path or private key content in logs, error outputs, or artifacts
            priv_key_lines = [
                ln.strip()
                for ln in priv_key_1.read_text(encoding="utf-8").splitlines()
                if ln.strip() and not ln.startswith("-----")
            ]
            for artifact in out_dir.iterdir():
                if artifact.suffix in {".json", ".txt", ".md"} or artifact.name == "SHA256SUMS":
                    text = artifact.read_text(encoding="utf-8", errors="replace")
                    self.assertNotIn(secret_sentinel, text)
                    self.assertNotIn("PRIVATE KEY", text)
                    for secret_line in priv_key_lines:
                        self.assertNotIn(secret_line, text)

            # Error paths must also NEVER leak SPAWN_UPDATE_SIGNING_KEY path
            missing_secret_path = str(root / "DO_NOT_LEAK_MISSING_KEY_99123.pem")
            bad_env = os.environ.copy()
            bad_env[rel.SIGNING_KEY_ENV_VAR] = missing_secret_path
            bad_proc = _run_silent(
                ["dotnet", "run", str(sign_cs), "--", "sign", str(manifest_path), str(root / "bad.sig")],
                cwd=REPO_ROOT,
                env=bad_env,
            )
            self.assertNotEqual(bad_proc.returncode, 0)
            self.assertNotIn("DO_NOT_LEAK_MISSING_KEY_99123", bad_proc.stdout + bad_proc.stderr)

            corrupt_key_path = root / "DO_NOT_LEAK_CORRUPT_KEY_77312.pem"
            corrupt_key_path.write_text("not-a-valid-pem-key\n", encoding="utf-8")
            corrupt_env = os.environ.copy()
            corrupt_env[rel.SIGNING_KEY_ENV_VAR] = str(corrupt_key_path)
            corrupt_proc = _run_silent(
                ["dotnet", "run", str(sign_cs), "--", "sign", str(manifest_path), str(root / "bad2.sig")],
                cwd=REPO_ROOT,
                env=corrupt_env,
            )
            self.assertNotEqual(corrupt_proc.returncode, 0)
            self.assertNotIn("DO_NOT_LEAK_CORRUPT_KEY_77312", corrupt_proc.stdout + corrupt_proc.stderr)
            self.assertNotIn("not-a-valid-pem-key", corrupt_proc.stdout + corrupt_proc.stderr)

    def test_committed_public_key_and_gitignore_hygiene(self) -> None:
        pub_path = REPO_ROOT / "installer" / "update-signing-public.pem"
        self.assertTrue(pub_path.is_file(), "installer/update-signing-public.pem must exist")
        raw = pub_path.read_bytes()
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"), "Public key PEM must not have UTF-8 BOM")
        self.assertNotIn(b"\r", raw, "Public key PEM must use LF line endings")
        text = raw.decode("utf-8")
        self.assertTrue(text.startswith("-----BEGIN PUBLIC KEY-----\n"))
        self.assertTrue(text.rstrip("\n").endswith("-----END PUBLIC KEY-----"))
        self.assertNotIn("PRIVATE KEY", text)

        # Verify OpenSSL parses the committed public key as prime256v1 / P-256
        openssl = _find_openssl()
        if openssl:
            info = _run_silent([openssl, "pkey", "-pubin", "-in", str(pub_path), "-text", "-noout"], cwd=REPO_ROOT)
            self.assertEqual(info.returncode, 0, info.stderr)
            self.assertIn("prime256v1", info.stdout)

        # Verify .gitignore rules protect .local/ and dist/ while tracking installer/update-signing-public.pem
        gi = (REPO_ROOT / ".gitignore").read_text(encoding="utf-8").splitlines()
        self.assertIn(".local/", gi)
        self.assertIn("dist/", gi)
        self.assertIn("!installer/update-signing-public.pem", gi)

        # Verify check_docs.py allows installer/update-signing-public.pem and blocks other .pem files
        import check_docs as cd  # noqa: E402

        self.assertIn("installer/update-signing-public.pem", cd.ALLOWED_PUBLIC_KEY_PEMS)
        with tempfile.TemporaryDirectory(prefix="spawn-checkdocs-pem-") as td:
            fake_root = Path(td)
            (fake_root / "installer").mkdir(parents=True, exist_ok=True)
            (fake_root / "installer" / "other.pem").write_text(text, encoding="utf-8", newline="\n")
            errs, _ = cd.check_repository(fake_root)
            self.assertTrue(
                any("installer/other.pem" in e and "forbidden" in e for e in errs),
                f"Expected check_docs to block arbitrary .pem file, got: {errs}",
            )

            (fake_root / "installer" / "other.pem").unlink()
            (fake_root / "installer" / "update-signing-public.pem").write_text(
                "-----BEGIN " + "PRIVATE KEY-----\nfake\n-----END " + "PRIVATE KEY-----\n",
                encoding="utf-8",
                newline="\n",
            )
            errs_priv, _ = cd.check_repository(fake_root)
            self.assertTrue(
                any("installer/update-signing-public.pem" in e and "private key" in e.lower() for e in errs_priv),
                f"Expected check_docs to reject private key inside allowed pem, got: {errs_priv}",
            )


if __name__ == "__main__":
    _configure_quiet_test_host()
    unittest.main(verbosity=2)
