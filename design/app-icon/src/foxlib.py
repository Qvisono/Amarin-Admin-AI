"""Мини-библиотека для рисования значков: фигуры как пути skia, аффинные преобразования,
булевы операции и сборка чистого SVG.

Итоговый SVG — только залитые пути, без масок, клипов и обводок. Слои режутся друг о друга
булевыми операциями, а не накрываются: иначе у общего края сглаживание нижнего слоя
проступает каймой, и такой путь нельзя без переделки перенести в XAML.
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


def grow(s: Shape, r: float) -> Shape:
    """Расширить фигуру на r во все стороны."""
    return s | s.stroke_outline(2 * r)


def shrink(s: Shape, r: float) -> Shape:
    """Сжать фигуру на r внутрь — для каймы одной ширины по всему краю."""
    return s - s.stroke_outline(2 * r)


# ------------------------------------------------------------------ примитивы

K = 0.5522847498  # ручки кубической четверти окружности


def ellipse(cx, cy, rx, ry):
    return Shape.from_cubics(
        (cx + rx, cy),
        [
            ((cx + rx, cy + K * ry), (cx + K * rx, cy + ry), (cx, cy + ry)),
            ((cx - K * rx, cy + ry), (cx - rx, cy + K * ry), (cx - rx, cy)),
            ((cx - rx, cy - K * ry), (cx - K * rx, cy - ry), (cx, cy - ry)),
            ((cx + K * rx, cy - ry), (cx + rx, cy - K * ry), (cx + rx, cy)),
        ],
    )


def circle(cx, cy, r):
    return ellipse(cx, cy, r, r)


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


# ------------------------------------------------------------------ сцена


@dataclass
class Layer:
    shape: Shape
    fill: str
    name: str = ""


@dataclass
class Art:
    layers: list = field(default_factory=list)

    def add(self, shape, fill, name=""):
        if shape is not None and not shape.empty():
            self.layers.append(Layer(shape, fill, name))
        return self

    def bounds(self):
        boxes = [layer.shape.bounds() for layer in self.layers]
        return (min(b[0] for b in boxes), min(b[1] for b in boxes), max(b[2] for b in boxes), max(b[3] for b in boxes))

    def transformed(self, m):
        return Art([Layer(layer.shape.transformed(m), layer.fill, layer.name) for layer in self.layers])

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
        parts += [f'<path fill="{layer.fill}" d="{layer.shape.d()}"/>' for layer in self.layers]
        body = "\n  ".join(parts)
        return (
            f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="{size}" height="{size}">\n  '
            f"{body}\n</svg>\n"
        )
