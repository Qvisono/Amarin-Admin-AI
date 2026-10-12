"""Три варианта значка «лис с опущенными ушками и приоткрытым ротиком».

Каждый вариант рисуется в поле 1024×1024 и умеет три уровня детализации:
  full — для 40 px и крупнее;
  s    — для 24–32 px: без румянца, бликов на носу и тонких линий рта;
  xs   — для 16–20 px: только силуэт, маска, глаза и нос — мелочь там всё равно в кашу.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from foxlib import (  # noqa: E402
    Art,
    Layer,
    circle,
    ellipse,
    rect,
    rounded_polygon,
    smooth,
    smooth_open,
    symmetric,
)

FULL, S, XS = "full", "s", "xs"


# ------------------------------------------------------------------ общее


def ear_shape(B, L, bulge, tip_round, base_round=0.6):
    """Ухо в своих координатах: основание по оси x, кончик вверх, в (0, -L)."""
    pts = [(-B / 2, 0), (-B * bulge, -L * 0.5), (0, -L), (B * bulge, -L * 0.5), (B / 2, 0), (0, B * 0.2)]
    return smooth(pts, handles={2: tip_round, 0: base_round, 4: base_round})


def placed(shape, theta, anchor):
    """Повернуть ухо наружу-вниз на theta градусов ниже горизонтали и поставить к корню."""
    return shape.rotate(90 + theta).translate(*anchor)


def omega_mouth(my, w, h, line):
    """Приоткрытый рот: верх — «ω» от перемычки, низ — мягкая чаша."""
    xs = (512 - w, 512 - w / 2, 512, 512 + w / 2, 512 + w)
    top = smooth_open([(xs[0], my - 4), (xs[1], my + 12), (xs[2], my), (xs[3], my + 12), (xs[4], my - 4)],
                      handles={1: 1.1, 3: 1.1})
    bowl = smooth(
        [(xs[0], my - 4), (xs[1], my + 12), (xs[2], my), (xs[3], my + 12), (xs[4], my - 4),
         (512 + w * 0.48, my + h * 0.8), (512, my + h), (512 - w * 0.48, my + h * 0.8)],
        corners=[0, 2, 4], handles={1: 1.1, 3: 1.1},
    )
    return bowl, top.stroke_outline(line)


def fox_nose(ny, w=52, h=52):
    return smooth([(512, ny), (512 + w, ny + 6), (530, ny + h), (494, ny + h), (512 - w, ny + 6)],
                  handles={0: 0.8, 2: 0.5, 3: 0.5})


def sticker(art, width, color):
    """Белая обводка по силуэту — как наклейка."""
    sil = art.silhouette()
    return Art([Layer(sil.stroke_outline(width * 2) | sil, color, 1.0, "sticker")] + art.layers)


# ------------------------------------------------------------------ A «Лисёнок»

A_PAL = dict(red="#E5383B", ear="#C92A33", tip="#7E1426", inner="#FFE3D8", cream="#FFF4EA", ink="#2B1A1E",
             mouth="#7E1426", tongue="#FF7F8E", blush="#FF8E8E", shine="#FFFFFF")
# На малиновой плитке лис светлее, а кончики ушей почти чёрные: тёмно-красные кончики
# сливались с фоном, и уши читались обрубленными.
A_TILE_PAL = dict(A_PAL, red="#F04848", ear="#D8313A", tip="#4A0A14")
A_TILE_BG = "#B3192B"

# Правая половина головы от макушки до подбородка; 5 — кончик пушистой щеки.
A_HEAD = [(512, 206), (630, 214), (732, 262), (806, 352), (846, 470), (916, 624), (790, 784), (646, 868), (512, 908)]


def fox_a(detail=FULL, pal=None):
    p = dict(A_PAL, **(pal or {}))
    art = Art()
    head = symmetric(A_HEAD, corners=[5])

    theta, anchor, B, L = 45, (664, 274), 256, 446
    outer = ear_shape(B, L, bulge=0.36, tip_round=0.25)
    tip_zone = outer - circle(0, -L * 0.10, L * 0.64)
    inner = smooth(
        [(-B * 0.28, -L * 0.14), (-B * 0.168, -L * 0.47), (0, -L * 0.80), (B * 0.168, -L * 0.47), (B * 0.28, -L * 0.14),
         (0, -L * 0.14 + B * 0.1)],
        handles={2: 0.25},
    ) - tip_zone
    ears = placed(outer, theta, anchor).with_mirror()
    tips = placed(tip_zone, theta, anchor).with_mirror()
    art.add(ears - tips, p["ear"], name="ears")
    art.add(tips, p["tip"], name="ear tips")
    if detail != XS:
        art.add(placed(inner, theta, anchor).with_mirror() - head, p["inner"], name="inner ears")

    t = 600
    cream_zone = symmetric(
        [(512, t + 52), (566, t + 6), (646, t - 12), (744, t - 8), (852, t + 26), (950, t + 70), (1010, t + 120),
         (1010, 1010), (512, 1010)],
        corners=[0, 7],
    )
    cream = head & cream_zone
    art.add(head - cream_zone, p["red"], name="head")
    art.add(cream, p["cream"], name="muzzle")

    ex, ey, erx, ery = 646, 524, 54, 62
    if detail == XS:
        erx, ery = 60, 68
    art.add(ellipse(ex, ey, erx, ery).with_mirror(), p["ink"], name="eyes")
    if detail == FULL:
        shine = circle(ex + erx * 0.34, ey - ery * 0.36, erx * 0.36) | circle(ex - erx * 0.30, ey + ery * 0.46, erx * 0.15)
        art.add(shine.with_mirror(), p["shine"], name="eye shine")
    elif detail == S:
        art.add(circle(ex + erx * 0.30, ey - ery * 0.32, erx * 0.40).with_mirror(), p["shine"], name="eye shine")

    if detail == FULL:
        art.add(ellipse(754, t + 70, 44, 25).with_mirror() & cream, p["blush"], opacity=0.75, name="blush")

    ny = t + 40
    my = ny + 84
    if detail == FULL:
        bowl, lip = omega_mouth(my, 50, 54, 11)
        philtrum = smooth_open([(512, ny + 46), (512, my)]).stroke_outline(11)
        art.add(bowl, p["mouth"], name="mouth")
        art.add(ellipse(512, my + 56, 36, 26) & bowl, p["tongue"], name="tongue")
        art.add(philtrum | lip, p["ink"], name="mouth line")
    elif detail == S:
        bowl = smooth([(456, my - 6), (512, my - 2), (568, my - 6), (548, my + 46), (512, my + 60), (476, my + 46)],
                      corners=[0, 2])
        art.add(bowl, p["mouth"], name="mouth")
        art.add(ellipse(512, my + 62, 40, 30) & bowl, p["tongue"], name="tongue")
    art.add(fox_nose(ny, 58 if detail == XS else 52, 56 if detail == XS else 52), p["ink"], name="nose")
    if detail == FULL:
        art.add(ellipse(496, ny + 14, 14, 7), p["shine"], opacity=0.55, name="nose shine")
    return art


# ------------------------------------------------------------------ B «Улыбка»

B_PAL = dict(red="#E5383B", ear="#C92A33", tip="#7E1426", inner="#FFE3D8", cream="#FFF6EE", ink="#3A1C22",
             mouth="#8A1A2B", tongue="#FF7F8E", blush="#FF8E8E")
# Плитка чуть темнее лиса: белая обводка отделяет его и так, но одинаковый красный внутри и
# снаружи обводки читался как дырка в плитке.
B_TILE_BG = "#D42A36"
B_STICKER = "#FFFFFF"
# Обводка тем толще, чем мельче кадр: тонкая на 16 px расплывается в бледную полоску.
B_STICKER_WIDTH = {FULL: 28, S: 40, XS: 56}

B_HEAD = [(512, 214), (636, 222), (740, 270), (816, 362), (858, 482), (926, 622), (800, 786), (656, 862), (512, 900)]


def fox_b(detail=FULL, pal=None):
    p = dict(B_PAL, **(pal or {}))
    art = Art()
    head = symmetric(B_HEAD, corners=[5])

    theta, anchor, B, L = 45, (668, 282), 262, 440
    outer = ear_shape(B, L, bulge=0.36, tip_round=0.55)
    tip_zone = outer - circle(0, -L * 0.06, L * 0.66)
    inner = smooth(
        [(-B * 0.25, -L * 0.14), (-B * 0.16, -L * 0.48), (0, -L * 0.80), (B * 0.16, -L * 0.48), (B * 0.25, -L * 0.14),
         (0, -L * 0.10)],
        handles={2: 0.45},
    ) - tip_zone
    ears = placed(outer, theta, anchor).with_mirror()
    tips = placed(tip_zone, theta, anchor).with_mirror()
    art.add(ears - tips, p["ear"], name="ears")
    art.add(tips, p["tip"], name="ear tips")
    if detail != XS:
        art.add(placed(inner, theta, anchor).with_mirror() - head, p["inner"], name="inner ears")

    t = 606
    cream_zone = symmetric(
        [(512, t + 46), (568, t + 2), (652, t - 14), (752, t - 8), (860, t + 26), (960, t + 70), (1010, t + 120),
         (1010, 1010), (512, 1010)],
        corners=[0, 7],
    )
    cream = head & cream_zone
    art.add(head - cream_zone, p["red"], name="head")
    art.add(cream, p["cream"], name="muzzle")

    # Зажмуренные глазки — дуги «домиком», толстой линией с круглыми концами.
    ex, ey, ew, eh = 652, 542, 50, 32
    stroke = {FULL: 28, S: 34, XS: 44}[detail]
    eye = smooth_open([(ex - ew, ey + eh * 0.35), (ex, ey - eh * 0.65), (ex + ew, ey + eh * 0.35)],
                      handles={1: 1.3}).stroke_outline(stroke)
    art.add(eye.with_mirror(), p["ink"], name="eyes")

    if detail == FULL:
        art.add(ellipse(762, t + 74, 50, 28).with_mirror() & cream, p["blush"], opacity=0.8, name="blush")

    ny = t + 34
    my = ny + 82
    if detail == FULL:
        bowl, lip = omega_mouth(my, 54, 62, 12)
        philtrum = smooth_open([(512, ny + 44), (512, my)]).stroke_outline(12)
        art.add(bowl, p["mouth"], name="mouth")
        art.add(ellipse(512, my + 64, 40, 30) & bowl, p["tongue"], name="tongue")
        art.add(philtrum | lip, p["ink"], name="mouth line")
    else:
        bowl = smooth([(452, my - 6), (512, my - 2), (572, my - 6), (550, my + 50), (512, my + 66), (474, my + 50)],
                      corners=[0, 2])
        art.add(bowl, p["mouth"], name="mouth")
        if detail == S:
            art.add(ellipse(512, my + 68, 42, 32) & bowl, p["tongue"], name="tongue")
    art.add(fox_nose(ny, 56 if detail == XS else 50, 54 if detail == XS else 50), p["ink"], name="nose")
    return art


# ------------------------------------------------------------------ C «Грань»

C_PAL = dict(red_l="#EE4448", red_d="#D02C37", ear_l="#C92A33", ear_d="#A51D2B", inner_l="#FFC2B8",
             inner_d="#F2AFA6", cream_l="#FFF5EC", cream_d="#F6DDD0", ink="#2A171C", mouth="#6E1222",
             tongue="#FF7F8E", shine="#FFFFFF")
C_TILE_BG = "#211A1E"

_LEFT = rect(-4000, -4000, 4512, 9000)
_RIGHT = rect(512, -4000, 4512, 9000)


def _mirror_pts(right):
    return list(right) + [(1024 - x, y) for x, y in reversed(right[1:-1])]


def fox_c(detail=FULL, pal=None):
    p = dict(C_PAL, **(pal or {}))
    art = Art()
    head = rounded_polygon(
        _mirror_pts([(512, 220), (640, 232), (754, 318), (862, 476), (946, 640), (512, 924)]),
        [400, 90, 70, 50, 18, 120, 18, 50, 70, 90],
    )

    # Уши прижаты к голове и лежат поверх неё — так «опущенные ушки» читаются сразу.
    top, tip, side, inside = (584, 236), (960, 572), (800, 516), (604, 384)
    ear = rounded_polygon([top, tip, side, inside], [30, 44, 30, 30])
    base = ((top[0] + side[0]) / 2, (top[1] + side[1]) / 2)
    base = (base[0] + (tip[0] - base[0]) * 0.10, base[1] + (tip[1] - base[1]) * 0.10)

    def towards(pt, f):
        return (base[0] + (pt[0] - base[0]) * f, base[1] + (pt[1] - base[1]) * f)

    inner = rounded_polygon([towards(top, 0.62), towards(tip, 0.80), towards(side, 0.62), towards(inside, 0.62)],
                            [10, 26, 10, 10]) & ear

    t = 600
    mask_zone = rounded_polygon(
        _mirror_pts([(512, t + 54), (604, t - 22), (744, t - 10), (960, t + 32), (1100, t + 100), (1100, 1100),
                     (512, 1100)]),
        [18, 50, 50, 0, 0, 0, 0, 0, 0, 0, 0, 50],
    )
    cream = head & mask_zone
    fur = head - mask_zone
    art.add(fur & _LEFT, p["red_l"], name="head L")
    art.add(fur & _RIGHT, p["red_d"], name="head R")
    art.add(cream & _LEFT, p["cream_l"], name="mask L")
    art.add(cream & _RIGHT, p["cream_d"], name="mask R")
    art.add(ear.mirrored(), p["ear_l"], name="ear L")
    art.add(ear, p["ear_d"], name="ear R")
    art.add(inner.mirrored(), p["inner_l"], name="inner L")
    art.add(inner, p["inner_d"], name="inner R")

    ex, ey, er = 648, 528, 58
    if detail == XS:
        er = 66
    art.add(circle(ex, ey, er).with_mirror(), p["ink"], name="eyes")
    if detail == FULL:
        shine = circle(ex + er * 0.32, ey - er * 0.34, er * 0.30) | circle(ex - er * 0.34, ey + er * 0.40, er * 0.12)
        art.add(shine.with_mirror(), p["shine"], name="shine")
    elif detail == S:
        art.add(circle(ex + er * 0.30, ey - er * 0.30, er * 0.34).with_mirror(), p["shine"], name="shine")

    ny = t + 38
    my = ny + 84
    if detail == FULL:
        philtrum = smooth_open([(512, ny + 40), (512, my + 2)]).stroke_outline(11)
        art.add(philtrum, p["ink"], name="philtrum")
        m = rounded_polygon([(466, my), (558, my), (512, my + 62)], [14, 14, 22])
        art.add(m, p["mouth"], name="mouth")
        art.add(ellipse(512, my + 64, 40, 30) & m, p["tongue"], name="tongue")
    elif detail == S:
        m = rounded_polygon([(458, my - 10), (566, my - 10), (512, my + 60)], [16, 16, 24])
        art.add(m, p["mouth"], name="mouth")
    w = 58 if detail == XS else 52
    art.add(rounded_polygon([(512 - w, ny), (512 + w, ny), (512, ny + 56 + (6 if detail == XS else 0))], [20, 20, 18]),
            p["ink"], name="nose")
    return art


# ------------------------------------------------------------------ каталог
# mark — лис без подложки; tile_art и tile_bg — он же на плитке; alt_* — плитка другого цвета.

DESIGNS = {
    "a": dict(
        name="Лисёнок",
        about="круглая мордочка, блестящие глазки, румянец и язычок",
        mark=lambda d: fox_a(d),
        tile_art=lambda d: fox_a(d, A_TILE_PAL),
        tile_bg=A_TILE_BG,
        tile_pad=0.085,
        alt_bg="#FFD5CA",
        alt_art=lambda d: fox_a(d),
    ),
    "b": dict(
        name="Улыбка",
        about="зажмурился от удовольствия, лис-наклейка на красном",
        mark=lambda d: fox_b(d),
        tile_art=lambda d: sticker(fox_b(d), B_STICKER_WIDTH[d], B_STICKER),
        tile_bg=B_TILE_BG,
        tile_pad=0.075,
        alt_bg="#9E1626",
        alt_art=lambda d: sticker(fox_b(d), B_STICKER_WIDTH[d], B_STICKER),
    ),
    "c": dict(
        name="Грань",
        about="геометричный, светлая и тенистая половины, ушки прижаты",
        mark=lambda d: fox_c(d),
        tile_art=lambda d: fox_c(d),
        tile_bg=C_TILE_BG,
        tile_pad=0.085,
        alt_bg="#4A0E1A",
        alt_art=lambda d: fox_c(d),
    ),
}
