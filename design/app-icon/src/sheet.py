"""Листы для выбора варианта: overview.png (с одного взгляда) и preview.png (подробно)."""
from __future__ import annotations

import json
import os
import subprocess

from foxlib import Art, tile
from fox_icons import DESIGNS, FULL

HERE = os.path.dirname(os.path.abspath(__file__))
SMALL = [64, 48, 32, 24, 20, 16]


def img(path, size):
    return f'<img src="file://{os.path.abspath(path)}" width="{size}" height="{size}">'


def render_html(html, out_png, work, width, scale):
    page = os.path.join(work, os.path.basename(out_png)[:-4] + ".html")
    with open(page, "w") as f:
        f.write(html)
    jobs = os.path.join(work, os.path.basename(out_png)[:-4] + ".jobs.json")
    with open(jobs, "w") as f:
        json.dump([{"html": page, "out": out_png, "width": width, "height": 600, "scale": scale}], f)
    subprocess.run(["node", os.path.join(HERE, "render.mjs"), jobs], check=True)


def overview(out, work):
    """Три плитки и под каждой тот же лис без подложки, на светлой и тёмной теме программы."""
    cols = []
    for (key, d), letter in zip(DESIGNS.items(), "ABC"):
        mark = os.path.join(out, f"fox-{key}-mark.svg")
        cols.append(f"""
  <div class="col">
    {img(os.path.join(out, f"fox-{key}-tile.svg"), 300)}
    <div class="name"><span class="badge">{letter}</span>«{d['name']}»</div>
    <div class="about">{d['about']}</div>
    <div class="marks"><div class="m light">{img(mark, 112)}</div><div class="m dark">{img(mark, 112)}</div></div>
  </div>""")
    html = f"""<!doctype html><html lang="ru"><head><meta charset="utf-8"><style>
  body{{margin:0;padding:30px 34px;background:#f6f3f2;font:15px/1.4 "Segoe UI",system-ui,sans-serif;color:#1d1b1c;width:1080px}}
  h1{{font-size:25px;margin:0 0 4px}} .sub{{color:#6f6a6c;margin:0 0 22px}}
  .row{{display:flex;gap:30px}}
  .col{{width:340px;background:#fff;border:1px solid #e7e2e1;border-radius:20px;padding:20px;display:flex;flex-direction:column;align-items:center}}
  .name{{font-weight:700;font-size:19px;margin-top:10px;display:flex;gap:8px;align-items:center}}
  .badge{{width:26px;height:26px;border-radius:8px;background:#E5383B;color:#fff;font-size:15px;display:grid;place-items:center}}
  .about{{color:#6f6a6c;text-align:center;font-size:13.5px;min-height:38px}}
  .marks{{display:flex;gap:10px;margin-top:12px}}
  .m{{width:140px;height:140px;border-radius:14px;display:grid;place-items:center}}
  .light{{background:#EBEBEC}} .dark{{background:#131313}}
  .foot{{color:#6f6a6c;font-size:13px;margin-top:16px}}
</style></head><body>
  <h1>Лис для Amarin Admin AI: три варианта</h1>
  <p class="sub">Сверху значок-плитка в духе iOS (углы прозрачные), снизу тот же лис без подложки, на светлой и тёмной теме программы.</p>
  <div class="row">{''.join(cols)}</div>
  <p class="foot">Поля одинаковые: плитка отступает от краёв на 40/1024 со всех сторон, лис внутри стоит строго по центру.</p>
</body></html>"""
    render_html(html, os.path.join(out, "overview.png"), work, 1148, 2)


