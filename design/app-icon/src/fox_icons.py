"""Лис для значка в плоском красном: уши опущены и отведены в стороны, ротик приоткрыт.
Цвета — только оттенки красного нынешней «A», без белого и чёрного.

Поле 1024×1024. Пропорции подобраны так, что рамка рисунка — ровно квадрат 858×858: в квадратном
значке поля получаются одинаковыми со всех четырёх сторон (это сверяет make.py).

Два способа раскраски:
  fox()   — несколько плоских оттенков красного: светлые щёки, тёмные кончики ушей и глаза;
  stamp() — один красный, щёки, глазки и середина ушей прорезаны насквозь (как штамп).

Три уровня детализации (кадр ICO берёт свой):
  full — от 40 px: всё, включая румянец, второй блик и язычок;
  s    — 24-32 px: без румянца, второго блика и двух тонов уха, глаза и ротик крупнее;
  xs   — 16-20 px: силуэт, щёки, кончики ушей, глаза и нос — мельче всё равно сливается.
"""
from __future__ import annotations

import math

from foxlib import Art, Shape, circle, ellipse, grow, rounded_polygon, shrink
from hobby import mirror_knots, path

FULL, S, XS = "full", "s", "xs"

# Оттенки нынешней «A»: тёмная изнанка ленты, основной, яркий и розовый блик на вершине.
DARK = "#9E1826"
MID = "#C4192B"
MAIN = "#D81F2D"
SOFT = "#E2525D"   # щёки: светлее основного, но всё ещё красный
PINK = "#EB8597"   # блики, румянец, язычок
DEEP = "#6E0F1A"   # глаза, нос и ротик: темнее «A», иначе на красном теряются

# Голова: макушка и щёки — точки эллипса черепа, ниже две пряди шерсти на щеке и подбородок.
SKULL = (512, 560, 372, 380)
SKULL_ANGLES = (90, 55, 22)
TUFTS = [(918, 626), (872, 650), (900, 742), (826, 760)]  # кончик, выемка, кончик, выемка
JAW = [(720, 891), (512, 955)]

# Ухо — лист из двух дуг: корень за головой, кончик отведён наружу на 60° от вертикали,
# поэтому уши смотрят в стороны, а не вверх. s_in и s_out — выпуклость верхнего и нижнего края.
EAR = dict(root=(590, 313), angle=60, length=410, s_in=98, s_out=108, bias_in=0.45, bias_out=0.42)
EAR_TIP_R = 14     # скругление кончика
EAR_INNER = (0.10, 0.70, 0.55, 0.40)  # светлая середина: от и до (доли длины), выпуклость краёв (доли)
EAR_DARK_R = 104   # тёмный кончик — всё, что ближе этого к кончику

# Граница щёк — одна кубическая кривая от переносицы к щеке: так «V» над носом без изломов.
MASK = ((512, 764), (556, 680), (690, 618), (896, 600))
RIM = {FULL: 22, S: 34, XS: 48}  # красная кайма вокруг щёк; на мелких кадрах шире, иначе пропадает

EYES = (636, 566, 57, 65)
NOSE = (512, 760, 44, 38)
MOUTH = (512, 812, 40, 48)


def C(x, y):
    """Острый узел контура."""
    return {"p": (x, y), "corner": True}


def sym(right_half):
    """Симметричная фигура по правой половине (от точки на оси сверху до точки на оси снизу)."""
    start, segs = path(mirror_knots(right_half), closed=True)
    return Shape.from_cubics(start, segs)


def stroke(points, width):
    start, segs = path(points, closed=False)
    return Shape.from_cubics(start, segs, close=False).stroke_outline(width)


def soften(s, r):
    """Скруглить выпуклые углы радиусом r."""
    return grow(shrink(s, r), r)


