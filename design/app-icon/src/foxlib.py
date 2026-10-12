"""Мини-библиотека для рисования значков: фигуры как пути skia, аффинные преобразования,
булевы операции и сборка чистого SVG.

Итоговый SVG — только залитые пути, без масок, клипов и обводок. Слои режутся друг о друга
булевыми операциями, а не накрываются: иначе у общего края сглаживание нижнего слоя
проступает каймой (у светлой мордочки на красной голове была розовая бахрома), и такой путь
нельзя без переделки перенести в XAML.
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field

import pathops

CX = 512.0  # ось симметрии поля 1024


def fmt(v: float) -> str:
    s = f"{v:.2f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


class Shape:
    """Путь skia, заливка по ненулевому правилу."""

    def __init__(self, path: pathops.Path | None = None):
        self.p = path if path is not None else pathops.Path()

    @staticmethod
    def from_cubics(start, segs, close=True):
        """Сегмент из одной точки — прямая, из трёх — кубическая кривая."""
        s = Shape()
        s.p.moveTo(*start)
        for seg in segs:
            if len(seg) == 1:
                s.p.lineTo(*seg[0])
            else:
                c1, c2, p = seg
                s.p.cubicTo(*c1, *c2, *p)
        if close:
            s.p.close()
        return s

    def copy(self):
        q = pathops.Path()
        q.addPath(self.p)
        return Shape(q)

    def __or__(self, other):
        return Shape(pathops.op(self.p, other.p, pathops.PathOp.UNION))

    def __sub__(self, other):
        return Shape(pathops.op(self.p, other.p, pathops.PathOp.DIFFERENCE))

    def __and__(self, other):
        return Shape(pathops.op(self.p, other.p, pathops.PathOp.INTERSECTION))

    def transformed(self, m):
        """m = (a, b, c, d, e, f): x' = a·x + c·y + e, y' = b·x + d·y + f."""
        a, b, c, d, e, f = m
        q = pathops.Path()
        for verb, pts in self.p.segments:
            tp = [(a * x + c * y + e, b * x + d * y + f) for x, y in pts]
            if verb == "moveTo":
                q.moveTo(*tp[0])
            elif verb == "lineTo":
                q.lineTo(*tp[0])
            elif verb == "curveTo":
                q.cubicTo(*tp[0], *tp[1], *tp[2])
            elif verb == "qCurveTo":
                for cp, end in _quads(tp):
                    q.quadTo(*cp, *end)
            elif verb == "closePath":
                q.close()
            elif verb != "endPath":
                raise ValueError(verb)
        return Shape(q)

    def mirrored(self, cx=CX):
        return self.transformed((-1, 0, 0, 1, 2 * cx, 0))

    def with_mirror(self, cx=CX):
        return self | self.mirrored(cx)

    def translate(self, dx, dy):
        return self.transformed((1, 0, 0, 1, dx, dy))

    def rotate(self, deg, ox=0.0, oy=0.0):
        r = math.radians(deg)
        c, s = math.cos(r), math.sin(r)
        return self.transformed((c, s, -s, c, ox - c * ox + s * oy, oy - s * ox - c * oy))

    def stroke_outline(self, width):
        """Обводка с круглыми концами — как залитая фигура."""
        q = self.copy().p
        q.stroke(width, pathops.LineCap.ROUND_CAP, pathops.LineJoin.ROUND_JOIN, 4)
        q.convertConicsToQuads()
        return Shape(pathops.op(q, pathops.Path(), pathops.PathOp.UNION))

    def bounds(self):
        return self.p.bounds

    def empty(self):
        return not any(True for _ in self.p.segments)

    def d(self):
        out = []
        for verb, pts in self.p.segments:
            if verb == "moveTo":
                out.append(f"M{fmt(pts[0][0])} {fmt(pts[0][1])}")
            elif verb == "lineTo":
                out.append(f"L{fmt(pts[0][0])} {fmt(pts[0][1])}")
            elif verb == "curveTo":
                out.append("C" + " ".join(f"{fmt(x)} {fmt(y)}" for x, y in pts))
            elif verb == "qCurveTo":
                for cp, end in _quads(pts):
                    out.append(f"Q{fmt(cp[0])} {fmt(cp[1])} {fmt(end[0])} {fmt(end[1])}")
            elif verb == "closePath":
                out.append("Z")
        return "".join(out)


def _quads(pts):
    """qCurveTo в духе TrueType: между соседними контрольными точками — неявная точка кривой."""
    ctrl, end = pts[:-1], pts[-1]
    for i, cp in enumerate(ctrl):
        nxt = end if i == len(ctrl) - 1 else ((cp[0] + ctrl[i + 1][0]) / 2, (cp[1] + ctrl[i + 1][1]) / 2)
        yield cp, nxt


# ------------------------------------------------------------------ примитивы

K = 0.5522847498  # ручки кубической четверти окружности


def ellipse(cx, cy, rx, ry, rot=0.0):
    s = Shape.from_cubics(
        (cx + rx, cy),
        [
            ((cx + rx, cy + K * ry), (cx + K * rx, cy + ry), (cx, cy + ry)),
            ((cx - K * rx, cy + ry), (cx - rx, cy + K * ry), (cx - rx, cy)),
            ((cx - rx, cy - K * ry), (cx - K * rx, cy - ry), (cx, cy - ry)),
            ((cx + K * rx, cy - ry), (cx + rx, cy - K * ry), (cx + rx, cy)),
        ],
    )
    return s.rotate(rot, cx, cy) if rot else s


def circle(cx, cy, r):
    return ellipse(cx, cy, r, r)


def rect(x, y, w, h):
    return Shape.from_cubics((x, y), [((x + w, y),), ((x + w, y + h),), ((x, y + h),)])


def smooth(points, closed=True, corners=(), handles=None):
    """Гладкий контур через точки (Catmull-Rom -> кубические кривые).

    corners — индексы острых точек; handles — {индекс: множитель длины ручек}.
    """
    n = len(points)
    handles = handles or {}
    corners = set(corners)

    def tangent(i):
        if i in corners:
            return (0.0, 0.0)
        if closed:
            p0, p2 = points[(i - 1) % n], points[(i + 1) % n]
        else:
            p0, p2 = points[max(i - 1, 0)], points[min(i + 1, n - 1)]
        k = handles.get(i, 1.0) / 6.0
        return ((p2[0] - p0[0]) * k, (p2[1] - p0[1]) * k)

    tans = [tangent(i) for i in range(n)]
    segs = []
    for i in range(n if closed else n - 1):
        a, b = points[i], points[(i + 1) % n]
        ta, tb = tans[i], tans[(i + 1) % n]
        segs.append(((a[0] + ta[0], a[1] + ta[1]), (b[0] - tb[0], b[1] - tb[1]), b))
    return Shape.from_cubics(points[0], segs, close=closed)


def smooth_open(points, corners=(), handles=None):
    return smooth(points, closed=False, corners=corners, handles=handles)


def symmetric(right_half, corners=(), handles=None, cx=CX):
    """Правая половина от точки на оси сверху до точки на оси снизу -> симметричный контур.

    Индексы corners и handles — по правой половине, левая получает зеркальные.
    """
    left = [(2 * cx - x, y) for x, y in reversed(right_half[1:-1])]
    pts = list(right_half) + left
    n_r, n = len(right_half), len(pts)
    cs, hs = set(), {}
    for i in corners:
        cs.add(i)
        if 0 < i < n_r - 1:
            cs.add(n - i)
    for i, v in (handles or {}).items():
        hs[i] = v
        if 0 < i < n_r - 1:
            hs[n - i] = v
    return smooth(pts, closed=True, corners=cs, handles=hs)


def rounded_polygon(points, radius):
    """Многоугольник со скруглёнными углами; radius — число или список по вершинам."""
    n = len(points)
    rs = radius if isinstance(radius, (list, tuple)) else [radius] * n
    corners = []
    for i in range(n):
        p0, p1, p2 = points[i - 1], points[i], points[(i + 1) % n]
        v1 = (p0[0] - p1[0], p0[1] - p1[1])
        v2 = (p2[0] - p1[0], p2[1] - p1[1])
        l1, l2 = math.hypot(*v1), math.hypot(*v2)
        r = min(rs[i], l1 * 0.45, l2 * 0.45)
        a = (p1[0] + v1[0] / l1 * r, p1[1] + v1[1] / l1 * r)
        b = (p1[0] + v2[0] / l2 * r, p1[1] + v2[1] / l2 * r)
        c1 = (a[0] + (p1[0] - a[0]) * 0.55, a[1] + (p1[1] - a[1]) * 0.55)
        c2 = (b[0] + (p1[0] - b[0]) * 0.55, b[1] + (p1[1] - b[1]) * 0.55)
        corners.append((a, (c1, c2, b)))
    segs = []
    for i, (a, curve) in enumerate(corners):
        if i > 0:
            segs.append((a,))
        segs.append(curve)
    return Shape.from_cubics(corners[0][0], segs)


def ios_rounded_rect(x, y, w, h, r):
    """Квадрат с «непрерывными» углами, как маска значков iOS.

    Коэффициенты — известный разбор маски UIKit (iOS 7+): угол начинается в 1,5287·r от
    вершины и меняет кривизну плавно, без излома, как у обычного скругления дугой.
    """
    r = min(r, min(w, h) / 2 / 1.52866483)

    def tl(px, py):
        return (x + px * r, y + py * r)

    def tr(px, py):
        return (x + w - px * r, y + py * r)

    def br(px, py):
        return (x + w - px * r, y + h - py * r)

    def bl(px, py):
        return (x + px * r, y + h - py * r)

    def corner(f, along_x):
        # Обход по часовой. Правый верхний и левый нижний углы начинаются с горизонтального
        # ребра, два других — с вертикального: числа те же, оси меняются местами.
        def p(u, v):
            return f(u, v) if along_x else f(v, u)

        return [
            (p(1.08849323, 0), p(0.86840689, 0), p(0.66993427, 0.06549600)),
            (p(0.63149399, 0.07491100),),
            (p(0.37282392, 0.16905899), p(0.16906013, 0.37282401), p(0.07491176, 0.63149399)),
            (p(0, 0.86840701), p(0, 1.08849299), p(0, 1.52866483)),
        ]

    segs = [(tr(1.52866471, 0),)] + corner(tr, True)
    segs += [(br(0, 1.52866471),)] + corner(br, False)
    segs += [(bl(1.52866471, 0),)] + corner(bl, True)
    segs += [(tl(0, 1.52866471),)] + corner(tl, False)
    return Shape.from_cubics(tl(1.52866483, 0), segs)


# ------------------------------------------------------------------ сцена


@dataclass
class Layer:
    shape: Shape
    fill: str
    opacity: float = 1.0
    name: str = ""


@dataclass
class Art:
    layers: list = field(default_factory=list)

    def add(self, shape, fill, opacity=1.0, name=""):
        if shape is not None and not shape.empty():
            self.layers.append(Layer(shape, fill, opacity, name))
        return self

    def bounds(self):
        boxes = [layer.shape.bounds() for layer in self.layers]
        return (min(b[0] for b in boxes), min(b[1] for b in boxes), max(b[2] for b in boxes), max(b[3] for b in boxes))

    def silhouette(self):
        s = Shape()
        for layer in self.layers:
            s = s | layer.shape
        return s

    def transformed(self, m):
        return Art([Layer(layer.shape.transformed(m), layer.fill, layer.opacity, layer.name) for layer in self.layers])

    def fit(self, box):
        """Вписать рисунок в box = (x0, y0, x1, y1) целиком и поставить ровно в его середину."""
        bx0, by0, bx1, by1 = self.bounds()
        x0, y0, x1, y1 = box
        s = min((x1 - x0) / (bx1 - bx0), (y1 - y0) / (by1 - by0))
        tx = (x0 + x1) / 2 - s * (bx0 + bx1) / 2
        ty = (y0 + y1) / 2 - s * (by0 + by1) / 2
        return self.transformed((s, 0, 0, s, tx, ty))

    def svg(self, size=1024, title=None):
        parts = [f"<title>{title}</title>"] if title else []
        for layer in self.layers:
            op = f' fill-opacity="{fmt(layer.opacity)}"' if layer.opacity != 1.0 else ""
            parts.append(f'<path fill="{layer.fill}"{op} d="{layer.shape.d()}"/>')
        body = "\n  ".join(parts)
        return (
            f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="{size}" height="{size}">\n  '
            f"{body}\n</svg>\n"
        )


def tile(art, bg, canvas=1024, margin=40, radius=0.2237, pad=0.13):
    """Плитка в духе iOS: квадрат с непрерывными углами и прозрачными уголками, рисунок по центру.

    margin — поле вокруг плитки, одинаковое со всех сторон; pad — поле рисунка внутри плитки
    (доля её стороны) по более длинной стороне рисунка.
    """
    size = canvas - 2 * margin
    sq = ios_rounded_rect(margin, margin, size, size, size * radius)
    p = pad * size
    fitted = art.fit((margin + p, margin + p, canvas - margin - p, canvas - margin - p))
    return Art([Layer(sq, bg, 1.0, "tile")] + fitted.layers)
