"""Лис для значка: красная голова, белая зубчатая шерсть на щеках в красной кайме, прижатые
опущенные ушки, приоткрытый ротик. Три варианта отличаются только глазами.

Поле 1024×1024. Силуэт подобран так, что его рамка — ровно квадрат 780×780: в квадратном
значке поля получаются одинаковыми со всех четырёх сторон.

Три уровня детализации (кадр ICO берёт свой):
  full — от 40 px: всё, включая тень под ушами, второй блик и язычок;
  s    — 24-32 px: без тени и язычка, ротик и глаза чуть крупнее;
  xs   — 16-20 px: силуэт, шерсть, уши, глаза и нос — мельче всё равно сливается.
"""
from __future__ import annotations

import math

from foxlib import Art, Shape, circle, ellipse, rounded_polygon, shrink
from hobby import mirror_knots, path

FULL, S, XS = "full", "s", "xs"

PAL = dict(
    fur="#E4373C",
    shade="#C42A35",      # тень под ушами
    ear_back="#B91F2F",   # кромка уха
    inner="#FFD5CC",
    tip="#3B1A20",        # тёмные кончики ушей, как у рыжей лисы
    cream="#FFF5EE",
    ink="#2A161B",
    mouth="#8F1B2C",
    tongue="#FF8A8F",
    shine="#FFFFFF",
)


def C(x, y, din=None, dout=None):
    """Острый узел контура; din/dout — направления входа и выхода в градусах."""
    k = {"p": (x, y), "corner": True}
    if din is not None:
        k["din"] = din
    if dout is not None:
        k["dout"] = dout
    return k


def sym(right_half):
    """Симметричная фигура по правой половине (от точки на оси сверху до точки на оси снизу)."""
    start, segs = path(mirror_knots(right_half), closed=True)
    return Shape.from_cubics(start, segs)


def stroke(points, width):
    start, segs = path(points, closed=False)
    return Shape.from_cubics(start, segs, close=False).stroke_outline(width)


def ellipse_pts(cx, cy, rx, ry, angles):
    return [(cx + rx * math.cos(math.radians(a)), cy - ry * math.sin(math.radians(a))) for a in angles]


# Купол головы — точки на эллипсе: так контур от макушки до щёк выпуклый, без бугров.
TOP = ellipse_pts(512, 528, 372, 412, [90, 62, 40, 20, 4])
# Шерсть на щеках: острые зубцы вниз-наружу, к подбородку мельче.
TIPS = [(902, 592), (876, 674), (830, 756)]
NOTCHES = [(852, 606), (818, 688), (764, 770)]
JAW = [(670, 852), (512, 896)]
# Верхняя граница белого: «M» под глазами и клин переносицы до носа.
MASK = [C(512, 744), (538, 684), (572, 626), (622, 586), (700, 564), (790, 558), (880, 538), C(1100, 520),
        C(1100, 1100), C(512, 1100)]
RIM = 24  # красная кайма вокруг белой шерсти: белое не касается края и не расплывается «бородой»
# Правое ухо лежит поверх головы: корень у макушки, кончик опущен вниз-наружу за край головы.
EAR = [(594, 196), (900, 520), (778, 494), (618, 336)]
EAR_TIP_R = 46
EAR_INNER = (0.60, 0.84)  # внутренняя часть: доля от основания по бокам и к кончику
EAR_TIP_CUT = 0.28        # тёмный кончик — последние 28% длины
EAR_SHADOW = (10, 16)
NOSE = (512, 728, 40, 44)
MOUTH = (512, 792, 28, 32)

EYES = {
    "round": (620, 466, 48, 58),
    "closed": (620, 482, 48, 58),
    "pill": (614, 470, 34, 50),
}


def silhouette():
    knots = list(TOP)
    for t, n in zip(TIPS, NOTCHES):
        knots += [C(*t), C(*n)]
    return sym(knots + JAW)


