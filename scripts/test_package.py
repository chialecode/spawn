"""Offline stdlib-only unittest suite for the NSIS packaging and install helpers in package.py.

Covers the pinned NSIS fetch (hash and path checks), the generated uninstall file list, the
silent install command line, waiting while the window is open, the unified receipt (including
clean PR-branch installs), and the install decisions with fake registry, installer and window
probes. When pinned NSIS 3.13 is cached locally (and not under CI), also compiles and exercises
isolated `spawn-dev-test` / `spawn-test` dummy installers against temporary shortcut directories
without touching the user's real Start Menu, Desktop, `spawn`, or `spawn-dev` installations.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock
import zipfile

REPO_ROOT = Path(__file__).resolve().parents[1]
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import generate_icons  # noqa: E402
import package as pkg  # noqa: E402

UNIFIED_RECEIPT_FIELDS = (
    "identity",
    "installed",
    "skipped",
    "reason",
    "error",
    "firstInstall",
    "oldVersion",
    "newVersion",
    "installLocation",
    "wasRunning",
    "windowOpen",
    "deferred",
    "deferMs",
    "quitMs",
    "relaunched",
    "background",
    "inJob",
    "elapsedMs",
    "at",
)


def _zip_bytes(entries: dict[str, bytes]) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for name, data in entries.items():
            zf.writestr(name, data)
    return buf.getvalue()


class PinnedNsisTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="spawn-nsis-test-")
        self.tmp = Path(self._tmp.name)
        self.target = self.tmp / ".tools" / "nsis-3.13"

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def _fetch(self, data: bytes) -> None:
        with mock.patch.object(pkg, "NSIS_ZIP_SHA256", hashlib.sha256(data).hexdigest()):
            pkg.fetch_pinned_nsis(self.target, download=lambda _url: data)

    def test_pins_the_official_zip(self) -> None:
        self.assertEqual(
            pkg.NSIS_ZIP_URL, "https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip"
        )
        self.assertRegex(pkg.NSIS_ZIP_SHA256, r"^[0-9a-f]{64}$")

    def test_fetch_unpacks_and_is_cached(self) -> None:
        data = _zip_bytes({"nsis-3.13/makensis.exe": b"MZ", "nsis-3.13/Stubs/zlib-x86-unicode": b"s"})
        with mock.patch.object(pkg, "NSIS_ZIP_SHA256", hashlib.sha256(data).hexdigest()):
            found = pkg.find_makensis({}, cache_dir=self.target, download=lambda _url: data)
        self.assertEqual(found, self.target / "makensis.exe")
        self.assertTrue((self.target / "Stubs" / "zlib-x86-unicode").is_file())

        def no_download(_url: str) -> bytes:
            raise AssertionError("a cached NSIS must not be downloaded again")

        self.assertEqual(pkg.find_makensis({}, cache_dir=self.target, download=no_download), found)

    def test_hash_mismatch_is_refused_and_nothing_is_kept(self) -> None:
        data = _zip_bytes({"nsis-3.13/makensis.exe": b"MZ"})
        with self.assertRaisesRegex(RuntimeError, "SHA-256 mismatch"):
            pkg.fetch_pinned_nsis(self.target, download=lambda _url: data)
        self.assertFalse(self.target.exists())

    def test_unsafe_archive_paths_are_refused(self) -> None:
        for name in ("nsis-3.13/../evil.exe", "nsis-3.13/Stubs/C:evil", "other/makensis.exe"):
            with self.subTest(name=name):
                with self.assertRaises(RuntimeError):
                    self._fetch(_zip_bytes({"nsis-3.13/makensis.exe": b"MZ", name: b"x"}))
                self.assertFalse(self.target.exists())
                self.assertEqual([p.name for p in self.target.parent.glob(".*staging*")], [])

    def test_offline_fails_clearly(self) -> None:
        def offline(_url: str) -> bytes:
            raise OSError("no network")

        with self.assertRaisesRegex(RuntimeError, "SPAWN_MAKENSIS_PATH"):
            pkg.find_makensis({}, cache_dir=self.target, download=offline)

    def test_override_and_missing_without_fetch(self) -> None:
        with self.assertRaises(FileNotFoundError):
            pkg.find_makensis({"SPAWN_MAKENSIS_PATH": str(self.tmp / "missing.exe")}, cache_dir=self.target)
        exe = self.tmp / "makensis.exe"
        exe.write_bytes(b"MZ")
        self.assertEqual(pkg.find_makensis({"SPAWN_MAKENSIS_PATH": str(exe)}, cache_dir=self.target), exe)
        with self.assertRaises(FileNotFoundError):
            pkg.find_makensis({}, cache_dir=self.target, fetch=False)


class InstallerScriptTests(unittest.TestCase):
    def test_remove_files_macro_lists_every_file_then_folders_deepest_first(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            app = Path(tmp)
            for rel in ("spawn-dev.exe", "Workers/Trainer/e_sqlite3.dll", "assets/icon.ico", "odd $name.txt"):
                (app / rel).parent.mkdir(parents=True, exist_ok=True)
                (app / rel).write_bytes(b"x")
            text = pkg.render_remove_files_nsh(app)
        self.assertIn("!macro SPAWN_REMOVE_INSTALLED_FILES", text)
        self.assertIn(
            '!insertmacro SPAWN_REMOVE_FILE "Workers\\Trainer\\e_sqlite3.dll" "Workers_Trainer_e_sqlite3.dll"', text
        )
        self.assertIn('!insertmacro SPAWN_REMOVE_FILE "odd $$name.txt" "odd $$name.txt"', text)
        self.assertLess(text.index('RMDir "$INSTDIR\\Workers\\Trainer"'), text.index('RMDir "$INSTDIR\\Workers"'))
        self.assertNotIn("RMDir /r", text)

    def test_empty_app_folder_is_an_error(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(RuntimeError):
                pkg.render_remove_files_nsh(Path(tmp))

    def test_silent_command_keeps_d_last_and_unquoted(self) -> None:
        cmd = pkg.build_silent_install_command(Path("C:\\a b\\setup.exe"), Path("C:\\x y\\spawn-dev"))
        self.assertEqual(cmd, '"C:\\a b\\setup.exe" /S /D=C:\\x y\\spawn-dev')
        self.assertEqual(pkg.build_silent_install_command(Path("setup.exe"), None), "setup.exe /S")

    def test_nsi_script_contract(self) -> None:
        raw = (REPO_ROOT / "installer" / "spawn.nsi").read_bytes()
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(b"\r\n", raw)
        text = raw.decode("utf-8")
        for needle in (
            '!if "${NSIS_VERSION}" != "v3.13"',
            "RequestExecutionLevel user",
            '!define SPAWN_UNINSTALL_KEY "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\${SPAWN_APP_ID}"',
            '!define SPAWN_START_MENU_DIR "$SMPROGRAMS"',
            '!define SPAWN_DESKTOP_DIR "$DESKTOP"',
            "--quit-for-update",
            '"/UPDATE"',
            '"/RELAUNCH"',
            "--after-update",
            '!insertmacro MUI_LANGUAGE "SimpChinese"',
            "!define SPAWN_EXIT_FOLDER_TAKEN 7",
            "!define SPAWN_EXIT_FILES_NOT_WRITTEN 8",
            # An update without /D= stays in the installed folder.
            'InstallDirRegKey HKCU "${SPAWN_UNINSTALL_KEY}" "InstallLocation"',
            "SetCompressor /SOLID zlib",
        ):
            self.assertIn(needle, text)
        self.assertIn(8, pkg.INSTALLER_EXIT_REASONS)
        self.assertNotRegex(text, r"(?im)^\s*SetCompressor\s.*lzma")
        self.assertFalse((REPO_ROOT / "installer" / "spawn.iss").exists())

        # Silent over-installs must NEVER select SecStartMenu (even for dev builds where
        # SPAWN_DESKTOP_ON_UPDATE == 1) and must NEVER delete unselected shortcuts under ${Silent}.
        on_init_match = re.search(r"Function \.onInit(?P<body>.*?)FunctionEnd", text, re.S)
        self.assertIsNotNone(on_init_match)
        on_init = on_init_match.group("body")  # type: ignore[union-attr]
        shortcut_init_match = re.search(
            r"!if \$\{SPAWN_CREATE_SHORTCUTS\} == 1(?P<body>.*?)^!endif",
            on_init,
            re.S | re.M,
        )
        self.assertIsNotNone(shortcut_init_match)
        reinstall_match = re.search(
            r'\$\{If\}\s+\$FirstInstall\s*==\s*"1"(?P<first>.*?)\$\{Else\}(?P<update>.*)\$\{EndIf\}',
            shortcut_init_match.group("body"),  # type: ignore[union-attr]
            re.S,
        )
        self.assertIsNotNone(reinstall_match)
        update_branch = reinstall_match.group("update")  # type: ignore[union-attr]
        self.assertRegex(
            update_branch,
            r'\$\{IfNot\}\s+\$\{Silent\}\s*\n\s*\$\{If\}\s+\$\{FileExists\}\s+"\$\{SPAWN_START_MENU_DIR\}\\\$\{SPAWN_DISPLAY_NAME\}\.lnk"\s*\n\s*!insertmacro\s+SelectSection\s+\$\{SecStartMenu\}\s*\n\s*\$\{EndIf\}\s*\n\s*\$\{EndIf\}',
        )
        desktop_on_update_match = re.search(
            r"!if\s+\$\{SPAWN_DESKTOP_ON_UPDATE\}\s*==\s*1(?P<body>.*?)!endif",
            update_branch,
            re.S,
        )
        self.assertIsNotNone(desktop_on_update_match)
        desktop_on_update = desktop_on_update_match.group("body")  # type: ignore[union-attr]
        self.assertIn("!insertmacro SelectSection ${SecDesktop}", desktop_on_update)
        self.assertNotIn("SecStartMenu", desktop_on_update)

        shortcuts_match = re.search(r'Section "-Shortcuts"(?P<body>.*?)SectionEnd', text, re.S)
        self.assertIsNotNone(shortcuts_match)
        shortcuts_section = shortcuts_match.group("body")  # type: ignore[union-attr]
        self.assertEqual(shortcuts_section.count("${ElseIfNot} ${Silent}"), 2)
        self.assertNotRegex(shortcuts_section, r"(?m)^\s*\$\{Else\}\s*$")
        self.assertIn('Delete "${SPAWN_START_MENU_DIR}\\${SPAWN_DISPLAY_NAME}.lnk"', shortcuts_section)
        self.assertIn('Delete "${SPAWN_DESKTOP_DIR}\\${SPAWN_DISPLAY_NAME}.lnk"', shortcuts_section)

        uninstall_match = re.search(r'Section "Uninstall"(?P<body>.*?)SectionEnd', text, re.S)
        self.assertIsNotNone(uninstall_match)
        uninstall_section = uninstall_match.group("body")  # type: ignore[union-attr]
        self.assertIn('${GetOptions} $0 "/UPDATE" $1', uninstall_section)
        self.assertIn('Push "${SPAWN_START_MENU_DIR}\\${SPAWN_DISPLAY_NAME}.lnk"', uninstall_section)
        self.assertIn('Push "${SPAWN_DESKTOP_DIR}\\${SPAWN_DISPLAY_NAME}.lnk"', uninstall_section)

    def test_compile_nsis_installer_restricts_custom_shortcut_dirs_to_test_identities(self) -> None:
        with tempfile.TemporaryDirectory(prefix="spawn-nsis-sdirs-") as tmp:
            root = Path(tmp)
            app_dir = root / "app"
            app_dir.mkdir()
            (app_dir / "spawn-dev-test.exe").write_bytes(b"MZ")
            out_file = root / "out" / "setup.exe"
            out_file.parent.mkdir()
            work_dir = root / "work"
            recorded_cmds: list[list[str]] = []

            def fake_run(cmd: list[str], **_kwargs: object) -> subprocess.CompletedProcess[str]:
                recorded_cmds.append(cmd)
                out_file.write_bytes(b"MZ-setup")
                return subprocess.CompletedProcess(args=cmd, returncode=0, stdout="", stderr="")

            for prod_identity in ("spawn", "spawn-dev"):
                with self.assertRaisesRegex(ValueError, "isolated \\*-test"):
                    pkg.compile_nsis_installer(
                        root / "makensis.exe",
                        REPO_ROOT / "installer" / "spawn.nsi",
                        profile=pkg.IDENTITY_PROFILES[prod_identity],
                        version="0.1.1",
                        numeric_version="0.1.1.0",
                        icon_file=REPO_ROOT / "assets" / "icon.ico",
                        app_dir=app_dir,
                        out_file=out_file,
                        work_dir=work_dir,
                        create_shortcuts=True,
                        fast_compression=True,
                        cwd=REPO_ROOT,
                        start_menu_dir=root / "sm",
                    )

            with mock.patch.object(pkg, "_run_quiet", side_effect=fake_run):
                pkg.compile_nsis_installer(
                    root / "makensis.exe",
                    REPO_ROOT / "installer" / "spawn.nsi",
                    profile=pkg.IDENTITY_PROFILES["spawn-dev-test"],
                    version="0.1.1-dev.1",
                    numeric_version="0.1.1.0",
                    icon_file=REPO_ROOT / "assets" / "icon-dev.ico",
                    app_dir=app_dir,
                    out_file=out_file,
                    work_dir=work_dir,
                    create_shortcuts=True,
                    fast_compression=True,
                    cwd=REPO_ROOT,
                    start_menu_dir=root / "sm",
                    desktop_dir=root / "dt",
                )
            self.assertEqual(len(recorded_cmds), 1)
            self.assertIn("-DSPAWN_DESKTOP_ON_UPDATE=1", recorded_cmds[0])
            self.assertIn(f"-DSPAWN_START_MENU_DIR={root / 'sm'}", recorded_cmds[0])
            self.assertIn(f"-DSPAWN_DESKTOP_DIR={root / 'dt'}", recorded_cmds[0])

    def test_profiles_use_the_shared_uninstall_key(self) -> None:
        for identity, profile in pkg.IDENTITY_PROFILES.items():
            self.assertEqual(profile.app_id, f"io.github.chialecode.{identity}")
        self.assertEqual(
            pkg.UNINSTALL_KEY_PREFIX + pkg.IDENTITY_PROFILES["spawn-dev"].app_id,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\io.github.chialecode.spawn-dev",
        )

    def test_compile_nsis_installer_passes_wx_and_fails_on_nonzero_exit(self) -> None:
        with tempfile.TemporaryDirectory(prefix="spawn-nsis-wx-") as tmp:
            root = Path(tmp)
            app_dir = root / "app"
            app_dir.mkdir()
            (app_dir / "spawn.exe").write_bytes(b"MZ")
            out_file = root / "out" / "setup.exe"
            out_file.parent.mkdir()
            work_dir = root / "work"
            recorded_cmds: list[list[str]] = []

            def fake_run(cmd: list[str], **_kwargs: object) -> subprocess.CompletedProcess[str]:
                recorded_cmds.append(cmd)
                out_file.write_bytes(b"MZ-setup")
                return subprocess.CompletedProcess(args=cmd, returncode=0, stdout="", stderr="")

            with mock.patch.object(pkg, "_run_quiet", side_effect=fake_run):
                pkg.compile_nsis_installer(
                    root / "makensis.exe",
                    REPO_ROOT / "installer" / "spawn.nsi",
                    profile=pkg.IDENTITY_PROFILES["spawn"],
                    version="0.1.1",
                    numeric_version="0.1.1.0",
                    icon_file=REPO_ROOT / "assets" / "icon.ico",
                    app_dir=app_dir,
                    out_file=out_file,
                    work_dir=work_dir,
                    create_shortcuts=True,
                    fast_compression=False,
                    cwd=REPO_ROOT,
                )

            self.assertEqual(len(recorded_cmds), 1)
            self.assertIn("-WX", recorded_cmds[0])
            self.assertIn("-DSPAWN_CREATE_SHORTCUTS=1", recorded_cmds[0])
            self.assertNotIn("-DSPAWN_FAST_COMPRESSION", recorded_cmds[0])

            def fake_warn_fail(cmd: list[str], **_kwargs: object) -> subprocess.CompletedProcess[str]:
                return subprocess.CompletedProcess(
                    args=cmd,
                    returncode=1,
                    stdout="warning: synthetic warning",
                    stderr="Error - aborting creation process",
                )

            with mock.patch.object(pkg, "_run_quiet", side_effect=fake_warn_fail):
                with self.assertRaisesRegex(RuntimeError, "makensis failed"):
                    pkg.compile_nsis_installer(
                        root / "makensis.exe",
                        REPO_ROOT / "installer" / "spawn.nsi",
                        profile=pkg.IDENTITY_PROFILES["spawn"],
                        version="0.1.1",
                        numeric_version="0.1.1.0",
                        icon_file=REPO_ROOT / "assets" / "icon.ico",
                        app_dir=app_dir,
                        out_file=out_file,
                        work_dir=work_dir,
                        create_shortcuts=False,
                        fast_compression=True,
                        cwd=REPO_ROOT,
                    )

    def test_check_nsis_script_covers_all_identities_and_shortcut_modes(self) -> None:
        calls: list[dict[str, object]] = []
        observed_app_dirs: list[Path] = []

        def fake_compile(
            makensis_exe: Path,
            nsi_path: Path,
            *,
            profile: pkg.IdentityProfile,
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
            self.assertEqual(nsi_path, REPO_ROOT / "installer" / "spawn.nsi")
            self.assertTrue((app_dir / f"{profile.identity}.exe").is_file())
            self.assertTrue((app_dir / "Spawn.OverlayHost.exe").is_file())
            self.assertTrue((app_dir / "Workers" / "Trainer" / "Spawn.TrainerWorker.exe").is_file())
            self.assertTrue((app_dir / "THIRD-PARTY-NOTICES.txt").is_file())
            self.assertTrue(icon_file.is_file())
            self.assertEqual(fast_compression, profile.dev_like)
            observed_app_dirs.append(app_dir)
            calls.append(
                {
                    "identity": profile.identity,
                    "create_shortcuts": create_shortcuts,
                    "fast_compression": fast_compression,
                    "version": version,
                    "numeric_version": numeric_version,
                }
            )

        out = io.StringIO()
        with mock.patch("sys.stdout", out):
            checked = pkg.check_nsis_script(
                REPO_ROOT,
                makensis_exe=Path("makensis.exe"),
                compile_fn=fake_compile,
            )

        expected = {
            (identity, create_shortcuts)
            for identity in ("spawn", "spawn-dev", "spawn-test", "spawn-dev-test")
            for create_shortcuts in (False, True)
        }
        self.assertEqual(len(checked), 8)
        self.assertEqual(set(checked), expected)
        self.assertEqual({(c["identity"], c["create_shortcuts"]) for c in calls}, expected)
        for app_dir in observed_app_dirs:
            self.assertFalse(app_dir.exists(), "temporary dummy app dir must be cleaned up")

    def test_check_nsis_script_real_makensis_passes_clean_and_fails_on_warning(self) -> None:
        try:
            makensis = pkg.find_makensis({}, fetch=False)
        except FileNotFoundError:
            self.skipTest("pinned NSIS 3.13 not cached locally (offline unit test)")

        out = io.StringIO()
        with mock.patch("sys.stdout", out):
            checked = pkg.check_nsis_script(REPO_ROOT, makensis_exe=makensis)
        self.assertEqual(len(checked), 8)

        with tempfile.TemporaryDirectory(prefix="spawn-nsis-warn-") as tmp:
            bad_nsi = Path(tmp) / "spawn-warn.nsi"
            original = (REPO_ROOT / "installer" / "spawn.nsi").read_text(encoding="utf-8")
            bad_nsi.write_text(
                original + '\n!warning "synthetic warning to verify -WX enforcement"\n',
                encoding="utf-8",
                newline="\n",
            )
            with self.assertRaisesRegex(RuntimeError, "makensis failed"):
                pkg.check_nsis_script(
                    REPO_ROOT,
                    makensis_exe=makensis,
                    nsi_path=bad_nsi,
                    profiles=[pkg.IDENTITY_PROFILES["spawn"]],
                )


class FakeClock:
    def __init__(self) -> None:
        self.now = 0.0

    def monotonic(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.now += seconds


class WindowDeferTests(unittest.TestCase):
    def test_no_window_means_no_wait(self) -> None:
        clock = FakeClock()
        result = pkg.wait_while_window_open(
            Path("x.exe"), is_window_open=lambda _p: False, sleep=clock.sleep, monotonic=clock.monotonic
        )
        self.assertEqual(result, (False, False, None))
        self.assertEqual(clock.now, 0.0)

    def test_waits_until_the_window_closes(self) -> None:
        clock = FakeClock()
        answers = iter([True, True, True, False])
        result = pkg.wait_while_window_open(
            Path("x.exe"), is_window_open=lambda _p: next(answers), sleep=clock.sleep, monotonic=clock.monotonic
        )
        self.assertEqual(result, (True, False, 4000))

    def test_gives_up_after_the_limit(self) -> None:
        clock = FakeClock()
        was_open, still_open, waited_ms = pkg.wait_while_window_open(
            Path("x.exe"), is_window_open=lambda _p: True, sleep=clock.sleep, monotonic=clock.monotonic
        )
        self.assertTrue(was_open and still_open)
        self.assertEqual(waited_ms, int(pkg.WINDOW_DEFER_TIMEOUT_SECONDS * 1000))


class InstallDecisionTests(unittest.TestCase):
    """install_package with fake registry, quit request, installer and window probe."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="spawn-install-test-")
        self.tmp = Path(self._tmp.name)
        self.install_dir = self.tmp / "Programs" / "spawn-dev-test"
        self.receipt_path = self.tmp / "install-dev.json"
        self.profile = pkg.IDENTITY_PROFILES["spawn-dev-test"]
        self.registry: dict[str, dict[str, str]] = {}
        self.commands: list[object] = []
        self.installer_rc = 0
        self.lookups: list[str] = []

        def lookup(app_id: str) -> dict[str, str] | None:
            self.lookups.append(app_id)
            return self.registry.get(app_id)

        def run_quiet(cmd: object, **_kwargs: object) -> subprocess.CompletedProcess[str]:
            self.commands.append(cmd)
            if self.installer_rc == 0:
                for rel in ("spawn-dev-test.exe", "Workers/Trainer/Spawn.TrainerWorker.exe", "THIRD-PARTY-NOTICES.txt"):
                    (self.install_dir / rel).parent.mkdir(parents=True, exist_ok=True)
                    (self.install_dir / rel).write_bytes(b"x")
                self.registry[self.profile.app_id] = {
                    "DisplayVersion": "0.1.1-dev.2",
                    "InstallLocation": str(self.install_dir),
                }
            return subprocess.CompletedProcess(args=cmd, returncode=self.installer_rc, stdout="", stderr="")

        patches = [
            mock.patch.object(pkg, "lookup_uninstall_entry", side_effect=lookup),
            mock.patch.object(pkg, "_run_quiet", side_effect=run_quiet),
            mock.patch.object(pkg, "request_graceful_quit_and_wait", return_value=(False, 0)),
            mock.patch.object(pkg, "retire_locked_binaries", return_value=0),
            mock.patch.object(pkg, "write_dev_workspace_bridge", return_value=None),
            mock.patch.object(pkg, "should_skip_dev_auto_install", return_value=None),
            mock.patch.object(pkg, "launch_detached_background", side_effect=AssertionError("no relaunch in tests")),
        ]
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def _install(self, *, window: tuple[bool, bool, int | None] = (False, False, None)) -> dict[str, object]:
        with mock.patch.object(pkg, "wait_while_window_open", return_value=window):
            return pkg.install_package(
                REPO_ROOT,
                self.profile,
                self.tmp / "spawn-dev-test-setup-0.1.1-dev.2-x64.exe",
                "0.1.1-dev.2",
                install_dir_override=self.install_dir,
                relaunch_background=False,
                receipt_path=self.receipt_path,
                is_dev_auto_install=True,
            )

    def test_first_install_is_automatic_and_the_receipt_is_unified(self) -> None:
        receipt = self._install()
        self.assertTrue(receipt["installed"])
        self.assertTrue(receipt["firstInstall"])
        self.assertIsNone(receipt["oldVersion"])
        self.assertEqual(receipt["installLocation"], str(self.install_dir))
        for field in UNIFIED_RECEIPT_FIELDS:
            self.assertIn(field, receipt)
        setup = self.tmp / "spawn-dev-test-setup-0.1.1-dev.2-x64.exe"
        self.assertEqual(self.commands, [subprocess.list2cmdline([str(setup), "/S"]) + f" /D={self.install_dir}"])
        self.assertTrue(self.receipt_path.is_file())
        self.assertEqual(set(self.lookups), {"io.github.chialecode.spawn-dev-test"})

    def test_update_records_the_old_version(self) -> None:
        self.registry[self.profile.app_id] = {"DisplayVersion": "0.1.1-dev.1", "InstallLocation": str(self.install_dir)}
        receipt = self._install()
        self.assertTrue(receipt["installed"])
        self.assertFalse(receipt["firstInstall"])
        self.assertEqual(receipt["oldVersion"], "0.1.1-dev.1")

    def test_window_closed_after_a_wait_is_installed_and_recorded(self) -> None:
        receipt = self._install(window=(True, False, 6000))
        self.assertTrue(receipt["installed"])
        self.assertTrue(receipt["windowOpen"] and receipt["deferred"])
        self.assertEqual(receipt["deferMs"], 6000)

    def test_window_left_open_skips_without_running_the_installer(self) -> None:
        receipt = self._install(window=(True, True, 600000))
        self.assertFalse(receipt["installed"])
        self.assertTrue(receipt["skipped"])
        self.assertEqual(receipt["reason"], "window_open")
        self.assertIn("--install-only", str(receipt["guidance"]))
        self.assertEqual(self.commands, [])

    def test_installer_failure_is_reported_with_its_exit_code(self) -> None:
        self.installer_rc = 7
        with self.assertRaisesRegex(RuntimeError, "code 7 .*another program"):
            self._install()
        receipt = json.loads(self.receipt_path.read_text(encoding="utf-8"))
        self.assertEqual(receipt["reason"], "installer_failed_exit_7")
        self.assertFalse(receipt["installed"])

    def test_skip_receipt_from_the_environment_is_unified_too(self) -> None:
        receipt = pkg.new_install_receipt(self.profile, "0.1.1-dev.2")
        for field in UNIFIED_RECEIPT_FIELDS:
            self.assertIn(field, receipt)
        self.assertIn("gitBranch", receipt)
        self.assertIn("gitCommit", receipt)

    def test_dev_auto_install_allows_clean_pr_branch_and_records_git_metadata(self) -> None:
        pr_repo = self.tmp / "pr-repo"
        pr_repo.mkdir()
        clean_env = pkg._clean_git_env()

        def run_git(*git_args: str) -> str:
            proc = subprocess.run(
                ["git", *git_args],
                cwd=str(pr_repo),
                env=clean_env,
                stdin=subprocess.DEVNULL,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                check=False,
            )
            self.assertEqual(proc.returncode, 0, f"git {git_args} failed: {proc.stdout}\n{proc.stderr}")
            return proc.stdout.strip()

        run_git("init", "-b", "main")
        run_git("config", "user.name", "SPAWN Test")
        run_git("config", "user.email", "spawn-test@example.invalid")
        run_git("config", "commit.gpgsign", "false")
        (pr_repo / "README.md").write_text("base\n", encoding="utf-8", newline="\n")
        run_git("add", "README.md")
        run_git("commit", "-m", "chore: base")
        run_git("checkout", "-b", "fix/71-prereview-dev-install")
        (pr_repo / "change.txt").write_text("pr change\n", encoding="utf-8", newline="\n")
        run_git("add", "change.txt")
        run_git("commit", "-m", "fix: sample pr commit")
        expected_sha = run_git("rev-parse", "--short=12", "HEAD")

        with mock.patch.object(pkg, "wait_while_window_open", return_value=(False, False, None)):
            receipt = pkg.install_package(
                pr_repo,
                self.profile,
                self.tmp / "spawn-dev-test-setup-0.1.1-dev.2-x64.exe",
                "0.1.1-dev.2",
                install_dir_override=self.install_dir,
                relaunch_background=False,
                receipt_path=self.receipt_path,
                is_dev_auto_install=True,
            )

        self.assertTrue(receipt["installed"])
        self.assertFalse(receipt["skipped"])
        self.assertEqual(receipt["gitBranch"], "fix/71-prereview-dev-install")
        self.assertEqual(receipt["gitCommit"], expected_sha)