def lens(p0, p1, s_a, s_b, bias_a=0.5, bias_b=0.5):
    """Лист от p0 до p1: дуга a слева от хорды (если смотреть от p0 к p1, y вниз), дуга b справа.

    s — наибольший отступ дуги от хорды, bias — где вдоль хорды он приходится (0..1).
    """
    dx, dy = p1[0] - p0[0], p1[1] - p0[1]
    length = math.hypot(dx, dy)
    nx, ny = dy / length, -dx / length

    def ctrl(t, s):
        return (p0[0] + dx * t + nx * s, p0[1] + dy * t + ny * s)

    k = 4 / 3  # у кубической дуги с равными ручками наибольший отступ — 3/4 от их выноса
    a1, a2 = ctrl(bias_a - 0.2, s_a * k), ctrl(bias_a + 0.2, s_a * k)
    b1, b2 = ctrl(bias_b + 0.2, -s_b * k), ctrl(bias_b - 0.2, -s_b * k)
    return Shape.from_cubics(p0, [(a1, a2, p1), (b1, b2, p0)])


def head():
    cx, cy, rx, ry = SKULL
    crown = [(cx + rx * math.cos(math.radians(a)), cy - ry * math.sin(math.radians(a))) for a in SKULL_ANGLES]
    return sym(crown + [C(*t) for t in TUFTS] + JAW)


def cheeks():
    """Область ниже границы щёк, обе стороны."""
    a, b, c, d = MASK
    right = Shape.from_cubics(a, [(b, c, d), ((1200, d[1] - 40),), ((1200, 1200),), ((512, 1200),)])
    return right | right.mirrored()


def ear_axis():
    a = math.radians(EAR["angle"])
    return (math.sin(a), -math.cos(a)), EAR["root"], EAR["length"]


def along(t):
    (ux, uy), root, length = ear_axis()
    return (root[0] + ux * length * t, root[1] + uy * length * t)


def ear_parts():
    """Правое ухо целиком, его светлая середина, тёмный кончик и наружная половина (под второй тон)."""
    (ux, uy), root, length = ear_axis()
    tip = along(1)
    e = EAR
    ear = soften(lens(root, tip, e["s_in"], e["s_out"], e["bias_in"], e["bias_out"]), EAR_TIP_R)
    t0, t1, kin, kout = EAR_INNER
    inner = lens(along(t0), along(t1), e["s_in"] * kin, e["s_out"] * kout, e["bias_in"], e["bias_out"]) & ear
    dark = ear & circle(tip[0], tip[1], EAR_DARK_R)
    nx, ny = uy, -ux  # нормаль к оси: вверх-внутрь
    w = length
    outer = Shape.from_cubics(root, [(tip,), ((tip[0] - nx * w, tip[1] - ny * w),), ((root[0] - nx * w, root[1] - ny * w),)])
    return ear, inner, dark, outer


def nose_shape(scale=1.0):
    nx, ny, nw, nh = NOSE
    nw, nh = nw * scale, nh * scale
    return rounded_polygon([(nx - nw, ny - nh * 0.4), (nx + nw, ny - nh * 0.4), (nx, ny + nh * 0.7)], [20, 20, 16])


def mouth_shape(scale=1.0):
    """Приоткрытый ротик «о» под носом."""
    mx, my, mw, mh = MOUTH
    return ellipse(mx, my + mh * 0.5, mw * 0.62 * scale, mh * 0.55 * scale)


def closed_eyes(width):
    ex, ey, ew, eh = EYES
    arc = stroke([(ex - ew * 1.05, ey + eh * 0.22), (ex, ey - eh * 0.42), (ex + ew * 1.05, ey + eh * 0.22)], width)
    return arc.with_mirror()


