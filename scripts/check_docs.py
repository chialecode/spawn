"""Zero-dependency documentation, registry, traceability, and hygiene gate for SPAWN.

Requires Python 3.12+ standard library only.
Validates:
1. Required root entrypoints and governance files exist (and CLAUDE.md == '@AGENTS.md').
2. docs/governance/document-registry.json covers all owned Markdown (.md) and governed JSON (.json) files.
3. All relative Markdown links and fragment anchors resolve to real files/headings inside the repository.
4. Requirement / acceptance traceability consistency when docs/delivery/traceability.json is present.
5. Managed files contain no sensitive artifacts, machine-specific personal/workspace paths, or external sibling repo leaks.
"""

from __future__ import annotations

import argparse
from collections import Counter
import json
import os
from pathlib import Path
import re
import sys
from urllib.parse import unquote, urlsplit


EXCLUDED_DIRS = {
    ".git",
    ".local",
    ".vs",
    ".vscode",
    ".idea",
    ".venv",
    "venv",
    "__pycache__",
    ".pytest_cache",
    ".mypy_cache",
    ".ruff_cache",
    "bin",
    "obj",
    "publish",
    "artifacts",
    "out",
    "dist",
    "build",
    "TestResults",
    "BenchmarkDotNet.Artifacts",
    ".tools",
    "SPAWN_Vault",
    "node_modules",
    "vendor",
    "third-party",
    ".worktrees",
    "workers",
}

REQUIRED_FILES = (
    "README.md",
    "AGENTS.md",
    "CLAUDE.md",
    "CONTRIBUTING.md",
    "DESIGN.md",
    "REVIEW.md",
    "SECURITY.md",
    "LESSONS.md",
    "LICENSE",
    "docs/README.md",
    "docs/project-baseline.md",
    "docs/governance/documentation-policy.md",
    "docs/governance/document-registry.json",
    "scripts/check_docs.py",
)

ALLOWED_KINDS = {
    "entry",
    "index",
    "rule",
    "requirements",
    "design",
    "spec",
    "plan",
    "status",
    "decision",
    "research",
    "evidence",
    "template",
    "governance",
}

ALLOWED_STATUSES = {"active", "draft", "reference", "superseded"}
STAGES = {"M1": 1, "M2": 2, "M3": 3, "M4": 4, "M5": 5, "M6": 6}

FORBIDDEN_FILE_PATTERNS = (
    re.compile(r"(?:^|/)\.env(?:\.(?!example$)[^/]+)?$", re.I),
    re.compile(r"\.(?:sqlite|sqlite3|db|db-wal|db-shm|sppk|pem|key|p12|pfx|snk|sec)$", re.I),
    re.compile(r"(?:^|/)id_(?:rsa|ed25519|ecdsa|dsa)(?:\.pub)?$", re.I),
)

ALLOWED_PUBLIC_KEY_PEMS = frozenset(
    {
        "installer/update-signing-public.pem",
    }
)

TEXT_EXTENSIONS = {
    ".md",
    ".json",
    ".yml",
    ".yaml",
    ".py",
    ".cs",
    ".csproj",
    ".props",
    ".targets",
    ".sln",
    ".slnx",
    ".xaml",
    ".proto",
    ".ps1",
    ".nsi",
    ".sh",
    ".txt",
    ".editorconfig",
    ".gitattributes",
    ".gitignore",
}

# Encoded tokens prevent self-matching if someone greps the repository for sibling repo names.
_SIB_A = "\x64\x65\x6c\x74\x61"
_SIB_B = "\x6d\x61\x6e\x67\x61"
_SIB_C = "\x74\x72\x61\x63\x65"
_SIB_GROUP = f"(?:{_SIB_A}|{_SIB_B}|{_SIB_C})"

