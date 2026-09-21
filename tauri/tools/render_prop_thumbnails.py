"""数値IDの通常版FBXをBlenderで撮影する。原本・FBXは変更しない。"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import traceback

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_SOURCE = ROOT / "unity/Assets/Models/Props"
DEFAULT_OUTPUT = ROOT / "tauri/src/assets/images/props"


def write_json(path, value):
    path = Path(path)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


def render_model(source, output, size, samples):
    import bpy
    from mathutils import Vector

    bpy.ops.wm.read_homefile(use_empty=True)
    bpy.ops.outliner.orphans_purge(do_recursive=True)
    bpy.ops.import_scene.fbx(filepath=str(source), use_anim=False)
    # 別マシンで書き出された絶対パスも、FBXに同梱された画像へ解決する。
    for image in bpy.data.images:
        if image.source != "FILE" or image.packed_file:
            continue
        name = image.filepath.replace("\\", "/").split("/")[-1]
        candidates = [source.parent / "Textures" / name, source.parent / name]
        local = next((path for path in candidates if path.is_file()), None)
        if local:
            image.filepath = str(local)
            image.reload()
        elif not Path(bpy.path.abspath(image.filepath)).is_file():
            raise RuntimeError(f"画像が見つかりません: {image.filepath}")

    bpy.context.view_layer.update()
    points = [obj.matrix_world @ Vector(corner) for obj in bpy.context.scene.objects
              if obj.type == "MESH" for corner in obj.bound_box]
    if not points:
        raise RuntimeError(f"メッシュがありません: {source}")
    low = Vector(tuple(min(p[i] for p in points) for i in range(3)))
    high = Vector(tuple(max(p[i] for p in points) for i in range(3)))
    center = (low + high) / 2
    radius = max((high - low).length / 2, 0.01)
    scene = bpy.context.scene
    camera_data = bpy.data.cameras.new("ThumbnailCamera")
    camera = bpy.data.objects.new("ThumbnailCamera", camera_data)
    scene.collection.objects.link(camera)
    scene.camera = camera
    camera.location = center + Vector((1, -1, 0.75)).normalized() * radius * 3
    camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    camera_data.type = "ORTHO"
    rotation = camera.rotation_euler.to_matrix().transposed()
    projected = [rotation @ (p - center) for p in points]
    camera_data.ortho_scale = max(max(p[i] for p in projected) - min(p[i] for p in projected)
                                  for i in (0, 1)) * 1.15
    camera_data.clip_start = max(radius * 0.001, 0.001)
    camera_data.clip_end = radius * 10
    sun_data = bpy.data.lights.new("ThumbnailSun", type="SUN")
    sun_data.energy = 3
    sun = bpy.data.objects.new("ThumbnailSun", sun_data)
    scene.collection.objects.link(sun)
    sun.rotation_euler = (0.87, 0, 0.52)
    world = bpy.data.worlds.new("ThumbnailWorld")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.9, 0.9, 0.9, 1)
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.6
    scene.world = world
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.render.resolution_x = scene.render.resolution_y = size
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    # 書きかけのPNGをUIや次回の差分判定に渡さない。
    temporary = output.with_name(output.stem + ".render.png")
    scene.render.filepath = str(temporary)
    bpy.ops.render.render(write_still=True)
    temporary.replace(output)


def worker(job_path):
    job = json.loads(Path(job_path).read_text(encoding="utf-8"))
    result = {"completed": [], "errors": {}, "done": False, "pid": os.getpid()}
    write_json(job["result"], result)
    try:
        for task in job["tasks"]:
            try:
                render_model(Path(task["source"]), Path(task["output"]), job["size"], job["samples"])
                result["completed"].append(task["id"])
            except Exception:
                result["errors"][task["id"]] = traceback.format_exc()
            write_json(job["result"], result)
    finally:
        result["done"] = True
        write_json(job["result"], result)


def worker_alive(pid):
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.OpenProcess(0x100000, False, pid)
        if not handle:
            return False
        try:
            return kernel.WaitForSingleObject(handle, 0) == 258
        finally:
            kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--blender", default=os.environ.get("BLENDER"))
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--only", help="完全一致のIDをカンマ区切りで指定（例: 1,2,121）")
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--size", type=int, default=256)
    parser.add_argument("--samples", type=int, default=24)
    parser.add_argument("--timeout", type=int, default=1800, help="Blender待機の上限秒")
    args = parser.parse_args()
    if args.size < 16 or args.samples < 1 or args.timeout < 1:
        parser.error("sizeは16以上、samplesとtimeoutは1以上を指定してください")
    source, output = args.source.resolve(), args.output.resolve()
    models = {p.stem: p for p in source.glob("*.fbx") if p.stem.isascii() and p.stem.isdecimal()}
    if args.only:
        requested = set(args.only.split(","))
        if requested - models.keys():
            parser.error(f"通常版FBXがありません: {', '.join(sorted(requested - models.keys()))}")
        models = {key: path for key, path in models.items() if key in requested}
    if not models:
        parser.error(f"数値IDのFBXがありません: {source}")
    output.mkdir(parents=True, exist_ok=True)
    cache_path = output / ".thumbnail-cache.json"
    cache = json.loads(cache_path.read_text(encoding="utf-8")) if cache_path.exists() else {}
    # 画像の差し替えも検出する。共有Textures全体を含め、参照画像の見落としを避ける。
    common = hashlib.sha256(Path(__file__).read_bytes())
    common.update(f"{args.size}:{args.samples}".encode())
    for texture in sorted((source / "Textures").glob("*")):
        if texture.is_file() and texture.suffix != ".meta":
            common.update(texture.name.encode())
            common.update(texture.read_bytes())
    hashes, tasks = {}, []
    for key in sorted(models, key=int):
        digest = common.copy()
        digest.update(models[key].read_bytes())
        hashes[key] = digest.hexdigest()
        target = output / f"{key}.png"
        if args.force or cache.get(key) != hashes[key] or not target.exists():
            tasks.append({"id": key, "source": str(models[key]), "output": str(target)})
    if not tasks:
        print(f"Up to date: {len(models)} thumbnails")
        return 0
    blender = args.blender or shutil.which("blender") or shutil.which("blender-launcher.exe")
    if not blender:
        parser.error("Blenderが見つかりません。--blender または環境変数BLENDERで指定してください")
    errors = {}
    completed = 0
    # Cyclesのネイティブ資源を確実に解放するため、モデルごとにプロセスを終える。
    for offset in range(0, len(tasks)):
        batch = tasks[offset:offset + 1]
        with tempfile.TemporaryDirectory(prefix="prop-thumbnails-", ignore_cleanup_errors=True) as directory:
            job_path, result_path = Path(directory) / "job.json", Path(directory) / "result.json"
            write_json(job_path, {"tasks": batch, "size": args.size, "samples": args.samples, "result": str(result_path)})
            command = [blender, "--background", "--factory-startup", "--python", str(Path(__file__).resolve()),
                       "--", "--worker", str(job_path)]
            process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            start, count = time.monotonic(), -1
            result = {"completed": [], "errors": {}, "done": False}
            while time.monotonic() - start < args.timeout:
                if result_path.exists():
                    result = json.loads(result_path.read_text(encoding="utf-8"))
                    progress = len(result["completed"]) + len(result["errors"])
                    if progress != count:
                        print(f"Rendered {offset + progress}/{len(tasks)}", flush=True)
                        count = progress
                        for key in result["completed"]:
                            cache[key] = hashes[key]
                        write_json(cache_path, cache)
                    if result["done"]:
                        break
                    if not worker_alive(result["pid"]):
                        if json.loads(result_path.read_text(encoding="utf-8"))["done"]:
                            continue
                        raise RuntimeError("Blenderが処理途中で終了しました。完了分は保存済みです。再実行してください")
                if process.poll() not in (None, 0):
                    raise RuntimeError(f"Blender終了コード: {process.returncode}")
                # Store版ランチャーは処理の完了前にexit 0で戻るため、結果ファイルを待つ。
                time.sleep(0.5)
            else:
                process.terminate()
                raise TimeoutError("Blenderの完了待ちがタイムアウトしました")
            process.wait(timeout=30)
            completed += len(result["completed"])
            errors.update(result["errors"])
    for key, error in errors.items():
        print(f"FAILED {key}: {error}", file=sys.stderr)
    print(f"Generated {completed} thumbnails in {output}")
    return 1 if errors else 0



if __name__ == "__main__":
    if "--worker" in sys.argv:
        worker(sys.argv[sys.argv.index("--worker") + 1])
    else:
        raise SystemExit(main())
