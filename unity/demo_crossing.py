from dataclasses import dataclass
from typing import List, Tuple, Union

import matplotlib.pyplot as plt
from shapely.geometry import Polygon
from shapely.ops import unary_union
import math


@dataclass
class PathData:
    start_point: Tuple[float, float]  # (x, z)
    end_point: Tuple[float, float]    # (x, z)
    start_node: int
    end_node: int
    width: float


def _make_road_rect(a: Tuple[float, float],
                    b: Tuple[float, float],
                    width: float) -> Polygon:
    ax, az = a
    bx, bz = b
    dx = bx - ax
    dz = bz - az
    length = math.hypot(dx, dz)
    if length == 0:
        raise ValueError("start_point と end_point が同じ")
    ux = dx / length
    uz = dz / length
    nx = -uz
    nz = ux
    hw = width / 2.0

    p1 = (ax + nx * hw, az + nz * hw)
    p2 = (ax - nx * hw, az - nz * hw)
    p3 = (bx - nx * hw, bz - nz * hw)
    p4 = (bx + nx * hw, bz + nz * hw)
    return Polygon([p1, p2, p3, p4])


def compute_intersection_polygon(
    intersection_node_id: int,
    intersection_pos: Tuple[float, float],
    paths: List[PathData]
) -> Union[Polygon, 'shapely.geometry.MultiPolygon']:
    """
    処理概要

    - 交差点に接続している道路だけを抽出。
    - 各道路を中心線＋幅から長方形ポリゴンに変換。
    - すべての道路ペアについてポリゴン交差を取り、「2 本以上重なる領域」を unary_union でまとめる（最小交差範囲）。
    - 各道路ごとに
      - その道路ポリゴンと交差範囲の交差ポリゴンを計算。
      - 交差点側の端点から道路方向ベクトルに射影して、交差範囲内で最も遠い点の距離 max_t を求める。
      - その max_t を通る「道路方向に直交する線」をカットラインとし、その線より交差点側だけを残す半平面ポリゴンで道路長方形をクリッピング。
    - すべてのクリップ済み道路ポリゴンを unary_union して、交差点範囲ポリゴンを得る。
    - さらに全道路の終端面の角をぐるっと結んだ多角形を作り、それも交差点ポリゴンに含める。
    """
    ix, iz = intersection_pos

    connected_paths = [
        p for p in paths
        if p.start_node == intersection_node_id or p.end_node == intersection_node_id
    ]
    if len(connected_paths) < 2:
        raise ValueError("少なくとも2本の道路が必要")

    road_polys = []
    for p in connected_paths:
        if p.start_node == intersection_node_id:
            a = p.start_point
            b = p.end_point
        else:
            a = p.end_point
            b = p.start_point
        road_polys.append((p, a, b, _make_road_rect(a, b, p.width)))

    pair_inters = []
    n = len(road_polys)
    for i in range(n):
        for j in range(i + 1, n):
            poly_i = road_polys[i][3]
            poly_j = road_polys[j][3]
            inter = poly_i.intersection(poly_j)
            if not inter.is_empty and inter.area > 1e-6:
                pair_inters.append(inter)

    if not pair_inters:
        return Polygon([(ix, iz)])

    overlap_region = unary_union(pair_inters)

    clipped_roads = []
    terminal_corners: List[Tuple[float, float]] = []

    for p, a, b, poly in road_polys:
        inter = poly.intersection(overlap_region)
        if inter.is_empty or inter.area < 1e-6:
            continue

        if (abs(a[0] - ix) < 1e-6 and abs(a[1] - iz) < 1e-6):
            start = a
            end = b
        else:
            start = b
            end = a
        sx, sz = start
        ex, ez = end
        vx = ex - sx
        vz = ez - sz
        vlen2 = vx * vx + vz * vz
        if vlen2 == 0:
            continue

        def collect_coords(geom):
            if geom.geom_type == "Polygon":
                for x, z in geom.exterior.coords:
                    yield x, z
            elif geom.geom_type == "MultiPolygon":
                for g in geom.geoms:
                    for x, z in g.exterior.coords:
                        yield x, z
            elif geom.geom_type in ("LineString", "LinearRing"):
                for x, z in geom.coords:
                    yield x, z
            elif geom.geom_type == "GeometryCollection":
                for g in geom.geoms:
                    yield from collect_coords(g)

        max_t = 0.0
        vlen = math.sqrt(vlen2)
        for x, z in collect_coords(inter):
            dx = x - sx
            dz = z - sz
            t = (dx * vx + dz * vz) / vlen
            if t > max_t:
                max_t = t

        if max_t <= 0:
            continue

        ux = vx / vlen
        uz = vz / vlen
        nx = -uz
        nz = ux
        cut_x = sx + ux * max_t
        cut_z = sz + uz * max_t

        # 終端面の2頂点
        hw = p.width / 2.0
        corner1 = (cut_x + nx * hw, cut_z + nz * hw)
        corner2 = (cut_x - nx * hw, cut_z - nz * hw)
        terminal_corners.append(corner1)
        terminal_corners.append(corner2)

        L = 1e4
        p1 = (cut_x + nx * L, cut_z + nz * L)
        p2 = (cut_x - nx * L, cut_z - nz * L)
        p3 = (cut_x - nx * L - ux * L, cut_z - nz * L - uz * L)
        p4 = (cut_x + nx * L - ux * L, cut_z + nz * L - uz * L)
        half_plane = Polygon([p1, p2, p3, p4])

        clipped = poly.intersection(half_plane)
        if not clipped.is_empty and clipped.area > 1e-6:
            clipped_roads.append(clipped)

    if not clipped_roads:
        base_poly = overlap_region
    else:
        base_poly = unary_union(clipped_roads)

    # 終端面の外周多角形をジョイン
    if len(terminal_corners) >= 3:
        def angle_from_center(pt):
            x, z = pt
            return math.atan2(z - iz, x - ix)

        # 時計回り
        sorted_corners = sorted(terminal_corners,
                                key=angle_from_center,
                                reverse=True)
        end_polygon = Polygon(sorted_corners)
        if end_polygon.is_valid and not end_polygon.is_empty and end_polygon.area > 1e-6:
            base_poly = unary_union([base_poly, end_polygon])

    return base_poly