HYGIENE_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    (
        "Windows user profile absolute path",
        re.compile(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]Users[\\/]", re.I),
    ),
    (
        "personal workspace absolute path",
        re.compile(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/](?:Code|Projects?|Work|Workspace|Repos?)[\\/]", re.I),
    ),
    (
        "Unix user home path",
        re.compile(r"(?<![A-Za-z0-9._/-])/(?:Users|home)/[A-Za-z0-9._-]+/"),
    ),
    (
        "external sibling repository reference",
        re.compile(
            rf"(?i)\b(?:chialecode/{_SIB_GROUP}|reference/{_SIB_GROUP}|project[\\/]{_SIB_GROUP})\b"
        ),
    ),
    (
        "external sibling package or channel identifier",
        re.compile(
            rf"(?i)\b(?:{_SIB_A}-(?:cli|core|data|app|desktop|model|dev|test)|{_SIB_B}-dev|{_SIB_C}-dev)\b"
        ),
    ),
    (
        "external sibling environment variable",
        re.compile(
            rf"\b(?:{_SIB_A.upper()}|{_SIB_B.upper()}|{_SIB_C.upper()})_(?:REFERENCE_DIR|DATA_DIR|DOCUMENTS_DIR|CHANNEL|IDENTITY|GATE_JOBS)\b"
        ),
    ),
    (
        "external sibling project name",
        re.compile(rf"\b{_SIB_A.upper()}\b|(?i:\b{_SIB_B}\b)"),
    ),
    (
        "private key PEM block",
        re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----"),
    ),
    (
        "OpenAI-style secret key",
        re.compile(r"\bsk-[A-Za-z0-9]{20,}\b"),
    ),
    (
        "GitHub personal/app token",
        re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b"),
    ),
    (
        "AWS access key ID",
        re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    ),
    (
        "credential-embedded URL",
        re.compile(r"https?://[^/\s:@\"'`]+:[^/\s@\"'`]+@[^/\s\"'`]+"),
    ),
)


def strip_fenced_blocks(text: str) -> str:
    """Remove fenced code blocks (backticks or tildes)."""
    out: list[str] = []
    fence: str | None = None
    for line in text.splitlines():
        mark = re.match(r"^ {0,3}(`{3,}|~{3,})", line)
        if fence is None and mark:
            fence = mark.group(1)
            continue
        if fence is not None:
            stripped = line.strip()
            if len(stripped) >= len(fence) and set(stripped) == {fence[0]}:
                fence = None
            continue
        out.append(line)
    return "\n".join(out)


def strip_inline_code(line: str) -> str:
    """Remove inline backtick code spans from a single line before link scanning."""
    return re.sub(r"(`+)([^`]*?)\1", "", line)


def collect_anchors(markdown_text: str) -> set[str]:
    """Collect GitHub-flavored heading slugs and explicit HTML id anchors."""
    content = strip_fenced_blocks(markdown_text)
    found: set[str] = set()
    counts: Counter[str] = Counter()
    for heading in re.findall(r"^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$", content, re.M):
        clean = re.sub(r"<[^>]+>", "", heading)
        clean = re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", clean)
        clean = clean.replace("`", "").strip().lower()
        slug = "".join(ch for ch in clean if ch.isalnum() or ch in "_-" or ch.isspace())
        slug = re.sub(r"\s", "-", slug)
        if slug:
            actual = f"{slug}-{counts[slug]}" if counts[slug] else slug
            found.add(actual)
            counts[slug] += 1
    for explicit_id in re.findall(r'<(?:a|h[1-6])\b[^>]*\bid=["\']([^"\']+)["\']', content, re.I):
        found.add(explicit_id)
        found.add(explicit_id.lower())
    return found