def pressed_ear():
    r1, tip, r2, inside = EAR
    ear = rounded_polygon([r1, tip, r2, inside], [30, EAR_TIP_R, 30, 30])
    base = ((r1[0] + r2[0]) / 2, (r1[1] + r2[1]) / 2)
    k, kt = EAR_INNER

    def toward(pt, f):
        return (base[0] + (pt[0] - base[0]) * f, base[1] + (pt[1] - base[1]) * f)

    inner = rounded_polygon([toward(r1, k), toward(tip, kt), toward(r2, k), toward(inside, k)],
                            [16, EAR_TIP_R * 0.7, 16, 16]) & ear
    length = math.hypot(tip[0] - base[0], tip[1] - base[1])
    tip_zone = ear - circle(base[0], base[1], length * (1 - EAR_TIP_CUT))
    return ear, inner, tip_zone


def fox(eyes="round", detail=FULL, pal=None):
    p = dict(PAL, **(pal or {}))
    art = Art()

    sil = silhouette()
    cream = shrink(sil, RIM) & sym(MASK)
    fur = sil - cream
    ear, inner, tip_zone = pressed_ear()
    ears, inners, tips = ear.with_mirror(), inner.with_mirror(), tip_zone.with_mirror()

    art.add(fur, p["fur"], "fur")
    if detail == FULL:
        shadow = ear.translate(*EAR_SHADOW) - ear
        art.add((shadow | shadow.mirrored()) & fur, p["shade"], "ear shadow")
    art.add(cream, p["cream"], "cream")
    art.add(ears - tips, p["ear_back"], "ears")
    art.add(tips, p["tip"], "ear tips")
    if detail != XS:
        art.add(inners - tips, p["inner"], "inner ears")

    ex, ey, ew, eh = EYES[eyes]
    if eyes == "round":
        scale = {FULL: (1, 1), S: (1.08, 1.06), XS: (1.15, 1.12)}[detail]
        ew, eh = ew * scale[0], eh * scale[1]
        art.add(ellipse(ex, ey, ew, eh).with_mirror(), p["ink"], "eyes")
        if detail != XS:
            for cx in (ex, 1024 - ex):  # блики с одной стороны: свет падает слева сверху
                shine = circle(cx - ew * 0.30, ey - eh * 0.34, ew * 0.36)
                if detail == FULL:
                    shine = shine | circle(cx + ew * 0.32, ey + eh * 0.42, ew * 0.14)
                art.add(shine, p["shine"], "eye shine")
    elif eyes == "pill":
        def capsule(cx):
            return rounded_polygon([(cx - ew, ey - eh), (cx + ew, ey - eh), (cx + ew, ey + eh), (cx - ew, ey + eh)], [ew] * 4)

        art.add(capsule(ex) | capsule(1024 - ex), p["ink"], "eyes")
        if detail != XS:
            for cx in (ex, 1024 - ex):
                art.add(circle(cx - ew * 0.18, ey - eh * 0.42, ew * 0.42), p["shine"], "eye shine")
    else:  # зажмурился: дуги «домиком»
        width = {FULL: 26, S: 32, XS: 42}[detail]
        arc = stroke([(ex - ew * 1.05, ey + eh * 0.20), (ex, ey - eh * 0.45), (ex + ew * 1.05, ey + eh * 0.20)], width)
        art.add(arc.with_mirror(), p["ink"], "eyes")

    nx, ny, nw, nh = NOSE
    if detail == XS:
        nw, nh = nw * 1.2, nh * 1.15
    art.add(rounded_polygon([(nx - nw, ny), (nx + nw, ny), (nx, ny + nh)], [18, 18, 16]), p["ink"], "nose")
    if detail != XS:
        mx, my, mw, mh = MOUTH
        if detail == S:
            mw, mh = mw * 1.25, mh * 1.2
        mouth = sym([C(512, my), C(mx + mw, my + 2), (mx + mw * 0.62, my + mh * 0.78), (512, my + mh)])
        art.add(mouth, p["mouth"], "mouth")
        if detail == FULL:
            art.add(ellipse(mx, my + mh * 0.95, mw * 0.72, mh * 0.5) & mouth, p["tongue"], "tongue")
    return art


DESIGNS = {
    "a": dict(name="Ушки", about="большие блестящие глазки, ушки прижаты, ротик приоткрыт", eyes="round"),
    "b": dict(name="Зажмурился", about="глазки дужками: лис довольно зажмурился", eyes="closed"),
    "c": dict(name="Капсулы", about="глаза-капсулы: самый лаконичный, ближе к логотипу", eyes="pill"),
}