class IsolatedShortcutReinstallRuntimeTests(unittest.TestCase):
    """End-to-end NSIS silent reinstall shortcut verification using isolated temp shortcut dirs.

    Runs only on Windows when pinned NSIS 3.13 is cached locally and CI!=true. Never touches the
    user's real Start Menu, Desktop, `spawn`, or `spawn-dev` installations.
    """

    def _require_local_nsis(self) -> Path:
        if os.name != "nt":
            self.skipTest("Windows-only NSIS runtime test")
        if os.environ.get("CI", "").strip().lower() == "true":
            self.skipTest("CI runs compile-only NSIS verification without executing installers")
        try:
            return pkg.find_makensis({}, fetch=False)
        except FileNotFoundError:
            self.skipTest("pinned NSIS 3.13 not cached locally (offline unit test)")

    @staticmethod
    def _snapshot_real_shortcuts() -> dict[Path, tuple[bool, int | None]]:
        dirs: list[Path] = []
        appdata = os.environ.get("APPDATA", "").strip()
        if appdata:
            dirs.append(Path(appdata) / "Microsoft" / "Windows" / "Start Menu" / "Programs")
        userprofile = os.environ.get("USERPROFILE", "").strip()
        if userprofile:
            dirs.append(Path(userprofile) / "Desktop")
        names = ("SPAWN.lnk", "SPAWN Dev.lnk", "SPAWN Test.lnk", "SPAWN Dev Test.lnk")
        snap: dict[Path, tuple[bool, int | None]] = {}
        for folder in dirs:
            for name in names:
                p = folder / name
                if p.exists():
                    snap[p] = (True, p.stat().st_mtime_ns)
                else:
                    snap[p] = (False, None)
        return snap

    @staticmethod
    def _compile_dummy_setup(
        makensis: Path,
        *,
        profile: pkg.IdentityProfile,
        version: str,
        tmp_root: Path,
        tag: str,
        start_menu_dir: Path,
        desktop_dir: Path,
    ) -> Path:
        rel_ico, dev_ico = pkg.ensure_icons(REPO_ROOT)
        app_dir = tmp_root / f"app-{tag}"
        for rel_file, payload in (
            (f"{profile.identity}.exe", f"MZ-{tag}".encode("utf-8")),
            ("Spawn.OverlayHost.exe", b"MZ-overlay"),
            ("Workers/Trainer/Spawn.TrainerWorker.exe", b"MZ-worker"),
            ("VERSION.txt", f"version={version}\n".encode("utf-8")),
            ("THIRD-PARTY-NOTICES.txt", b"SPAWN notices\n"),
        ):
            target = app_dir / rel_file
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(payload)
        out_file = tmp_root / f"{profile.identity}-{tag}.exe"
        work_dir = tmp_root / f"work-{tag}"
        pkg.compile_nsis_installer(
            makensis,
            REPO_ROOT / "installer" / "spawn.nsi",
            profile=profile,
            version=version,
            numeric_version=pkg.compute_numeric_version(version),
            icon_file=dev_ico if profile.dev_like else rel_ico,
            app_dir=app_dir,
            out_file=out_file,
            work_dir=work_dir,
            create_shortcuts=True,
            fast_compression=True,
            cwd=REPO_ROOT,
            start_menu_dir=start_menu_dir,
            desktop_dir=desktop_dir,
        )
        return out_file

    def _run_silent_install(self, setup_exe: Path, install_dir: Path) -> None:
        cmd = pkg.build_silent_install_command(setup_exe, install_dir)
        proc = pkg._run_quiet(cmd, cwd=REPO_ROOT, timeout=120.0)
        self.assertEqual(proc.returncode, 0, f"silent install failed (exit {proc.returncode}): {proc.stdout}\n{proc.stderr}")

    def _run_silent_uninstall(self, profile: pkg.IdentityProfile, install_dir: Path) -> None:
        unins = install_dir / "Uninstall.exe"
        if unins.is_file():
            proc = pkg._run_quiet([str(unins), "/S"], cwd=REPO_ROOT, timeout=120.0)
            self.assertEqual(proc.returncode, 0, f"silent uninstall failed (exit {proc.returncode}): {proc.stdout}\n{proc.stderr}")
            deadline = time.monotonic() + 30.0
            while pkg.lookup_uninstall_entry(profile.app_id) is not None or unins.exists():
                if time.monotonic() >= deadline:
                    self.fail(f"{profile.identity} uninstall did not complete within 30s")
                time.sleep(0.15)
        elif pkg.lookup_uninstall_entry(profile.app_id) is not None:
            pkg.uninstall_identity(REPO_ROOT, profile, allow_release=False)

    def test_dev_silent_reinstall_skips_start_menu_and_creates_desktop(self) -> None:
        makensis = self._require_local_nsis()
        profile = pkg.IDENTITY_PROFILES["spawn-dev-test"]
        before_real = self._snapshot_real_shortcuts()

        with tempfile.TemporaryDirectory(prefix="spawn-dev-shortcut-rt-") as td:
            tmp = Path(td)
            install_dir = tmp / "Programs" / profile.identity
            sm_dir = tmp / "start-menu"
            dt_dir = tmp / "desktop"
            sm_dir.mkdir()
            dt_dir.mkdir()
            sm_lnk = sm_dir / f"{profile.display_name}.lnk"
            dt_lnk = dt_dir / f"{profile.display_name}.lnk"

            try:
                if pkg.lookup_uninstall_entry(profile.app_id) is not None:
                    pkg.uninstall_identity(REPO_ROOT, profile, allow_release=False)

                v1 = "0.1.1-dev.202611270001"
                v2 = "0.1.1-dev.202611270002"
                v3 = "0.1.1-dev.202611270003"
                setup_v1 = self._compile_dummy_setup(
                    makensis, profile=profile, version=v1, tmp_root=tmp, tag="v1", start_menu_dir=sm_dir, desktop_dir=dt_dir
                )
                setup_v2 = self._compile_dummy_setup(
                    makensis, profile=profile, version=v2, tmp_root=tmp, tag="v2", start_menu_dir=sm_dir, desktop_dir=dt_dir
                )
                setup_v3 = self._compile_dummy_setup(
                    makensis, profile=profile, version=v3, tmp_root=tmp, tag="v3", start_menu_dir=sm_dir, desktop_dir=dt_dir
                )

                # 1. First silent install creates BOTH Start Menu and Desktop shortcuts.
                self._run_silent_install(setup_v1, install_dir)
                self.assertTrue(sm_lnk.is_file(), "first install must create Start Menu shortcut")
                self.assertTrue(dt_lnk.is_file(), "first install must create Desktop shortcut")
                entry = pkg.lookup_uninstall_entry(profile.app_id)
                self.assertIsNotNone(entry)
                self.assertEqual(entry["DisplayVersion"], v1)  # type: ignore[index]

                # 2. Dev silent reinstall when user deleted BOTH shortcuts:
                #    Start Menu MUST stay absent, Desktop MUST be created.
                sm_lnk.unlink()
                dt_lnk.unlink()
                self._run_silent_install(setup_v2, install_dir)
                self.assertFalse(sm_lnk.exists(), "dev silent reinstall must NOT recreate a deleted Start Menu shortcut")
                self.assertTrue(dt_lnk.is_file(), "dev silent reinstall must still create the Desktop shortcut")
                entry = pkg.lookup_uninstall_entry(profile.app_id)
                self.assertEqual(entry["DisplayVersion"], v2)  # type: ignore[index]

                # 3. Dev silent reinstall when user kept Start Menu shortcut (with sentinel bytes & old mtime):
                #    Start Menu MUST stay completely untouched (exact mtime & bytes), Desktop MUST be created.
                sentinel = b"KEEP-EXISTING-START-MENU-SHORTCUT"
                sm_lnk.write_bytes(sentinel)
                old_epoch = 1577836800  # 2020-01-01T00:00:00Z
                os.utime(sm_lnk, (old_epoch, old_epoch))
                expected_mtime_ns = sm_lnk.stat().st_mtime_ns
                dt_lnk.unlink()

                self._run_silent_install(setup_v3, install_dir)
                self.assertTrue(sm_lnk.is_file(), "dev silent reinstall must NOT delete an existing Start Menu shortcut")
                self.assertEqual(sm_lnk.stat().st_mtime_ns, expected_mtime_ns, "Start Menu shortcut mtime must be preserved")
                self.assertEqual(sm_lnk.read_bytes(), sentinel, "Start Menu shortcut content must not be rewritten")
                self.assertTrue(dt_lnk.is_file(), "dev silent reinstall must create the Desktop shortcut")
                entry = pkg.lookup_uninstall_entry(profile.app_id)
                self.assertEqual(entry["DisplayVersion"], v3)  # type: ignore[index]

                # 4. Full silent uninstall removes both shortcuts and the uninstall registry entry.
                self._run_silent_uninstall(profile, install_dir)
                self.assertFalse(sm_lnk.exists(), "full uninstall must remove Start Menu shortcut")
                self.assertFalse(dt_lnk.exists(), "full uninstall must remove Desktop shortcut")
                self.assertIsNone(pkg.lookup_uninstall_entry(profile.app_id))
            finally:
                if pkg.lookup_uninstall_entry(profile.app_id) is not None or (install_dir / "Uninstall.exe").exists():
                    self._run_silent_uninstall(profile, install_dir)
                self.assertEqual(self._snapshot_real_shortcuts(), before_real)

    def test_release_silent_reinstall_keeps_start_menu_untouched_and_respects_deleted_desktop(self) -> None:
        makensis = self._require_local_nsis()
        profile = pkg.IDENTITY_PROFILES["spawn-test"]
        before_real = self._snapshot_real_shortcuts()

        with tempfile.TemporaryDirectory(prefix="spawn-rel-shortcut-rt-") as td:
            tmp = Path(td)
            install_dir = tmp / "Programs" / profile.identity
            sm_dir = tmp / "start-menu"
            dt_dir = tmp / "desktop"
            sm_dir.mkdir()
            dt_dir.mkdir()
            sm_lnk = sm_dir / f"{profile.display_name}.lnk"
            dt_lnk = dt_dir / f"{profile.display_name}.lnk"

            try:
                if pkg.lookup_uninstall_entry(profile.app_id) is not None:
                    pkg.uninstall_identity(REPO_ROOT, profile, allow_release=False)

                v1, v2, v3 = "0.1.1", "0.1.2", "0.1.3"
                setup_v1 = self._compile_dummy_setup(
                    makensis, profile=profile, version=v1, tmp_root=tmp, tag="v1", start_menu_dir=sm_dir, desktop_dir=dt_dir
                )
                setup_v2 = self._compile_dummy_setup(
                    makensis, profile=profile, version=v2, tmp_root=tmp, tag="v2", start_menu_dir=sm_dir, desktop_dir=dt_dir
                )
                setup_v3 = self._compile_dummy_setup(
                    makensis, profile=profile, version=v3, tmp_root=tmp, tag="v3", start_menu_dir=sm_dir, desktop_dir=dt_dir
                )

                # 1. First silent install creates both shortcuts.
                self._run_silent_install(setup_v1, install_dir)
                self.assertTrue(sm_lnk.is_file())
                self.assertTrue(dt_lnk.is_file())

                # 2. Release silent update: Start Menu kept untouched (mtime & bytes preserved),
                #    deleted Desktop shortcut MUST stay absent.
                sentinel = b"KEEP-RELEASE-START-MENU"
                sm_lnk.write_bytes(sentinel)
                old_epoch = 1577836800
                os.utime(sm_lnk, (old_epoch, old_epoch))
                expected_mtime_ns = sm_lnk.stat().st_mtime_ns
                dt_lnk.unlink()

                self._run_silent_install(setup_v2, install_dir)
                self.assertTrue(sm_lnk.is_file())
                self.assertEqual(sm_lnk.stat().st_mtime_ns, expected_mtime_ns)
                self.assertEqual(sm_lnk.read_bytes(), sentinel)
                self.assertFalse(dt_lnk.exists(), "release silent update must NOT recreate a deleted Desktop shortcut")

                # 3. Release silent update when Start Menu absent & Desktop present:
                #    Start Menu stays absent, existing Desktop shortcut stays present.
                sm_lnk.unlink()
                dt_lnk.write_bytes(b"EXISTING-DESKTOP")
                self._run_silent_install(setup_v3, install_dir)
                self.assertFalse(sm_lnk.exists(), "release silent update must NOT recreate a deleted Start Menu shortcut")
                self.assertTrue(dt_lnk.is_file(), "release silent update must keep an existing Desktop shortcut")

                # 4. Full uninstall cleans up Desktop shortcut and uninstall entry.
                self._run_silent_uninstall(profile, install_dir)
                self.assertFalse(sm_lnk.exists())
                self.assertFalse(dt_lnk.exists())
                self.assertIsNone(pkg.lookup_uninstall_entry(profile.app_id))
            finally:
                if pkg.lookup_uninstall_entry(profile.app_id) is not None or (install_dir / "Uninstall.exe").exists():
                    self._run_silent_uninstall(profile, install_dir)
                self.assertEqual(self._snapshot_real_shortcuts(), before_real)


class GenerateIconsCheckTests(unittest.TestCase):
    def test_check_flag_verifies_committed_assets_and_detects_tampering(self) -> None:
        out = io.StringIO()
        with mock.patch("sys.stdout", out):
            self.assertEqual(generate_icons.main(["--check"]), 0)
        self.assertIn("check passed", out.getvalue())

        assets_dir = REPO_ROOT / "assets"
        with tempfile.TemporaryDirectory(prefix="spawn-icons-check-") as td:
            tmp_assets = Path(td)
            for name in ("icon.png", "icon.ico", "icon-dev.png", "icon-dev.ico"):
                (tmp_assets / name).write_bytes((assets_dir / name).read_bytes())

            tampered = tmp_assets / "icon-dev.ico"
            raw = bytearray(tampered.read_bytes())
            raw[-1] ^= 0xFF
            tampered.write_bytes(bytes(raw))

            err = io.StringIO()
            with mock.patch("sys.stderr", err):
                self.assertEqual(generate_icons.main(["--check", "--assets-dir", str(tmp_assets)]), 1)
            self.assertIn("icon-dev.ico", err.getvalue())


if __name__ == "__main__":
    unittest.main(verbosity=1)
