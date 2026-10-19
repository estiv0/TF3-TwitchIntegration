"""Wyciąga ikonki z plików gry do web/img/ (PNG). Grafiki należą do Urban Games, więc nie ma ich
w repozytorium - uruchom skrypt raz po sklonowaniu, po aktualizacji gry i przed wgraniem strony.

- web/img/vehicles/<model>.png - z boku pojazdu: <model>_icon_small@2x.tga z paczek pojazdów
  (base/content/vehicle/**.zip, dlcs/*/content/vehicle.zip), najwyżej MAX_WIDTH px szerokości.
  <model> to nazwa pliku .mdl bez rozszerzenia - tę samą nazwę mod wysyła w models[id].icon
  (lua_mod: collectors.vehicleDetails), więc strona składa adres img/vehicles/<icon>.png.
- web/img/resources/<towar>.png - ikonki towarów: <towar>/<towar>@2x.tga z base/content/cargos/<towar>.zip.
- web/img/stations/<nazwa>.png - ikonki interfejsu (stacje, środki transportu): gui/hud/icons/*@2x.tga
  z base/content/gui.zip.

Bez zależności (TGA i PNG obsłużone ręcznie). Folder gry: argument, zmienna TF3_GAME_DIR albo
domyślna biblioteka Steam:
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
MAX_WIDTH = 512  # szersze (długie składy, statki) zmniejszamy do tej szerokości, z zachowaniem proporcji


def read_tga(data):
    """TGA true-color (typ 2/10, 24/32 bpp) albo szary (typ 3/11, 8 bpp), bez albo z RLE
    -> (w, h, wiersze RGBA od góry). Szare to maski ikonek interfejsu: biały z alfą = jasność."""
    id_len, cmap_type, img_type = data[0], data[1], data[2]
    w, h = struct.unpack("<HH", data[12:16])
    bpp, desc = data[16], data[17]
    gray = img_type in (3, 11)
    if cmap_type != 0 or not ((img_type in (2, 10) and bpp in (24, 32)) or (gray and bpp == 8)):
        raise ValueError(f"nieobsługiwany TGA: typ {img_type}, {bpp} bpp")
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


def vehicle_icons():
    """(nazwa, zip, plik w zipie) ikonek pojazdów; ten sam model w kilku paczkach - pierwszy wygrywa."""
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
            if name in zf.namelist():  # cargos/ ma też paczki wspólne (classes, icons, ...) bez ikonki
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
    print(f"{len(done)} ikonek w {out} ({size / 1e6:.1f} MB)")
    for f in failed:
        print("pominięto", f)


def main():
    if not (GAME / "base" / "content").is_dir():
        sys.exit(f"Nie ma gry w {GAME} - podaj folder Transport Fever 3 jako argument albo w TF3_GAME_DIR.")
    extract("stations", gui_icons())
    extract("resources", cargo_icons())
    extract("vehicles", vehicle_icons(), MAX_WIDTH)


if __name__ == "__main__":
    main()