def guides_svg(path, margin, canvas, key="a"):
    """Плитка с направляющими: поле вокруг плитки и поля вокруг лиса."""
    d = DESIGNS[key]
    art = tile(d["tile_art"](FULL), d["tile_bg"], canvas=canvas, margin=margin, pad=d["tile_pad"])
    x0, y0, x1, y1 = Art(art.layers[1:]).bounds()
    body = art.svg()
    body = body[: body.rindex("</svg>")]
    m, c = margin, canvas / 2
    dash = 'fill="none" stroke-width="5" stroke-dasharray="18 12"'
    g = [
        f'<rect x="2.5" y="2.5" width="{canvas - 5}" height="{canvas - 5}" {dash} stroke="#9a9496"/>',
        f'<rect x="{m}" y="{m}" width="{canvas - 2 * m}" height="{canvas - 2 * m}" {dash} stroke="#2F7DF6"/>',
        f'<rect x="{x0:.1f}" y="{y0:.1f}" width="{x1 - x0:.1f}" height="{y1 - y0:.1f}" {dash} stroke="#FFD34D"/>',
    ]
    for ax, ay, bx, by, label in (
        (m, c, x0, c, x0 - m), (x1, c, canvas - m, c, canvas - m - x1),
        (c, m, c, y0, y0 - m), (c, y1, c, canvas - m, canvas - m - y1),
    ):
        g.append(f'<line x1="{ax:.1f}" y1="{ay:.1f}" x2="{bx:.1f}" y2="{by:.1f}" stroke="#FFD34D" stroke-width="6"/>')
        g.append(f'<text x="{(ax + bx) / 2:.1f}" y="{(ay + by) / 2 - 14:.1f}" font-size="44" font-family="Segoe UI,Arial" '
                 f'font-weight="700" fill="#FFD34D" text-anchor="middle">{label:.0f}</text>')
    for lx, ly in ((m / 2, c + 120), (canvas - m / 2, c + 120), (c + 120, m / 2 + 14), (c + 120, canvas - m / 2 + 14)):
        g.append(f'<text x="{lx:.1f}" y="{ly:.1f}" font-size="34" font-family="Segoe UI,Arial" font-weight="700" '
                 f'fill="#2F7DF6" text-anchor="middle">{m}</text>')
    with open(path, "w") as f:
        f.write(body + "\n  " + "\n  ".join(g) + "\n</svg>\n")


def _card(out, work, key, d, letter):
    def sv(name):
        return os.path.join(out, name)

    def fr(form, s):
        return os.path.join(work, f"fox-{key}-{form}-{s}.png")

    def strip(form):
        return "".join(f'<div class="sz">{img(fr(form, s), s)}<span>{s}</span></div>' for s in SMALL)

    return f"""
<section class="card">
  <div class="head"><div class="letter">{letter}</div><div><h2>«{d['name']}»</h2><p>{d['about']}</p></div></div>
  <div class="row">
    <div class="hero">{img(sv(f"fox-{key}-tile.svg"), 240)}<span class="cap">значок-плитка, как в iOS</span></div>
    <div class="marks">
      <div class="pane light">{img(sv(f"fox-{key}-mark.svg"), 150)}</div>
      <div class="pane dark">{img(sv(f"fox-{key}-mark.svg"), 150)}</div>
      <span class="cap">знак без подложки: для самой программы</span>
    </div>
    <div class="alt">{img(sv(f"fox-{key}-tile-alt.svg"), 150)}<span class="cap">другой фон</span></div>
    <div class="explorer"><div class="ex-file">{img(fr("tile", 96), 96)}<span>Amarin Admin AI.exe</span></div>
      <span class="cap">в Проводнике, 96 px</span></div>
  </div>
  <div class="row small">
    <div class="strip light"><b>плитка</b>{strip("tile")}</div>
    <div class="strip dark"><b>плитка</b>{strip("tile")}</div>
  </div>
  <div class="row small">
    <div class="strip light"><b>знак</b>{strip("mark")}</div>
    <div class="strip dark"><b>знак</b>{strip("mark")}</div>
  </div>
  <div class="row small">
    <div class="app dark-app"><div class="logo-btn">{img(fr("mark", 20), 20)}</div><div class="newchat">＋ Новый чат</div>
      <div class="side-cap">тёмная тема, 20 px</div></div>
    <div class="app light-app"><div class="logo-btn">{img(fr("mark", 20), 20)}</div><div class="newchat">＋ Новый чат</div>
      <div class="side-cap">светлая тема, 20 px</div></div>
    <div class="taskbar"><div class="tb-ico">{img(fr("tile", 24), 24)}</div><div class="tb-ico">{img(fr("mark", 24), 24)}</div>
      <div class="tb-cap">панель задач, 24 px: плитка и знак</div></div>
  </div>
</section>"""


