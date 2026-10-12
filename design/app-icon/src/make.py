"""Собирает варианты значка: SVG, ICO со всеми размерами и листы для выбора.

    python3 design/app-icon/src/make.py [папка]      # по умолчанию design/app-icon

Нужны Python-пакеты skia-pathops, numpy и Pillow и Node.js с playwright (Chromium рисует SVG в PNG).

Кадры ICO рисуются из вектора под каждый размер, а не ужимаются из 256: мелкие — упрощённым
рисунком (уровни детализации — в fox_icons.py).
"""
from __future__ import annotations

import io
import json
import os
import struct
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from PIL import Image  # noqa: E402

import sheet  # noqa: E402
from fox_icons import DESIGNS, FULL, S, XS, draw  # noqa: E402

CANVAS = 1024
MARGIN = 64  # поле со всех четырёх сторон: рамка лиса квадратная, поэтому поля равны (на 256 px — 16 px)
ICO_SIZES = [256, 128, 96, 64, 48, 40, 32, 24, 20, 16]


def detail_for(size):
    if size <= 20:
        return XS
    if size <= 32:
        return S
    return FULL


def mark(key, detail):
    full = draw(key, FULL).bounds()
    w, h = full[2] - full[0], full[3] - full[1]
    # Поля равны со всех сторон, только пока рамка рисунка квадратная: правка пропорций в
    # fox_icons.py, которая её ломает, должна остановить сборку, а не тихо дать неровные поля.
    assert abs(w - h) < 1, f"рамка лиса {key} не квадратная: {w:.1f}x{h:.1f}"
    return draw(key, detail).fit((MARGIN, MARGIN, CANVAS - MARGIN, CANVAS - MARGIN), bounds=full)


def bmp_frame(img: Image.Image) -> bytes:
    """32-битный DIB для ICO: BITMAPINFOHEADER высотой 2h, строки снизу вверх, маска AND."""
    w, h = img.size
    px = img.convert("RGBA").load()
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = bytearray()
    mask = bytearray()
    stride = ((w + 31) // 32) * 4
    for y in range(h - 1, -1, -1):
        bits = bytearray(stride)
        for x in range(w):
            r, g, b, a = px[x, y]
            rows += bytes((b, g, r, a))
            if a == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        mask += bits
    return header + bytes(rows) + bytes(mask)


def png_bytes(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path, frames: dict[int, Image.Image]):
    """Крупные кадры — PNG (сжаты), мелкие — BMP: так значок понимают и Проводник, и WPF.

    Кадры идут от крупного к мелкому: WPF, получив .ico в Image.Source, берёт первый кадр.
    """
    sizes = sorted(frames, reverse=True)
    blobs = [png_bytes(frames[s]) if s >= 64 else bmp_frame(frames[s]) for s in sizes]
    head = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, blob in zip(sizes, blobs):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(head + entries + b"".join(blobs))


def main(out):
    out = os.path.abspath(out)
    os.makedirs(out, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        jobs, frames = [], {}
        for key, d in DESIGNS.items():
            svgs = {}
            for det in (FULL, S, XS):
                path = os.path.join(out if det == FULL else work, f"fox-{key}" + ("" if det == FULL else f"-{det}") + ".svg")
                with open(path, "w") as f:
                    f.write(mark(key, det).svg(title=f"Amarin Admin AI: {d['name']}"))
                svgs[det] = path
            for s in ICO_SIZES:
                png = os.path.join(work, f"fox-{key}-{s}.png")
                jobs.append({"svg": svgs[detail_for(s)], "out": png, "size": s})
                frames.setdefault(key, {})[s] = png
        with open(os.path.join(work, "frames.json"), "w") as f:
            json.dump(jobs, f)
        subprocess.run(["node", os.path.join(HERE, "render.mjs"), os.path.join(work, "frames.json")], check=True)

        for key, pngs in frames.items():
            write_ico(os.path.join(out, f"fox-{key}.ico"), {s: Image.open(p).convert("RGBA") for s, p in pngs.items()})

        sheet.overview(out, work)
        sheet.preview(out, work, MARGIN, CANVAS)
    print("ok:", out)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(HERE))
