"""Wyciąga ikonki pojazdów z plików gry do web/img/vehicles/ (PNG).

Każdy model pojazdu ma w swojej paczce (base/content/vehicle/**.zip, dlcs/*/content/vehicle.zip)
ikonkę z boku <model>_icon_small@2x.tga (różne wymiary, do 3032 px szerokości). Zapisujemy ją jako
<model>.png (najwyżej MAX_WIDTH px szerokości, z zachowaniem proporcji), gdzie
<model> to nazwa pliku .mdl bez rozszerzenia - tę samą nazwę mod wysyła w models[id].icon
(lua_mod: collectors.vehicleDetails), więc strona składa adres img/vehicles/<icon>.png.

Bez zależności (TGA i PNG obsłużone ręcznie). Uruchom ponownie po aktualizacji gry:
    python tools/extract_vehicle_icons.py ["E:/SteamLibrary/steamapps/common/Transport Fever 3"]
"""

import struct
import sys
import zipfile
import zlib
from pathlib import Path

GAME = Path(sys.argv[1] if len(sys.argv) > 1 else "E:/SteamLibrary/steamapps/common/Transport Fever 3")
OUT = Path(__file__).resolve().parent.parent / "web" / "img" / "vehicles"
SUFFIX = "_icon_small@2x.tga"
MAX_WIDTH = 512  # szersze (długie składy, statki) zmniejszamy do tej szerokości, z zachowaniem proporcji


def read_tga(data):
    """TGA true-color (typ 2) albo RLE (typ 10), 24/32 bpp -> (w, h, wiersze RGBA od góry)."""
    id_len, cmap_type, img_type = data[0], data[1], data[2]
    w, h = struct.unpack("<HH", data[12:16])
    bpp, desc = data[16], data[17]
    if cmap_type != 0 or img_type not in (2, 10) or bpp not in (24, 32):
        raise ValueError(f"nieobsługiwany TGA: typ {img_type}, {bpp} bpp")
    px = bpp // 8
    pos = 18 + id_len
    if img_type == 2:
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
        row[0::4], row[1::4], row[2::4] = src[2::px], src[1::px], src[0::px]  # BGR(A) -> RGB(A)
        row[3::4] = src[3::px] if px == 4 else b"\xff" * w
        rows.append(bytes(row))
    if not desc & 0x20:  # początek w lewym dolnym rogu
        rows.reverse()
    return w, h, rows


def resample_axis(lines, n_in, n_out):
    """Zmniejsza każdą linię (listę pikseli [r, g, b, a] z alfą przemnożoną) z n_in do n_out
    pikseli: średnia ważona pokryciem (filtr pudełkowy), bez ciemnych obwódek na krawędziach."""
    scale = n_in / n_out
    spans = []  # dla piksela wyjściowego: [(indeks wejściowy, waga)]
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
    """Zmniejsza obrazek szerszy niż max_w do tej szerokości z zachowaniem proporcji."""
    if w <= max_w:
        return w, h, rows
    nw, nh = max_w, max(1, round(h * max_w / w))
    # alfa przemnożona: przezroczyste piksele nie wnoszą koloru
    px = [[[row[i] * row[i + 3] / 255, row[i + 1] * row[i + 3] / 255, row[i + 2] * row[i + 3] / 255, row[i + 3]]
           for i in range(0, w * 4, 4)] for row in rows]
    px = resample_axis(px, w, nw)                                   # w poziomie
    cols = resample_axis([list(c) for c in zip(*px)], h, nh)        # w pionie (na kolumnach)
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
    """Filtr PNG dla wiersza RGBA (4 bajty na piksel)."""
    out = bytearray(len(row))
    for i, x in enumerate(row):
        a = row[i - 4] if i >= 4 else 0
        b = prev[i]
        c = prev[i - 4] if i >= 4 else 0
        pred = (0, a, b, (a + b) >> 1, paeth(a, b, c))[kind]
        out[i] = (x - pred) & 0xFF
    return out


def write_png(path, w, h, rows):
    # filtr wybierany per wiersz (najmniejsza suma wartości bezwzględnych) - jak libpng, mniejsze pliki
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


def vehicle_zips():
    yield from sorted((GAME / "base" / "content" / "vehicle").rglob("*.zip"))
    yield from sorted(GAME.glob("dlcs/*/content/vehicle.zip"))


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    done, failed = set(), []
    for z in vehicle_zips():
        with zipfile.ZipFile(z) as zf:
            for name in zf.namelist():
                if not name.endswith(SUFFIX):
                    continue
                model = name.rsplit("/", 1)[-1][:-len(SUFFIX)]
                if model in done:
                    continue
                try:
                    write_png(OUT / f"{model}.png", *downscale(*read_tga(zf.read(name)), MAX_WIDTH))
                    done.add(model)
                except ValueError as e:
                    failed.append(f"{z.name}:{name}: {e}")
    size = sum(p.stat().st_size for p in OUT.glob("*.png"))
    print(f"{len(done)} ikonek w {OUT} ({size / 1e6:.1f} MB)")
    for f in failed:
        print("pominięto", f)


if __name__ == "__main__":
    main()
