"""Листы для выбора варианта: overview.png (с одного взгляда) и preview.png (подробно)."""
from __future__ import annotations

import json
import os
import subprocess

from fox_icons import DESIGNS

HERE = os.path.dirname(os.path.abspath(__file__))
SMALL = [64, 48, 32, 24, 20, 16]
LIGHT, DARK = "#EBEBEC", "#131313"  # фон окна программы в светлой и тёмной теме


def img(path, size):
    return f'<img src="file://{os.path.abspath(path)}" width="{size}" height="{size}">'


def render_html(html, out_png, work, width, scale):
    stem = os.path.basename(out_png)[:-4]
    page = os.path.join(work, stem + ".html")
    with open(page, "w") as f:
        f.write(html)
    jobs = os.path.join(work, stem + ".jobs.json")
    with open(jobs, "w") as f:
        json.dump([{"html": page, "out": out_png, "width": width, "height": 600, "scale": scale}], f)
    subprocess.run(["node", os.path.join(HERE, "render.mjs"), jobs], check=True)


BASE_CSS = """
  *{box-sizing:border-box}
  body{margin:0;background:#f6f3f2;font:15px/1.4 "Segoe UI",system-ui,sans-serif;color:#1d1b1c}
  h1{margin:0 0 4px} .sub{color:#6f6a6c;margin:0 0 22px}
  .badge{width:26px;height:26px;border-radius:8px;background:#E4373C;color:#fff;font-size:15px;font-weight:700;display:grid;place-items:center}
  .light{background:#EBEBEC} .dark{background:#131313}
"""


def overview(out, work):
    """Три лиса крупно на светлой и тёмной теме программы и в настоящих размерах."""
    cols = []
    for (key, d), letter in zip(DESIGNS.items(), "ABC"):
        svg = os.path.join(out, f"fox-{key}.svg")
        fr = lambda s: os.path.join(work, f"fox-{key}-{s}.png")  # noqa: E731
        sizes = "".join(f'<div class="sz">{img(fr(s), s)}</div>' for s in (48, 32, 24, 16))
        cols.append(f"""
  <div class="col">
    <div class="big light">{img(svg, 250)}</div>
    <div class="name"><span class="badge">{letter}</span>«{d['name']}»</div>
    <div class="about">{d['about']}</div>
    <div class="row"><div class="mid dark">{img(svg, 120)}</div>
      <div class="sizes"><div class="strip light">{sizes}</div><div class="strip dark">{sizes}</div></div></div>
  </div>""")
    html = f"""<!doctype html><html lang="ru"><head><meta charset="utf-8"><style>{BASE_CSS}
  body{{padding:30px 34px;width:1132px}} h1{{font-size:25px}}
  .grid{{display:flex;gap:24px}}
  .col{{width:361px;background:#fff;border:1px solid #e7e2e1;border-radius:20px;padding:16px;display:flex;flex-direction:column;align-items:center}}
  .big{{width:329px;height:329px;border-radius:16px;display:grid;place-items:center}}
  .name{{font-weight:700;font-size:19px;margin-top:12px;display:flex;gap:8px;align-items:center}}
  .about{{color:#6f6a6c;text-align:center;font-size:13.5px;min-height:38px}}
  .row{{display:flex;gap:10px;margin-top:10px;align-items:stretch;width:100%}}
  .mid{{width:150px;height:150px;border-radius:14px;display:grid;place-items:center;flex:none}}
  .sizes{{flex:1;display:flex;flex-direction:column;gap:10px}}
  .strip{{flex:1;border-radius:12px;display:flex;align-items:center;justify-content:space-around;padding:0 6px}}
  .sz{{display:grid;place-items:center}}
  .foot{{color:#6f6a6c;font-size:13px;margin-top:16px}}
</style></head><body>
  <h1>Лис для Amarin Admin AI: три варианта</h1>
  <p class="sub">Красный, плоский, 2D. Ушки опущены и прижаты, ротик приоткрыт. Без подложки: фон прозрачный,
  лис стоит прямо на окне программы. Справа внизу настоящие размеры 48, 32, 24 и 16 px.</p>
  <div class="grid">{''.join(cols)}</div>
  <p class="foot">Рамка лиса квадратная, поэтому поля одинаковые со всех четырёх сторон: 64 из 1024, на 256 px это 16 px.</p>
</body></html>"""
    render_html(html, os.path.join(out, "overview.png"), work, 1200, 2)


def guides_svg(path, svg_path, margin, canvas):
    """Лис с направляющими: рамка рисунка и равные поля до краёв."""
    body = open(svg_path).read()
    body = body[: body.rindex("</svg>")]
    m, c = margin, canvas / 2
    dash = 'fill="none" stroke-width="5" stroke-dasharray="18 12"'
    g = [
        f'<rect x="2.5" y="2.5" width="{canvas - 5}" height="{canvas - 5}" {dash} stroke="#9a9496"/>',
        f'<rect x="{m}" y="{m}" width="{canvas - 2 * m}" height="{canvas - 2 * m}" {dash} stroke="#2F7DF6"/>',
    ]
    for ax, ay, bx, by in ((0, c, m, c), (canvas - m, c, canvas, c), (c, 0, c, m), (c, canvas - m, c, canvas)):
        g.append(f'<line x1="{ax}" y1="{ay}" x2="{bx}" y2="{by}" stroke="#2F7DF6" stroke-width="8"/>')
    for lx, ly in ((c + 60, m - 14), (c + 60, canvas - m + 52)):
        g.append(f'<text x="{lx}" y="{ly}" font-size="42" font-family="Segoe UI,Arial" font-weight="700" '
                 f'fill="#2F7DF6" text-anchor="middle">{m}</text>')
    with open(path, "w") as f:
        f.write(body + "\n  " + "\n  ".join(g) + "\n</svg>\n")


