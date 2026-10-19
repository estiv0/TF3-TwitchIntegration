"""Extracts the icons from the game files into web/img/ (PNG). The artwork belongs to Urban Games, so it isn't
in the repository - run the script once after cloning, after game updates and before deploying the website.

- web/img/vehicles/<model>.png - vehicle side view: <model>_icon_small@2x.tga from the vehicle packages
  (base/content/vehicle/**.zip, dlcs/*/content/vehicle.zip), at most MAX_WIDTH px wide.
  <model> is the .mdl file name without extension - the mod sends the same name in models[id].icon
  (lua_mod: collectors.vehicleDetails), so the website builds the address img/vehicles/<icon>.png.
- web/img/resources/<cargo>.png - cargo icons: <cargo>/<cargo>@2x.tga from base/content/cargos/<cargo>.zip.
- web/img/stations/<name>.png - interface icons (stations, carriers): gui/hud/icons/*@2x.tga
  from base/content/gui.zip.

No dependencies (TGA and PNG handled by hand). Game folder: an argument, the TF3_GAME_DIR variable or
the default Steam library:
    python tools/extract_game_icons.py ["D:/SteamLibrary/steamapps/common/Transport Fever 3"]
"""

import os
import struct
import sys
import zipfile
import zlib
from pathlib import Path

DEFAULT_GAME = "C:/Program Files (x86)/Steam/steamapps/common/Transport Fever 3"
GAME = Path(sys.argv[1] if len(sys.argv) > 1 else os.environ.get("TF3_GAME_DIR") or DEFAULT_GAME)
IMG = Path(__file__).resolve().parent.parent / "web" / "img"
VEHICLE_SUFFIX = "_icon_small@2x.tga"
GUI_ICONS = "gui/hud/icons/"
MAX_WIDTH = 512  # wider ones (long consists, ships) are scaled down to this width, keeping the aspect ratio


def read_tga(data):
    """True-color TGA (type 2/10, 24/32 bpp) or grayscale (type 3/11, 8 bpp), with or without RLE
    -> (w, h, RGBA rows top-down). Grayscale ones are interface icon masks: white with alpha = brightness."""
    id_len, cmap_type, img_type = data[0], data[1], data[2]
    w, h = struct.unpack("<HH", data[12:16])
    bpp, desc = data[16], data[17]
    gray = img_type in (3, 11)
    if cmap_type != 0 or not ((img_type in (2, 10) and bpp in (24, 32)) or (gray and bpp == 8)):
        raise ValueError(f"unsupported TGA: type {img_type}, {bpp} bpp")
    px = bpp // 8
    pos = 18 + id_len
    if img_type in (2, 3):
        raw = data[pos:pos + w * h * px]
    else:
        out = bytearray()
        while len(out) < w * h * px:
            head = data[pos]
            pos += 1
            count = (head & 0x7F) + 1
            if head & 0x80:
                out += data[pos:pos + px] * count
                pos += px
            else:
                out += data[pos:pos + count * px]
                pos += count * px
        raw = bytes(out)
    rows = []
    for y in range(h):
        src = raw[y * w * px:(y + 1) * w * px]
        row = bytearray(w * 4)
        if gray:
            row[0::4] = row[1::4] = row[2::4] = b"\xff" * w
            row[3::4] = src
        else:
            row[0::4], row[1::4], row[2::4] = src[2::px], src[1::px], src[0::px]  # BGR(A) -> RGB(A)
            row[3::4] = src[3::px] if px == 4 else b"\xff" * w
        rows.append(bytes(row))
    if not desc & 0x20:  # origin in the bottom left corner
        rows.reverse()
    return w, h, rows


def resample_axis(lines, n_in, n_out):
    """Shrinks each line (a list of [r, g, b, a] pixels with premultiplied alpha) from n_in to n_out
    pixels: an average weighted by coverage (box filter), without dark rims at the edges."""
    scale = n_in / n_out
    spans = []  # for each output pixel: [(input index, weight)]
    for o in range(n_out):
        lo, hi = o * scale, (o + 1) * scale
        span, i = [], int(lo)
        while i < hi and i < n_in:
            w = min(hi, i + 1) - max(lo, i)
            if w > 0:
                span.append((i, w / scale))
            i += 1
        spans.append(span)
    return [[[sum(line[i][c] * w for i, w in span) for c in range(4)] for span in spans] for line in lines]