def check_repository(root: Path) -> tuple[list[str], dict[str, int]]:
    root = root.resolve()
    errors: list[str] = []

    def safe_repo_file(rel_path: str, context: str) -> Path | None:
        if not isinstance(rel_path, str) or not rel_path or "\\" in rel_path or rel_path.startswith("/"):
            errors.append(f"{context}: invalid portable repository path: {rel_path!r}")
            return None
        resolved = (root / rel_path).resolve()
        if not resolved.is_relative_to(root):
            errors.append(f"{context}: path escapes repository root: {rel_path}")
            return None
        if not resolved.is_file():
            errors.append(f"{context}: missing file: {rel_path}")
            return None
        return resolved

    def load_json_object(rel_path: str) -> dict:
        target = safe_repo_file(rel_path, "json")
        if target is None:
            return {}
        try:
            parsed = json.loads(target.read_text(encoding="utf-8-sig"))
            if not isinstance(parsed, dict):
                errors.append(f"{rel_path}: top-level JSON value must be an object")
                return {}
            return parsed
        except (OSError, ValueError) as exc:
            errors.append(f"{rel_path}: invalid JSON ({exc})")
            return {}

    # 1. Check required root & governance files.
    for req_file in REQUIRED_FILES:
        safe_repo_file(req_file, "required-files")

    # Walk repository once to collect owned Markdown, governed JSON, and all managed files.
    md_docs: dict[str, str] = {}
    governed_json_paths: set[str] = set()
    all_files: list[tuple[str, Path]] = []

    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        dirnames[:] = sorted(
            d for d in dirnames if d not in EXCLUDED_DIRS and not d.lower().startswith("benchmarkdotnet")
        )
        for fname in sorted(filenames):
            fpath = Path(dirpath) / fname
            rel = fpath.relative_to(root).as_posix()
            resolved = fpath.resolve()
            if not resolved.is_relative_to(root):
                errors.append(f"{rel}: symlink escapes repository root")
                continue
            all_files.append((rel, fpath))
            if fname.lower().endswith(".md"):
                try:
                    md_docs[rel] = fpath.read_text(encoding="utf-8-sig")
                except (OSError, UnicodeDecodeError) as exc:
                    errors.append(f"{rel}: failed to read UTF-8 Markdown ({exc})")
            elif fname.lower().endswith(".json") and (rel.startswith("docs/") or rel.startswith(".github/")):
                governed_json_paths.add(rel)
                try:
                    json.loads(fpath.read_text(encoding="utf-8-sig"))
                except (OSError, UnicodeDecodeError, ValueError) as exc:
                    errors.append(f"{rel}: invalid JSON syntax ({exc})")

    if "CLAUDE.md" in md_docs and md_docs["CLAUDE.md"].strip() != "@AGENTS.md":
        errors.append("CLAUDE.md: must contain only '@AGENTS.md'")

    # 2. Validate document-registry.json bidirectional completeness.
    registry = load_json_object("docs/governance/document-registry.json")
    if registry:
        if registry.get("schemaVersion") != 1:
            errors.append("docs/governance/document-registry.json: expected schemaVersion == 1")
        current_stage = registry.get("currentStage")
        if current_stage not in STAGES:
            errors.append(f"docs/governance/document-registry.json: invalid currentStage {current_stage!r}")
        rows = registry.get("documents")
        if not isinstance(rows, list):
            errors.append("docs/governance/document-registry.json: 'documents' must be a list")
            rows = []

        registered_counts: Counter[str] = Counter()
        for idx, row in enumerate(rows):
            if not isinstance(row, dict):
                errors.append(f"registry entry #{idx}: expected object")
                continue
            doc_path = row.get("path", "")
            registered_counts[doc_path] += 1
            safe_repo_file(doc_path, "document-registry")
            for field in ("kind", "status", "owner", "scope", "reviewed", "basis"):
                val = row.get(field)
                if not isinstance(val, str) or not val.strip():
                    errors.append(f"{doc_path or f'entry#{idx}'}: missing non-empty '{field}'")
            if row.get("kind") not in ALLOWED_KINDS:
                errors.append(f"{doc_path}: invalid kind {row.get('kind')!r}")
            if row.get("status") not in ALLOWED_STATUSES:
                errors.append(f"{doc_path}: invalid status {row.get('status')!r}")
            if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", str(row.get("reviewed", ""))):
                errors.append(f"{doc_path}: 'reviewed' must be YYYY-MM-DD")
            if row.get("status") == "superseded":
                safe_repo_file(str(row.get("supersededBy", "")), f"{doc_path} supersededBy")
            retire_after = row.get("retireAfter")
            if retire_after is not None:
                if retire_after not in STAGES:
                    errors.append(f"{doc_path}: invalid retireAfter stage {retire_after!r}")
                elif current_stage in STAGES and STAGES[current_stage] > STAGES[retire_after]:
                    errors.append(
                        f"{doc_path}: retireAfter ({retire_after}) has expired for currentStage ({current_stage})"
                    )

        for dup_path, count in sorted(registered_counts.items()):
            if count > 1:
                errors.append(f"document-registry.json: duplicate path {dup_path} ({count}x)")

        expected_registered = set(md_docs.keys()) | governed_json_paths
        registered_set = set(registered_counts.keys())
        for missing_reg in sorted(expected_registered - registered_set):
            errors.append(f"unregistered document (missing from document-registry.json): {missing_reg}")
        for extra_reg in sorted(registered_set - expected_registered):
            if (root / extra_reg).is_file():
                errors.append(f"registered file outside governed .md/.json set: {extra_reg}")

    # 3. Validate Markdown inline links and anchors.
    anchor_cache: dict[str, set[str]] = {}

    def get_anchors(rel_md: str) -> set[str]:
        if rel_md not in anchor_cache:
            anchor_cache[rel_md] = collect_anchors(md_docs.get(rel_md, ""))
        return anchor_cache[rel_md]

    link_pattern = re.compile(r"!?\[[^\]\n]+\]\((<[^>]+>|[^\s)]+)(?:\s+\"[^\"]*\")?\)")
    ref_def_pattern = re.compile(r"^\s*\[[^\]]+\]:\s+\S+", re.M)

    for rel_md, raw_text in sorted(md_docs.items()):
        prose_block = strip_fenced_blocks(raw_text)
        clean_lines = [strip_inline_code(line) for line in prose_block.splitlines()]
        clean_prose = "\n".join(clean_lines)

        if ref_def_pattern.search(clean_prose):
            errors.append(f"{rel_md}: reference-style link definitions are disallowed; use inline links")

        for match in link_pattern.finditer(clean_prose):
            raw_target = match.group(1).strip("<>").strip()
            if not raw_target:
                errors.append(f"{rel_md}: empty Markdown link target")
                continue
            parsed = urlsplit(raw_target)
            if parsed.scheme or parsed.netloc:
                continue
            if "\\" in parsed.path or parsed.path.startswith("/"):
                errors.append(f"{rel_md}: non-portable link path (use relative '/' path): {raw_target}")
                continue
            dest = (
                ((root / rel_md).parent / unquote(parsed.path)).resolve()
                if parsed.path
                else (root / rel_md).resolve()
            )
            if not dest.is_relative_to(root) or not dest.is_file():
                errors.append(f"{rel_md}: broken relative link -> {raw_target}")
                continue
            if parsed.fragment and dest.suffix.lower() == ".md":
                dest_rel = dest.relative_to(root).as_posix()
                frag = unquote(parsed.fragment)
                valid_anchors = get_anchors(dest_rel)
                if frag not in valid_anchors and frag.lower() not in valid_anchors:
                    errors.append(f"{rel_md}: missing anchor #{frag} in {dest_rel}")

    # 4. Validate requirements & traceability consistency.
    req_doc = md_docs.get("docs/requirements/product-requirements.md", "")
    defined_reqs = re.findall(r"^\|\s*`?((?:REQ-[A-Z]+|NFR)-\d+)`?\s*\|", req_doc, re.M)
    for rid, cnt in Counter(defined_reqs).items():
        if cnt > 1:
            errors.append(f"docs/requirements/product-requirements.md: duplicate ID {rid}")

    plan_doc = md_docs.get("docs/engineering/delivery-plan.md", "")
    defined_acs = set(re.findall(r"\bAC-\d+[a-z]?\b", plan_doc))

    trace_path = root / "docs/delivery/traceability.json"
    if trace_path.is_file():
        trace = load_json_object("docs/delivery/traceability.json")
        trace_items = trace.get("requirements")
        if not isinstance(trace_items, list) or not trace_items:
            errors.append("docs/delivery/traceability.json: 'requirements' must be a non-empty list")
            trace_items = []
        traced_ids: Counter[str] = Counter()
        req_set = set(defined_reqs)
        for item in trace_items:
            if not isinstance(item, dict):
                errors.append("docs/delivery/traceability.json: requirement entry must be an object")
                continue
            rid = str(item.get("id") or item.get("reqId") or "")
            traced_ids[rid] += 1
            if req_set and rid not in req_set:
                errors.append(f"traceability.json: unknown requirement ID {rid!r}")
            stage_val = item.get("stage") or item.get("milestone")
            if isinstance(stage_val, str) and stage_val not in STAGES:
                errors.append(f"traceability.json ({rid}): invalid stage {stage_val!r}")
            for path_field in ("decisions", "design", "designDocs", "modules", "moduleSpecs", "evidence"):
                field_val = item.get(path_field)
                if isinstance(field_val, list):
                    for ref_p in field_val:
                        safe_repo_file(str(ref_p), f"traceability.json ({rid}.{path_field})")
                elif isinstance(field_val, str) and field_val:
                    safe_repo_file(field_val, f"traceability.json ({rid}.{path_field})")
            ac_list = item.get("acceptance") or item.get("acceptanceCriteria") or []
            if defined_acs and isinstance(ac_list, list):
                for ac_id in ac_list:
                    if str(ac_id) not in defined_acs:
                        errors.append(f"traceability.json ({rid}): unknown acceptance ID {ac_id!r}")
        for missing_req in sorted(req_set - set(traced_ids.keys())):
            errors.append(f"traceability.json: untracked requirement {missing_req}")
        for dup_req, cnt in sorted(traced_ids.items()):
            if cnt > 1:
                errors.append(f"traceability.json: duplicate traced requirement {dup_req}")

    # 4b. Validate current stage cases manifest and GitHub workflow pinning.
    current_stage_str = str(registry.get("currentStage") or "M1").lower()
    stage_cases_rel = f"docs/delivery/{current_stage_str}-stage-cases.json"
    if (root / stage_cases_rel).is_file():
        stage_cases = load_json_object(stage_cases_rel)
        if stage_cases.get("schemaVersion") != 1:
            errors.append(f"{stage_cases_rel}: expected schemaVersion == 1")
        case_ids: Counter[str] = Counter()
        req_lookup = set(defined_reqs)
        for case_obj in stage_cases.get("cases", []):
            if not isinstance(case_obj, dict):
                errors.append(f"{stage_cases_rel}: case entry must be an object")
                continue
            cid = str(case_obj.get("id") or "")
            case_ids[cid] += 1
            for r_ref in case_obj.get("requirements", []):
                if req_lookup and str(r_ref) not in req_lookup:
                    errors.append(f"{stage_cases_rel} ({cid}): unknown requirement {r_ref!r}")
            for a_ref in case_obj.get("acceptance", []):
                if defined_acs and str(a_ref) not in defined_acs:
                    errors.append(f"{stage_cases_rel} ({cid}): unknown acceptance {a_ref!r}")
        for cid, cnt in sorted(case_ids.items()):
            if not cid or cnt > 1:
                errors.append(f"{stage_cases_rel}: invalid or duplicate case ID {cid!r}")

    workflows_dir = root / ".github" / "workflows"
    if workflows_dir.is_dir():
        for wf_file in sorted(workflows_dir.glob("*.y*ml")):
            wf_rel = wf_file.relative_to(root).as_posix()
            wf_text = wf_file.read_text(encoding="utf-8-sig")
            if not re.search(r"^permissions\s*:", wf_text, re.M):
                errors.append(f"{wf_rel}: must explicitly declare top-level 'permissions:'")
            for uses_match in re.finditer(r"^\s*-?\s*uses:\s*([^\s#]+)", wf_text, re.M):
                uses_ref = uses_match.group(1).strip("\"'")
                if not uses_ref.startswith("./") and not re.search(r"@[0-9a-f]{40}$", uses_ref):
                    errors.append(f"{wf_rel}: third-party action must pin full 40-hex commit SHA ({uses_ref})")

    # 5. Repository file & text hygiene scan.
    scanned_text_count = 0
    for rel, fpath in all_files:
        if rel in ALLOWED_PUBLIC_KEY_PEMS:
            try:
                pem_bytes = fpath.read_bytes()
                pem_text = pem_bytes.decode("utf-8")
            except (OSError, UnicodeDecodeError) as exc:
                errors.append(f"{rel}: failed to read UTF-8 public key PEM ({exc})")
            else:
                if pem_bytes.startswith(b"\xef\xbb\xbf"):
                    errors.append(f"{rel}: public key PEM must not contain UTF-8 BOM")
                if "\r" in pem_text:
                    errors.append(f"{rel}: public key PEM must use LF line endings")
                if "PRIVATE KEY" in pem_text.upper():
                    errors.append(f"{rel}: allowed public key PEM must NEVER contain a private key")
                if not (
                    pem_text.startswith("-----BEGIN PUBLIC KEY-----\n")
                    and pem_text.rstrip("\n").endswith("-----END PUBLIC KEY-----")
                ):
                    errors.append(f"{rel}: allowed PEM must be a strict SubjectPublicKeyInfo PUBLIC KEY block")
        else:
            for forbidden_pat in FORBIDDEN_FILE_PATTERNS:
                if forbidden_pat.search(rel):
                    errors.append(f"{rel}: forbidden sensitive, certificate, or database file in repository")

        suffix = fpath.suffix.lower()
        is_named_config = fpath.name in {".editorconfig", ".gitattributes", ".gitignore", "LICENSE"}
        if suffix not in TEXT_EXTENSIONS and not is_named_config:
            continue
        try:
            raw_content = fpath.read_text(encoding="utf-8-sig")
        except (OSError, UnicodeDecodeError):
            continue
        scanned_text_count += 1
        if rel == "scripts/check_docs.py":
            continue
        for label, pattern in HYGIENE_PATTERNS:
            found_match = pattern.search(raw_content)
            if found_match:
                line_no = raw_content.count("\n", 0, found_match.start()) + 1
                snippet = found_match.group(0)
                errors.append(f"{rel}:{line_no}: forbidden {label} ({snippet!r})")

    stats = {
        "markdown": len(md_docs),
        "json": len(governed_json_paths),
        "requirements": len(set(defined_reqs)),
        "scanned_files": scanned_text_count,
    }
    return errors, stats


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
        help="Repository root directory to validate.",
    )
    args = parser.parse_args()
    try:
        errors, stats = check_repository(args.root)
    except Exception as exc:  # pragma: no cover - defensive CLI guard
        print(f"FAIL: unexpected checker error: {exc}", file=sys.stderr)
        return 1

    if errors:
        for err in errors:
            print(f"FAIL: {err}", file=sys.stderr)
        print(
            f"\nSUMMARY: FAILED with {len(errors)} issue(s) across "
            f"{stats['markdown']} Markdown and {stats['json']} governed JSON files.",
            file=sys.stderr,
        )
        return 1

    print(
        f"PASS: {stats['markdown']} Markdown docs, {stats['json']} governed JSON files, "
        f"{stats['requirements']} requirements, {stats['scanned_files']} text files verified — "
        "registry, relative links, anchors, traceability, and path/repo hygiene clean."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