def _card(out, work, key, d, letter):
    svg = os.path.join(out, f"fox-{key}.svg")

    def fr(s):
        return os.path.join(work, f"fox-{key}-{s}.png")

    strip = "".join(f'<div class="sz">{img(fr(s), s)}<span>{s}</span></div>' for s in SMALL)
    return f"""
<section class="card">
  <div class="head"><span class="badge">{letter}</span><div><h2>«{d['name']}»</h2><p>{d['about']}</p></div></div>
  <div class="row">
    <div class="pane light">{img(svg, 240)}</div>
    <div class="pane dark">{img(svg, 240)}</div>
    <div class="explorer"><div class="ex-file">{img(fr(96), 96)}<span>Amarin Admin AI.exe</span></div>
      <span class="cap">в Проводнике, 96 px</span></div>
  </div>
  <div class="row small">
    <div class="strip light">{strip}</div>
    <div class="strip dark">{strip}</div>
  </div>
  <div class="row small">
    <div class="app dark"><div class="logo-btn">{img(fr(20), 20)}</div><div class="newchat">＋ Новый чат</div>
      <div class="side-cap">тёмная тема, логотип 20 px</div></div>
    <div class="app light"><div class="logo-btn">{img(fr(20), 20)}</div><div class="newchat">＋ Новый чат</div>
      <div class="side-cap">светлая тема, логотип 20 px</div></div>
    <div class="taskbar"><div class="tb-ico">{img(fr(24), 24)}</div><div class="tb-ico">{img(fr(32), 32)}</div>
      <div class="tb-cap">панель задач: 24 и 32 px</div></div>
  </div>
</section>"""


def preview(out, work, margin, canvas):
    """Подробный лист: крупно, мелкие размеры как есть (1:1), вид в программе и сетка полей."""
    guides = os.path.join(work, "guides.svg")
    guides_svg(guides, os.path.join(out, "fox-a.svg"), margin, canvas)
    cards = "".join(_card(out, work, k, d, letter) for (k, d), letter in zip(DESIGNS.items(), "ABC"))
    html = f"""<!doctype html><html lang="ru"><head><meta charset="utf-8"><style>{BASE_CSS}
  body{{padding:28px;width:900px;font-size:14px}} h1{{font-size:26px}}
  .card{{background:#fff;border:1px solid #e7e2e1;border-radius:18px;padding:18px 20px;margin-bottom:18px}}
  .head{{display:flex;gap:12px;align-items:center;margin-bottom:12px}}
  h2{{margin:0;font-size:19px}} .head p{{margin:0;color:#6f6a6c}}
  .row{{display:flex;gap:14px;align-items:center}}
  .row.small{{margin-top:12px;align-items:stretch}}
  .pane{{width:300px;height:300px;border-radius:14px;display:grid;place-items:center}}
  .explorer{{display:flex;flex-direction:column;align-items:center;gap:6px;margin-left:8px}}
  .ex-file{{width:160px;height:160px;border-radius:10px;background:#fff;border:1px solid #e3e3e3;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:8px}}
  .ex-file span{{font:12px "Segoe UI",system-ui;color:#222}}
  .cap{{color:#6f6a6c;font-size:12.5px}}
  .strip{{flex:1;display:flex;align-items:center;justify-content:space-around;padding:10px 12px;border-radius:12px}}
  .sz{{display:flex;flex-direction:column;align-items:center;gap:3px}}
  .sz span{{font-size:10.5px;color:#9a9496}}
  .app{{width:250px;border-radius:12px;padding:10px;display:flex;flex-wrap:wrap;align-items:center;gap:8px}}
  .app.dark{{color:#eaeaea}} .app.light{{background:#E4E4E6;color:#29292d}}
  .logo-btn{{width:30px;height:30px;border-radius:6px;display:grid;place-items:center}}
  .app.dark .logo-btn{{background:#1f1f1f}} .app.light .logo-btn{{background:#e1e1e4}}
  .newchat{{font-size:13px;opacity:.9}}
  .side-cap{{width:100%;font-size:11px;opacity:.55}}
  .taskbar{{flex:1;background:#1c1c1f;border-radius:12px;padding:10px 14px;display:flex;align-items:center;gap:10px;flex-wrap:wrap;color:#bdbdbd}}
  .tb-ico{{width:44px;height:44px;border-radius:6px;background:#2a2a2e;display:grid;place-items:center}}
  .tb-cap{{font-size:11px;opacity:.7;margin-left:6px}}
  .grid{{display:flex;gap:20px;align-items:center}}
  .grid p{{margin:0 0 6px;color:#6f6a6c}}
</style></head><body>
  <h1>Новый значок Amarin Admin AI</h1>
  <p class="sub">Красный, плоский, 2D: милый лис с опущенными ушками и приоткрытым ротиком. Фон прозрачный.
  Мелкие размеры нарисованы отдельно, а не ужаты из большого.</p>
  {cards}
  <section class="card"><div class="grid">
    {img(guides, 260)}
    <div><h2>Одинаковые отступы</h2>
      <p>Рамка рисунка ровно квадратная и стоит по центру: поля слева, справа, сверху и снизу одинаковые,
      {margin} из {canvas}. На 256 px это {margin * 256 // canvas} px, на 32 px это {margin * 32 // canvas} px.</p>
      <p>Подложки нет: значок кладётся прямо на окно программы, как сейчас.</p>
      <p>Нынешняя «A» стоит со смещением: слева 20 px, справа 31 (на 256).</p></div>
  </div></section>
</body></html>"""
    render_html(html, os.path.join(out, "preview.png"), work, 956, 1)
