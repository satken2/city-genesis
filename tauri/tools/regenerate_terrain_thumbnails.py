"""terrain_maps.yamlに登録されたゲーム用地形の透過PNGを再生成する。

必要環境: Python (numpy, Pillow, PyYAML)、Node.js、tauriでnpm install済み。
file_nameは登録表の親の親(assets)を基準に解決する。標高・登録表・原本は変更しない。
"""

import argparse
import json
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile

import numpy as np
from PIL import Image
import yaml


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("registry", type=Path, help="ゲーム用terrain_maps.yamlのパス")
    parser.add_argument("--map-id", action="append", help="対象ID。省略時は登録済みの全マップ")
    args = parser.parse_args()
    registry = args.registry.resolve(strict=True)
    if registry.name != "terrain_maps.yaml":
        parser.error("ゲーム用のterrain_maps.yamlを指定してください")
    definitions = yaml.safe_load(registry.read_text(encoding="utf-8"))
    selected = args.map_id or list(definitions)
    unknown = set(selected) - definitions.keys()
    if unknown:
        parser.error(f"未登録のマップID: {sorted(unknown)}")
    node = shutil.which("node")
    if not node:
        parser.error("Node.jsが見つかりません")
    assets = registry.parent.parent
    originals = (registry.parent / "original").resolve()
    jobs = []
    for map_id in selected:
        definition = definitions[map_id]
        source = (assets / definition["file_name"]).resolve(strict=True)
        target = source.with_suffix(".png").resolve()
        if not source.is_relative_to(assets) or not target.is_relative_to(assets):
            parser.error(f"assets外の地形は処理できません: {map_id}")
        if source.is_relative_to(originals) or target.is_relative_to(originals):
            parser.error(f"原本への画像出力は禁止です: {map_id}")
        jobs.append((map_id, definition, source, target))

    renderer = Path(__file__).with_name("terrain-thumbnail-render.mjs")
    for map_id, definition, source, target in jobs:
        elev = np.load(source, allow_pickle=False)
        if elev.ndim != 2 or min(elev.shape) < 2 or not np.isfinite(elev).all():
            raise ValueError(f"不正な標高配列: {source}")
        settings = definition["settings"]
        metadata = {
            "rows": elev.shape[0],
            "cols": elev.shape[1],
            "mapWidth": settings["map_width"],
            "mapHeight": settings["map_height"],
            "spacing": definition["src_spacing"],
            "groundCover": definition.get("ground_cover"),
        }
        payload = json.dumps(metadata).encode("utf-8") + b"\n" + elev.astype("<f4").tobytes(order="C")
        result = subprocess.run([node, str(renderer)], input=payload, stdout=subprocess.PIPE, check=True)
        width, height = struct.unpack("<II", result.stdout[:8])
        image = Image.frombytes("RGBA", (width, height), result.stdout[8:])
        # エンコード失敗で既存PNGを失わないよう、完成したファイルだけを置き換える。
        with tempfile.NamedTemporaryFile(dir=target.parent, suffix=".png", delete=False) as temporary:
            temp_path = Path(temporary.name)
        try:
            image.save(temp_path, format="PNG", optimize=True)
            temp_path.replace(target)
        finally:
            temp_path.unlink(missing_ok=True)
        print(f"{map_id}: {width}x{height} -> {target.name}", flush=True)
    print(f"Generated {len(jobs)} terrain thumbnails.")


if __name__ == "__main__":
    main()