def demo_plot():
    paths = [
        PathData(start_point=(0, 0), end_point=(10, 0), start_node=1, end_node=2, width=2),
        PathData(start_point=(0, 0), end_point=(0, 10), start_node=1, end_node=3, width=3),
        PathData(start_point=(0, 0), end_point=(8, 8), start_node=1, end_node=4, width=2),
    ]
    intersection_node_id = 1
    intersection_pos = (0.0, 0.0)

    poly = compute_intersection_polygon(intersection_node_id, intersection_pos, paths)

    fig, ax = plt.subplots()

    # 全道路
    for p in paths:
        rect = _make_road_rect(p.start_point, p.end_point, p.width)
        x, y = rect.exterior.xy
        ax.fill(x, y, alpha=0.2)
        ax.plot([p.start_point[0], p.end_point[0]],
                [p.start_point[1], p.end_point[1]])

    # 交差点ポリゴン（濃い塗りつぶし）
    if poly.geom_type == "Polygon":
        xs, ys = poly.exterior.xy
        ax.fill(xs, ys, alpha=0.3)
    else:
        for g in poly.geoms:
            xs, ys = g.exterior.xy
            ax.fill(xs, ys, alpha=0.3)

    terminal_points = []

    for p in paths:
        if p.start_node == intersection_node_id:
            a = p.start_point
            b = p.end_point
        else:
            a = p.end_point
            b = p.start_point

        ax_, az_ = a
        bx_, bz_ = b
        dx = bx_ - ax_
        dz = bz_ - az_
        L = math.hypot(dx, dz)
        ux = dx / L
        uz = dz / L
        nx = -uz
        nz = ux
        hw = p.width / 2

        # 交差点側終端（a で固定）
        corner1 = (ax_ + nx * hw, az_ + nz * hw)
        corner2 = (ax_ - nx * hw, az_ - nz * hw)

        terminal_points.append(corner1)
        terminal_points.append(corner2)

    def angle(pt):
        return math.atan2(pt[1] - intersection_pos[1], pt[0] - intersection_pos[0])

    pts_sorted = sorted(terminal_points, key=angle, reverse=True)
    xs = [p[0] for p in pts_sorted] + [pts_sorted[0][0]]
    ys = [p[1] for p in pts_sorted] + [pts_sorted[0][1]]

    ax.plot(xs, ys, color="red", linewidth=2, linestyle="--")

    ax.set_aspect('equal', 'box')
    ax.set_xlabel("x")
    ax.set_ylabel("z")
    ax.set_title("Intersection area demo")
    plt.show()


if __name__ == "__main__":
    demo_plot()