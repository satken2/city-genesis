import argparse
import json
from pathlib import Path

import numpy as np


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="meta.json のグリッド寸法をもとに、height map の bin ファイルを .npy に変換する"
    )
    parser.add_argument(
        "--meta",
        default="meta.json",
        help="グリッド寸法(grid_rows, grid_cols)が書かれた meta.json のパス(デフォルト: meta.json)",
    )
    parser.add_argument(
        "--bin",
        default="near.bin",
        help="変換元の bin ファイルのパス。float16 の配列として読み込む(デフォルト: near.bin)",
    )
    parser.add_argument(
        "--out",
        default="map_my_island.npy",
        help="保存先の .npy ファイルのパス(デフォルト: map_my_island.npy)",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()

    # meta.json からグリッド寸法を読み込む
    with open(args.meta, "r") as f:
        meta = json.load(f)

    rows = meta["grid_rows"]
    cols = meta["grid_cols"]

    # bin ファイルを float16 の NumPy 配列として読み込む
    with open(args.bin, "rb") as f:
        data = np.frombuffer(f.read(), dtype=np.float16).reshape(rows, cols)

    # .npy として保存
    out_path = Path(args.out)
    np.save(out_path, data)
    print(f"saved: {out_path}")


if __name__ == "__main__":
    main()