def fox(eyes="round", detail=FULL):
    """Оттенки красного: голова, светлые щёки с каймой, уши в два тона с тёмными кончиками."""
    art = Art()
    h = head()
    ear, inner, dark, outer = ear_parts()
    ears = ear.with_mirror() - h  # уши за головой: видно только то, что выше и сбоку
    inners, tips = inner.with_mirror() & ears, dark.with_mirror() & ears
    muzzle = shrink(h, RIM[detail]) & cheeks()
    ex, ey, ew, eh = EYES

    blush = Shape()
    if detail == FULL:
        blush = ellipse(ex + ew * 1.25, ey + eh * 1.45, ew * 0.62, eh * 0.30).with_mirror() & muzzle
    art.add(h - muzzle, MAIN, "head")
    art.add(muzzle - blush, SOFT, "cheeks")
    art.add(blush, PINK, "blush")

    plain = ears - tips
    if detail != XS:
        plain = plain - inners
    if detail == FULL:  # наружная половина уха темнее — как изнанка ленты в «A»
        outers = outer.with_mirror()
        art.add(plain - outers, MAIN, "ears")
        art.add(plain & outers, MID, "ears outside")
    else:
        art.add(plain, MID if detail == XS else MAIN, "ears")
    if detail != XS:
        art.add(inners - tips, SOFT, "inner ears")
    art.add(tips, DARK, "ear tips")

    if eyes == "round":
        k = {FULL: 1.0, S: 1.08, XS: 1.16}[detail]
        w, hh = ew * k, eh * k
        art.add(ellipse(ex, ey, w, hh).with_mirror(), DEEP, "eyes")
        if detail != XS:
            for cx in (ex, 1024 - ex):
                shine = circle(cx - w * 0.24, ey - hh * 0.30, w * 0.36)
                if detail == FULL:
                    shine = shine | circle(cx + w * 0.34, ey + hh * 0.36, w * 0.15)
                art.add(shine, PINK, "eye shine")
    else:
        art.add(closed_eyes({FULL: 30, S: 40, XS: 52}[detail]), DEEP, "eyes")

    art.add(nose_shape({FULL: 1.0, S: 1.1, XS: 1.3}[detail]), DEEP, "nose")
    if detail != XS:
        mouth = mouth_shape(1.0 if detail == FULL else 1.25)
        art.add(mouth, DEEP, "mouth")
        if detail == FULL:
            mx, my, mw, mh = MOUTH
            art.add(ellipse(mx, my + mh * 1.0, mw * 0.66, mh * 0.48) & mouth, PINK, "tongue")
    return art


def stamp(detail=FULL, color=MAIN):
    """Один красный: щёки, середина ушей, глазки и щель между ухом и головой — сквозные.

    Нос и ротик остаются красными внутри прорезанных щёк. На светлом фоне щёки выходят светлыми,
    как у настоящей лисы; на тёмном — тёмными, а силуэт держит красная кайма.
    """
    h = head()
    ear, _, _, _ = ear_parts()
    ears = ear.with_mirror() - h
    cuts = shrink(h, RIM[detail]) & cheeks()
    if detail != XS:
        e = EAR
        hole = lens(along(0.18), along(0.66), e["s_in"] * 0.42, e["s_out"] * 0.30, e["bias_in"], e["bias_out"])
        cuts = cuts | (hole.with_mirror() & shrink(ears, 22))
        gap = {FULL: 26, S: 40}[detail]
        cuts = cuts | (h.stroke_outline(gap) & ears)
    cuts = cuts | closed_eyes({FULL: 34, S: 44, XS: 56}[detail])
    shape = ((h | ears) - cuts) | nose_shape({FULL: 1.0, S: 1.1, XS: 1.3}[detail])
    if detail != XS:
        shape = shape | mouth_shape(1.0 if detail == FULL else 1.25)
    art = Art()
    art.add(shape, color, "fox")
    return art


DESIGNS = {
    "a": dict(name="Глазки", about="оттенки красного, большие блестящие глазки, ушки опущены в стороны, ротик приоткрыт",
              kind="tones", eyes="round"),
    "b": dict(name="Зажмурился", about="оттенки красного, глазки дужками: лис зажмурился и приоткрыл ротик",
              kind="tones", eyes="closed"),
    "c": dict(name="Один красный", about="один цвет, щёки и глазки прорезаны: строгий, как штамп",
              kind="stamp", eyes="closed"),
}


def draw(key, detail=FULL):
    d = DESIGNS[key]
    return stamp(detail) if d["kind"] == "stamp" else fox(d["eyes"], detail)