def downscale(w, h, rows, max_w):
    """Shrinks an image wider than max_w to that width, keeping the aspect ratio."""
    if w <= max_w:
        return w, h, rows
    nw, nh = max_w, max(1, round(h * max_w / w))
    # premultiplied alpha: transparent pixels contribute no color
    px = [[[row[i] * row[i + 3] / 255, row[i + 1] * row[i + 3] / 255, row[i + 2] * row[i + 3] / 255, row[i + 3]]
           for i in range(0, w * 4, 4)] for row in rows]
    px = resample_axis(px, w, nw)                                   # horizontally
    cols = resample_axis([list(c) for c in zip(*px)], h, nh)        # vertically (on columns)
    out = []
    for y in range(nh):
        row = bytearray(nw * 4)
        for x in range(nw):
            r, g, b, a = cols[x][y]
            k = 255 / a if a > 0 else 0
            row[x * 4:x * 4 + 4] = bytes(min(255, round(v)) for v in (r * k, g * k, b * k, a))
        out.append(bytes(row))
    return nw, nh, out


def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    return a if pa <= pb and pa <= pc else b if pb <= pc else c


def filter_row(kind, row, prev):
    """PNG filter for an RGBA row (4 bytes per pixel)."""
    out = bytearray(len(row))
    for i, x in enumerate(row):
        a = row[i - 4] if i >= 4 else 0
        b = prev[i]
        c = prev[i - 4] if i >= 4 else 0
        pred = (0, a, b, (a + b) >> 1, paeth(a, b, c))[kind]
        out[i] = (x - pred) & 0xFF
    return out


def write_png(path, w, h, rows):
    # the filter chosen per row (smallest sum of absolute values) - like libpng, smaller files
    prev = bytes(w * 4)
    raw = bytearray()
    for row in rows:
        kind, best = min(((k, filter_row(k, row, prev)) for k in range(5)),
                         key=lambda kf: sum(v if v < 128 else 256 - v for v in kf[1]))
        raw.append(kind)
        raw += best
        prev = row

    def chunk(tag, body):
        return struct.pack(">I", len(body)) + tag + body + struct.pack(">I", zlib.crc32(tag + body))

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")
    path.write_bytes(png)


def vehicle_icons():
    """(name, zip, file in the zip) of vehicle icons; the same model in several packages - the first one wins."""
    zips = [*sorted((GAME / "base" / "content" / "vehicle").rglob("*.zip")),
            *sorted(GAME.glob("dlcs/*/content/vehicle.zip"))]
    for z in zips:
        with zipfile.ZipFile(z) as zf:
            for name in zf.namelist():
                if name.endswith(VEHICLE_SUFFIX):
                    yield name.rsplit("/", 1)[-1][:-len(VEHICLE_SUFFIX)], z, name


def cargo_icons():
    for z in sorted((GAME / "base" / "content" / "cargos").glob("*.zip")):
        name = f"{z.stem}/{z.stem}@2x.tga"
        with zipfile.ZipFile(z) as zf:
            if name in zf.namelist():  # cargos/ also has shared packages (classes, icons, ...) without an icon
                yield z.stem, z, name


def gui_icons():
    z = GAME / "base" / "content" / "gui.zip"
    with zipfile.ZipFile(z) as zf:
        for name in zf.namelist():
            rest = name[len(GUI_ICONS):]
            if name.startswith(GUI_ICONS) and "/" not in rest and rest.endswith("@2x.tga"):
                yield rest[:-len("@2x.tga")], z, name


def extract(folder, icons, max_width=None):
    out = IMG / folder
    out.mkdir(parents=True, exist_ok=True)
    done, failed = set(), []
    for key, z, name in icons:
        if key in done:
            continue
        try:
            with zipfile.ZipFile(z) as zf:
                w, h, rows = read_tga(zf.read(name))
            if max_width:
                w, h, rows = downscale(w, h, rows, max_width)
            write_png(out / f"{key}.png", w, h, rows)
            done.add(key)
        except ValueError as e:
            failed.append(f"{z.name}:{name}: {e}")
    size = sum(p.stat().st_size for p in out.glob("*.png"))
    print(f"{len(done)} icons in {out} ({size / 1e6:.1f} MB)")
    for f in failed:
        print("skipped", f)


def main():
    if not (GAME / "base" / "content").is_dir():
        sys.exit(f"No game in {GAME} - give the Transport Fever 3 folder as an argument or in TF3_GAME_DIR.")
    extract("stations", gui_icons())
    extract("resources", cargo_icons())
    extract("vehicles", vehicle_icons(), MAX_WIDTH)


if __name__ == "__main__":
    main()
