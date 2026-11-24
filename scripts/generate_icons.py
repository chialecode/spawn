"""Deterministic zero-dependency icon generator for SPAWN and SPAWN Dev.

Rasterizes the canonical marks in a 1024-unit space (the same geometry as `assets/icon.svg` and
`assets/icon-dev.svg`, and `BrandIconGeometry` in Spawn.Host):
  - flat dark-gray rounded square  (48..976, rx 200, #3C4043)
  - white geometric "S"             (height 250..780, stroke 110, terminal corner radius 28)
  - Dev only: flat amber badge      (560,800 400x160, rx 52, #F3A115) with a #202124 "DEV" glyph
No gradients, borders or shadows. The mark is rendered once at 1024 x 1024 (2 x 2 samples per pixel)
and every output size is area-averaged down from that render:
  - assets/icon.png & assets/icon.ico         (Release channel)
  - assets/icon-dev.png & assets/icon-dev.ico (Dev channel)
Standard library only (Python 3.12+); the same script always produces the same bytes.
Run from the repository root:  python -B scripts/generate_icons.py
"""

from __future__ import annotations

import argparse
import math
from pathlib import Path
import struct
import sys
import zlib

UNITS = 1024
SUPERSAMPLE = 2
PNG_SIZE = 512
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)

BACKGROUND = (0x3C, 0x40, 0x43)
LETTER = (0xFF, 0xFF, 0xFF)
BADGE = (0xF3, 0xA1, 0x15)
BADGE_TEXT = (0x20, 0x21, 0x24)


