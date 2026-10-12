"""Сплайны Хобби (как в METAFONT/MetaPost): гладкие кривые через точки с плавной кривизной.

Узел — точка (x, y) или словарь {p, corner, din, dout}: corner делает узел острым, din/dout
задают направление входа и выхода у угла в градусах (в координатах холста, y вниз). Между
углами каждый кусок решается отдельно; у концов без направления — условие curl.
"""
from __future__ import annotations

import math

import numpy as np

SQ2 = math.sqrt(2.0)
SQ5 = math.sqrt(5.0)


def _rho(theta, phi):
    st, sp = math.sin(theta), math.sin(phi)
    ct, cp = math.cos(theta), math.cos(phi)
    num = 2 + SQ2 * (st - sp / 16) * (sp - st / 16) * (ct - cp)
    den = 1 + 0.5 * (SQ5 - 1) * ct + 0.5 * (3 - SQ5) * cp
    return num / den


def _norm(a):
    while a > math.pi:
        a -= 2 * math.pi
    while a <= -math.pi:
        a += 2 * math.pi
    return a


def _controls(z0, z1, theta, phi):
    dx, dy = z1[0] - z0[0], z1[1] - z0[1]
    d = math.hypot(dx, dy)
    a = math.atan2(dy, dx)
    r1 = _rho(theta, phi) / 3 * d
    r2 = _rho(phi, theta) / 3 * d
    c1 = (z0[0] + r1 * math.cos(a + theta), z0[1] + r1 * math.sin(a + theta))
    c2 = (z1[0] - r2 * math.cos(a - phi), z1[1] - r2 * math.sin(a - phi))
    return c1, c2


def _solve(pts, closed, start_dir=None, end_dir=None):
    """Углы касательных: theta_j на выходе из узла j, phi_k на входе в узел k.

    Условия: направление непрерывно (theta_k + phi_k = -psi_k) и «кривизна» Хобби одна по обе
    стороны узла: (2·phi_k + theta_{k-1}) / d_{k-1} = (2·theta_k + phi_{k+1}) / d_k.
    """
    n = len(pts) if closed else len(pts) - 1
    nxt = (lambda j: (j + 1) % len(pts)) if closed else (lambda j: j + 1)
    d = [math.hypot(pts[nxt(j)][0] - pts[j][0], pts[nxt(j)][1] - pts[j][1]) for j in range(n)]
    ang = [math.atan2(pts[nxt(j)][1] - pts[j][1], pts[nxt(j)][0] - pts[j][0]) for j in range(n)]
    A = np.zeros((2 * n, 2 * n))
    b = np.zeros(2 * n)

    def T(j):
        return j % n

    def P(k):  # phi на узле k: у замкнутой кривой узлы 0..n-1, у открытой 1..n
        return n + (k % n if closed else k - 1)

    knots = range(n) if closed else range(1, n)
    row = 0
    for k in knots:
        psi = _norm(ang[k % n] - ang[(k - 1) % n])
        A[row, T(k)] = 1
        A[row, P(k)] = 1
        b[row] = -psi
        row += 1
        A[row, P(k)] += 2 / d[(k - 1) % n]
        A[row, T(k - 1)] += 1 / d[(k - 1) % n]
        A[row, T(k)] -= 2 / d[k % n]
        A[row, P(k + 1)] -= 1 / d[k % n]
        row += 1
    if not closed:
        if start_dir is None:  # curl 1: кривизна у конца такая же, как у соседа
            A[row, T(0)] = 1
            A[row, P(1)] = -1
        else:
            A[row, T(0)] = 1
            b[row] = _norm(math.radians(start_dir) - ang[0])
        row += 1
        if end_dir is None:
            A[row, P(n)] = 1
            A[row, T(n - 1)] = -1
        else:
            A[row, P(n)] = 1
            b[row] = _norm(ang[n - 1] - math.radians(end_dir))
    sol = np.linalg.solve(A, b)
    return [(sol[T(j)], sol[P(j + 1)]) for j in range(n)]


def _piece(pts, start_dir, end_dir):
    if len(pts) == 2 and start_dir is None and end_dir is None:
        (x0, y0), (x1, y1) = pts  # отрезок между двумя углами без направлений — прямая
        return [((x0 + (x1 - x0) / 3, y0 + (y1 - y0) / 3), (x0 + 2 * (x1 - x0) / 3, y0 + 2 * (y1 - y0) / 3), (x1, y1))]
    angles = _solve(pts, False, start_dir, end_dir)
    return [(*_controls(pts[j], pts[j + 1], th, ph), pts[j + 1]) for j, (th, ph) in enumerate(angles)]


def path(knots, closed=True):
    """Возвращает (начальная точка, кубические сегменты) для foxlib.Shape.from_cubics."""
    ks = [k if isinstance(k, dict) else {"p": k} for k in knots]

    def is_corner(k):
        return k.get("corner") or "din" in k or "dout" in k

    corners = [i for i, k in enumerate(ks) if is_corner(k)]
    pts = [tuple(k["p"]) for k in ks]
    if closed and not corners:
        angles = _solve(pts, True)
        n = len(pts)
        return pts[0], [(*_controls(pts[j], pts[(j + 1) % n], th, ph), pts[(j + 1) % n]) for j, (th, ph) in enumerate(angles)]
    if closed:
        c0 = corners[0]
        ks = ks[c0:] + ks[:c0] + [ks[c0]]
        pts = [tuple(k["p"]) for k in ks]
    idx = [i for i, k in enumerate(ks) if is_corner(k)]
    if not closed:
        idx = sorted({0, len(ks) - 1, *idx})
    segs = []
    for a, z in zip(idx, idx[1:]):
        segs += _piece(pts[a:z + 1], ks[a].get("dout"), ks[z].get("din"))
    return pts[0], segs


def mirror_knots(right_half, cx=512.0):
    """Правая половина от точки на оси сверху до точки на оси снизу -> полный контур.

    Левая половина обходится в обратную сторону, поэтому направление a отражается в -a,
    а вход и выход меняются местами.
    """
    def mirror(k):
        k = k if isinstance(k, dict) else {"p": k}
        m = {"p": (2 * cx - k["p"][0], k["p"][1])}
        if k.get("corner"):
            m["corner"] = True
        if "din" in k:
            m["dout"] = (-k["din"]) % 360
        if "dout" in k:
            m["din"] = (-k["dout"]) % 360
        return m

    left = [mirror(k) for k in reversed(right_half[1:-1])]
    return list(right_half) + left