def preview(out, work, margin, canvas):
    """Подробный лист: крупно, мелкие размеры как есть (1:1), вид в программе и сетка отступов."""
    guides = os.path.join(work, "guides.svg")
    guides_svg(guides, margin, canvas)
    cards = "".join(_card(out, work, k, d, letter) for (k, d), letter in zip(DESIGNS.items(), "ABC"))
    html = f"""<!doctype html><html lang="ru"><head><meta charset="utf-8"><style>
  :root{{--ink:#1d1b1c;--mute:#6f6a6c;--line:#e7e2e1}}
  *{{box-sizing:border-box}}
  body{{margin:0;padding:28px;background:#f6f3f2;font:14px/1.4 "Segoe UI",system-ui,sans-serif;color:var(--ink);width:1180px}}
  h1{{font-size:26px;margin:0 0 4px}} .sub{{color:var(--mute);margin:0 0 20px}}
  .card{{background:#fff;border:1px solid var(--line);border-radius:18px;padding:18px 20px;margin-bottom:18px}}
  .head{{display:flex;gap:12px;align-items:center;margin-bottom:10px}}
  .letter{{width:34px;height:34px;border-radius:10px;background:#E5383B;color:#fff;font-weight:700;font-size:18px;display:grid;place-items:center}}
  h2{{margin:0;font-size:19px}} .head p{{margin:0;color:var(--mute)}}
  .row{{display:flex;gap:16px;align-items:flex-end}}
  .row.small{{margin-top:10px;align-items:stretch}}
  .hero,.alt,.explorer{{display:flex;flex-direction:column;align-items:center;gap:6px}}
  .ex-file{{width:150px;height:150px;border-radius:10px;background:#fff;border:1px solid #e3e3e3;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:8px}}
  .ex-file span{{font:12px "Segoe UI",system-ui;color:#222}}
  .marks{{display:grid;grid-template-columns:auto auto;gap:10px;align-items:center}}
  .marks .cap{{grid-column:1/3;text-align:center}}
  .pane{{width:186px;height:186px;border-radius:14px;display:grid;place-items:center}}
  .light{{background:#EBEBEC}} .dark{{background:#131313}}
  .cap{{color:var(--mute);font-size:12.5px}}
  .strip{{flex:1;display:flex;align-items:center;gap:14px;padding:10px 14px;border-radius:12px}}
  .strip b{{font-weight:600;font-size:12px;width:46px;color:#8a8486}}
  .sz{{display:flex;flex-direction:column;align-items:center;gap:3px}}
  .sz span{{font-size:10.5px;color:#9a9496}}
  .app{{width:250px;border-radius:12px;padding:10px;display:flex;flex-wrap:wrap;align-items:center;gap:8px}}
  .dark-app{{background:#131313;color:#eaeaea}} .light-app{{background:#E4E4E6;color:#29292d}}
  .logo-btn{{width:30px;height:30px;border-radius:6px;display:grid;place-items:center}}
  .dark-app .logo-btn{{background:#1f1f1f}} .light-app .logo-btn{{background:#e1e1e4}}
  .newchat{{font-size:13px;opacity:.9}}
  .side-cap{{width:100%;font-size:11px;opacity:.55}}
  .taskbar{{flex:1;background:#1c1c1f;border-radius:12px;padding:10px 14px;display:flex;align-items:center;gap:10px;flex-wrap:wrap;color:#bdbdbd}}
  .tb-ico{{width:40px;height:40px;border-radius:6px;background:#2a2a2e;display:grid;place-items:center}}
  .tb-cap{{font-size:11px;opacity:.7;margin-left:6px}}
  .grid{{display:flex;gap:18px;align-items:center}}
  .grid p{{margin:0 0 6px;color:var(--mute)}}
</style></head><body>
  <h1>Новый значок Amarin Admin AI</h1>
  <p class="sub">Красный, плоский, 2D: милый лис с опущенными ушками и приоткрытым ротиком. Три варианта, у каждого
  плитка в духе iOS (прозрачные углы) и знак без подложки для самой программы. Мелкие размеры нарисованы отдельно, а не ужаты.</p>
  {cards}
  <section class="card"><div class="grid">
    {img(guides, 260)}
    <div><h2>Одинаковые отступы</h2>
      <p>Плитка стоит в квадрате с одинаковым полем со всех четырёх сторон: {margin} из {canvas}, на 256 px это {margin * 256 // canvas} px.</p>
      <p>Лис внутри плитки строго по центру: поля слева и справа равны, сверху и снизу равны.</p>
      <p>Знак без подложки устроен так же: тот же отступ {margin}/{canvas} по бокам и точная середина по высоте.</p>
      <p>Нынешняя «A» стоит со смещением: слева 20 px, справа 31 (на 256).</p></div>
  </div></section>
</body></html>"""
    render_html(html, os.path.join(out, "preview.png"), work, 1236, 1)