def inside_rounded_rect(x: float, y: float, left: float, top: float, right: float, bottom: float, radius: float) -> bool:
    if not (left <= x <= right and top <= y <= bottom):
        return False
    cx = min(max(x, left + radius), right - radius)
    cy = min(max(y, top + radius), bottom - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius**2


def inside_right_rounded(x: float, y: float, left: float, top: float, right: float, bottom: float, radius: float) -> bool:
    """Rectangle whose two right-hand corners are rounded."""
    if not (left <= x <= right and top <= y <= bottom):
        return False
    cx = right - radius
    if x <= cx:
        return True
    cy = min(max(y, top + radius), bottom - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius**2


def inside_left_rounded(x: float, y: float, left: float, top: float, right: float, bottom: float, radius: float) -> bool:
    """Rectangle whose two left-hand corners are rounded."""
    if not (left <= x <= right and top <= y <= bottom):
        return False
    cx = left + radius
    if x >= cx:
        return True
    cy = min(max(y, top + radius), bottom - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius**2


def inside_polygon(x: float, y: float, points: list[tuple[float, float]]) -> bool:
    inside = False
    for (x1, y1), (x2, y2) in zip(points, points[1:] + points[:1]):
        if (y1 > y) != (y2 > y) and x < x1 + (y - y1) * (x2 - x1) / (y2 - y1):
            inside = not inside
    return inside


def inside_annulus_half(x: float, y: float, cx: float, cy: float, r_in: float, r_out: float, left_half: bool) -> bool:
    if left_half and x > cx:
        return False
    if not left_half and x < cx:
        return False
    d2 = (x - cx) ** 2 + (y - cy) ** 2
    return r_in**2 <= d2 <= r_out**2


def inside_letter_s(x: float, y: float) -> bool:
    """Geometric "S": top bar, upper-left bowl, middle bar, lower-right bowl, bottom bar (stroke 110)."""
    if not (272 <= x <= 752 and 250 <= y <= 780):
        return False
    return (
        inside_right_rounded(x, y, 432, 250, 752, 360, 28)  # top bar, rounded right terminal
        or inside_annulus_half(x, y, 432, 410, 50, 160, left_half=True)  # upper bowl
        or (432 <= x <= 592 and 460 <= y <= 570)  # middle bar
        or inside_annulus_half(x, y, 592, 620, 50, 160, left_half=False)  # lower bowl
        or inside_left_rounded(x, y, 272, 670, 592, 780, 28)  # bottom bar, rounded left terminal
    )


V_LETTER = [(796, 839), (815, 839), (840, 903), (865, 839), (884, 839), (850, 925), (830, 925)]


def inside_badge_text(x: float, y: float) -> bool:
    d = inside_right_rounded(x, y, 636, 839, 710, 925, 40) and not inside_right_rounded(x, y, 654, 857, 692, 907, 22)
    e = (726 <= x <= 744 and 839 <= y <= 925) or any(
        726 <= x <= right and top <= y <= bottom for right, top, bottom in ((782, 839, 857), (776, 873, 891), (782, 907, 925))
    )
    return d or e or inside_polygon(x, y, V_LETTER)


def sample_release(x: float, y: float) -> tuple[int, int, int] | None:
    if not inside_rounded_rect(x, y, 48, 48, 976, 976, 200):
        return None
    return LETTER if inside_letter_s(x, y) else BACKGROUND


def sample_dev(x: float, y: float) -> tuple[int, int, int] | None:
    if inside_rounded_rect(x, y, 560, 800, 960, 960, 52):
        return BADGE_TEXT if inside_badge_text(x, y) else BADGE
    return sample_release(x, y)


def _overlay_dev_badge_in_place(out: bytearray) -> None:
    """Overlay the 400 x 160 DEV badge region [560..960, 800..960] onto an existing release 1024x1024 RGBA buffer."""
    n = SUPERSAMPLE * SUPERSAMPLE
    offsets = [((sx + 0.5) / SUPERSAMPLE, (sy + 0.5) / SUPERSAMPLE) for sy in range(SUPERSAMPLE) for sx in range(SUPERSAMPLE)]
    for py in range(800, 960):
        row_base = py * UNITS * 4
        for px in range(560, 960):
            r = g = b = 0
            covered = 0
            for ox, oy in offsets:
                colour = sample_dev(px + ox, py + oy)
                if colour is not None:
                    covered += 1
                    r += colour[0]
                    g += colour[1]
                    b += colour[2]
            idx = row_base + px * 4
            if covered:
                out[idx] = round(r / covered)
                out[idx + 1] = round(g / covered)
                out[idx + 2] = round(b / covered)
                out[idx + 3] = round(255 * covered / n)
            else:
                out[idx : idx + 4] = b"\x00\x00\x00\x00"


def render_master_rgba(dev_badge: bool, _release_base: bytes | None = None) -> bytes:
    """Render the mark once at 1024 x 1024 with 2 x 2 samples per pixel (straight-alpha RGBA)."""
    if dev_badge and _release_base is not None:
        dev_out = bytearray(_release_base)
        _overlay_dev_badge_in_place(dev_out)
        return bytes(dev_out)

    out = bytearray(UNITS * UNITS * 4)
    n = SUPERSAMPLE * SUPERSAMPLE
    offsets = [((sx + 0.5) / SUPERSAMPLE, (sy + 0.5) / SUPERSAMPLE) for sy in range(SUPERSAMPLE) for sx in range(SUPERSAMPLE)]
    bg_pixel = bytes((*BACKGROUND, 255))
    bg_full_span = bg_pixel * (976 - 48)
    bg_mid_span = bg_pixel * (776 - 248)
    letter_lut = [
        bytes(
            (
                round((hits * LETTER[0] + (n - hits) * BACKGROUND[0]) / n),
                round((hits * LETTER[1] + (n - hits) * BACKGROUND[1]) / n),
                round((hits * LETTER[2] + (n - hits) * BACKGROUND[2]) / n),
                255,
            )
        )
        for hits in range(n + 1)
    ]

    # 1. Rounded-square background [48..976, 48..976], rx=200: interior spans are solid BACKGROUND,
    #    only the four 200x200 corner quadrants need 2x2 rounded-rect supersampling.
    for py in range(48, 976):
        row_base = py * UNITS * 4
        if 248 <= py < 776:
            out[row_base + 48 * 4 : row_base + 976 * 4] = bg_full_span
        else:
            out[row_base + 248 * 4 : row_base + 776 * 4] = bg_mid_span
            cy_corner = 248.0 if py < 248 else 776.0
            dy_near = min(abs(py + 0.25 - cy_corner), abs(py + 0.75 - cy_corner))
            dy_far = max(abs(py + 0.25 - cy_corner), abs(py + 0.75 - cy_corner))
            dy_near2 = dy_near * dy_near
            dy_far2 = dy_far * dy_far

            for x_start, x_end, cx_corner in ((48, 248, 248.0), (776, 976, 776.0)):
                for px in range(x_start, x_end):
                    dx_near = min(abs(px + 0.25 - cx_corner), abs(px + 0.75 - cx_corner))
                    if dx_near * dx_near + dy_near2 > 40000.0:
                        continue
                    idx = row_base + px * 4
                    dx_far = max(abs(px + 0.25 - cx_corner), abs(px + 0.75 - cx_corner))
                    if dx_far * dx_far + dy_far2 <= 40000.0:
                        out[idx : idx + 4] = bg_pixel
                        continue
                    covered = sum(1 for ox, oy in offsets if (px + ox - cx_corner) ** 2 + (py + oy - cy_corner) ** 2 <= 40000.0)
                    if covered:
                        out[idx] = BACKGROUND[0]
                        out[idx + 1] = BACKGROUND[1]
                        out[idx + 2] = BACKGROUND[2]
                        out[idx + 3] = round(255 * covered / n)

    # 2. Geometric "S" [272..752, 250..780]: strictly inside the opaque rounded-square interior.
    for py in range(250, 780):
        row_base = py * UNITS * 4
        for px in range(272, 752):
            hits = sum(1 for ox, oy in offsets if inside_letter_s(px + ox, py + oy))
            if hits:
                idx = row_base + px * 4
                out[idx : idx + 4] = letter_lut[hits]

    if dev_badge:
        _overlay_dev_badge_in_place(out)

    return bytes(out)


def downsample_box_rgba(src: bytes, src_size: int, dst_size: int) -> bytes:
    """Premultiplied area-average downsampler from src_size to dst_size."""
    if dst_size == src_size:
        return src
    out = bytearray(dst_size * dst_size * 4)

    if src_size % dst_size == 0:
        step = src_size // dst_size
        step_bytes = step * 4
        total_w = float(step * step)
        zero_span = b"\x00\x00\x00\x00" * step
        bg_pixel = bytes((*BACKGROUND, 255))
        bg_span = bg_pixel * step
        letter_pixel = bytes((*LETTER, 255))
        letter_span = letter_pixel * step

        for dy in range(dst_size):
            sy_start = dy * step
            sy_end = sy_start + step
            out_row = dy * dst_size * 4

            for dx in range(dst_size):
                sx_byte = dx * step_bytes
                first_span = src[sy_start * src_size * 4 + sx_byte : sy_start * src_size * 4 + sx_byte + step_bytes]
                if first_span == zero_span and all(
                    src[sy * src_size * 4 + sx_byte : sy * src_size * 4 + sx_byte + step_bytes] == zero_span
                    for sy in range(sy_start + 1, sy_end)
                ):
                    continue
                if first_span == bg_span and all(
                    src[sy * src_size * 4 + sx_byte : sy * src_size * 4 + sx_byte + step_bytes] == bg_span
                    for sy in range(sy_start + 1, sy_end)
                ):
                    out_idx = out_row + dx * 4
                    out[out_idx : out_idx + 4] = bg_pixel
                    continue
                if first_span == letter_span and all(
                    src[sy * src_size * 4 + sx_byte : sy * src_size * 4 + sx_byte + step_bytes] == letter_span
                    for sy in range(sy_start + 1, sy_end)
                ):
                    out_idx = out_row + dx * 4
                    out[out_idx : out_idx + 4] = letter_pixel
                    continue

                sum_pr = 0.0
                sum_pg = 0.0
                sum_pb = 0.0
                sum_a = 0.0
                for sy in range(sy_start, sy_end):
                    row_base = sy * src_size * 4 + sx_byte
                    for off in range(0, step_bytes, 4):
                        idx = row_base + off
                        a = src[idx + 3] / 255.0
                        if a > 0.0:
                            sum_pr += src[idx] * a
                            sum_pg += src[idx + 1] * a
                            sum_pb += src[idx + 2] * a
                            sum_a += a

                if sum_a > 1e-6:
                    out_a = sum_a / total_w
                    out_idx = out_row + dx * 4
                    out[out_idx] = round(min(255.0, max(0.0, sum_pr / sum_a)))
                    out[out_idx + 1] = round(min(255.0, max(0.0, sum_pg / sum_a)))
                    out[out_idx + 2] = round(min(255.0, max(0.0, sum_pb / sum_a)))
                    out[out_idx + 3] = round(min(255.0, max(0.0, out_a * 255.0)))

        return bytes(out)

    scale = src_size / dst_size

    for dy in range(dst_size):
        y0 = dy * scale
        y1 = (dy + 1) * scale
        sy_start = int(math.floor(y0))
        sy_end = min(src_size, int(math.ceil(y1)))

        for dx in range(dst_size):
            x0 = dx * scale
            x1 = (dx + 1) * scale
            sx_start = int(math.floor(x0))
            sx_end = min(src_size, int(math.ceil(x1)))

            sum_pr = 0.0
            sum_pg = 0.0
            sum_pb = 0.0
            sum_a = 0.0
            total_w = 0.0

            for sy in range(sy_start, sy_end):
                wy = min(sy + 1.0, y1) - max(float(sy), y0)
                if wy <= 0.0:
                    continue
                row_base = sy * src_size * 4
                for sx in range(sx_start, sx_end):
                    wx = min(sx + 1.0, x1) - max(float(sx), x0)
                    if wx <= 0.0:
                        continue
                    w = wx * wy
                    idx = row_base + sx * 4
                    a = src[idx + 3] / 255.0
                    sum_pr += src[idx] * a * w
                    sum_pg += src[idx + 1] * a * w
                    sum_pb += src[idx + 2] * a * w
                    sum_a += a * w
                    total_w += w

            if total_w > 0.0 and sum_a > 1e-6:
                out_a = sum_a / total_w
                out_idx = (dy * dst_size + dx) * 4
                out[out_idx] = round(min(255.0, max(0.0, sum_pr / sum_a)))
                out[out_idx + 1] = round(min(255.0, max(0.0, sum_pg / sum_a)))
                out[out_idx + 2] = round(min(255.0, max(0.0, sum_pb / sum_a)))
                out[out_idx + 3] = round(min(255.0, max(0.0, out_a * 255.0)))

    return bytes(out)


def encode_png_rgba(rgba: bytes, width: int, height: int) -> bytes:
    def chunk(tag: bytes, data: bytes) -> bytes:
        crc = zlib.crc32(tag)
        crc = zlib.crc32(data, crc) & 0xFFFFFFFF
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", crc)

    stride = width * 4
    raw_rows = bytearray()
    for y in range(height):
        raw_rows.append(0)  # Filter type 0 (None)
        raw_rows.extend(rgba[y * stride : (y + 1) * stride])

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    compressed = zlib.compress(bytes(raw_rows), level=9)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", compressed) + chunk(b"IEND", b"")


def encode_ico(master_rgba: bytes, master_size: int, sizes: tuple[int, ...] = ICO_SIZES) -> bytes:
    frames: list[tuple[int, bytes]] = []
    for sz in sizes:
        scaled = downsample_box_rgba(master_rgba, master_size, sz)
        frames.append((sz, encode_png_rgba(scaled, sz, sz)))

    header = struct.pack("<HHH", 0, 1, len(frames))
    dir_entries = bytearray()
    payloads = bytearray()
    offset = 6 + 16 * len(frames)

    for sz, png_bytes in frames:
        dim_byte = 0 if sz >= 256 else sz
        dir_entries.extend(
            struct.pack(
                "<BBBBHHII",
                dim_byte,
                dim_byte,
                0,
                0,
                1,
                32,
                len(png_bytes),
                offset,
            )
        )
        payloads.extend(png_bytes)
        offset += len(png_bytes)

    return header + bytes(dir_entries) + bytes(payloads)


def render_expected_icon_files(dev_badge: bool, release_base: bytes | None = None) -> tuple[bytes, bytes, bytes]:
    rgba = render_master_rgba(dev_badge=dev_badge, _release_base=release_base)
    png_bytes = encode_png_rgba(downsample_box_rgba(rgba, UNITS, PNG_SIZE), PNG_SIZE, PNG_SIZE)
    ico_bytes = encode_ico(rgba, UNITS, ICO_SIZES)
    return rgba, png_bytes, ico_bytes


def generate_all_icons(assets_dir: Path) -> None:
    assets_dir.mkdir(parents=True, exist_ok=True)
    release_master: bytes | None = None
    for is_dev, stem in ((False, "icon"), (True, "icon-dev")):
        rgba, png_bytes, ico_bytes = render_expected_icon_files(dev_badge=is_dev, release_base=release_master)
        if not is_dev:
            release_master = rgba
        png_path = assets_dir / f"{stem}.png"
        ico_path = assets_dir / f"{stem}.ico"
        png_path.write_bytes(png_bytes)
        ico_path.write_bytes(ico_bytes)
        print(f"[icons] wrote {png_path.name} ({png_path.stat().st_size} B) and {ico_path.name} ({ico_path.stat().st_size} B)")


def check_all_icons(assets_dir: Path) -> bool:
    required = [assets_dir / name for name in ("icon.png", "icon.ico", "icon-dev.png", "icon-dev.ico")]
    missing = [f"missing {p.name}" for p in required if not p.is_file()]
    if missing:
        for item in missing:
            print(f"[icons] check failed: {item}", file=sys.stderr)
        return False

    mismatches: list[str] = []
    release_master: bytes | None = None
    for is_dev, stem in ((False, "icon"), (True, "icon-dev")):
        rgba, expected_png, expected_ico = render_expected_icon_files(dev_badge=is_dev, release_base=release_master)
        if not is_dev:
            release_master = rgba
        for path, expected in ((assets_dir / f"{stem}.png", expected_png), (assets_dir / f"{stem}.ico", expected_ico)):
            actual = path.read_bytes()
            if actual != expected:
                mismatches.append(f"{path.name} differs (expected {len(expected)} B, got {len(actual)} B)")

    if mismatches:
        for item in mismatches:
            print(f"[icons] check failed: {item}", file=sys.stderr)
        return False

    print("[icons] check passed: icon.png, icon.ico, icon-dev.png, and icon-dev.ico match generator output.")
    return True


def main(argv: list[str] | None = None) -> int:
    repo_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description="Generate or verify canonical brand icon assets.")
    parser.add_argument(
        "--check",
        action="store_true",
        help="Verify committed PNG/ICO assets match deterministic generator output without modifying files.",
    )
    parser.add_argument(
        "--assets-dir",
        type=Path,
        default=repo_root / "assets",
        help="Target directory containing icon(.png|.ico) and icon-dev(.png|.ico).",
    )
    args = parser.parse_args(argv)
    if args.check:
        return 0 if check_all_icons(args.assets_dir) else 1

    generate_all_icons(args.assets_dir)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
