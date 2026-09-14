"""Freesound原本をループ用Unity用Ogg Vorbisへ変換する。原本は公開リポジトリへコピーしない。"""
import argparse
import hashlib
import json
import pathlib
import subprocess

# (原本ファイル名のglob, 出力名, Resources/Audio内の出力フォルダ, 作者, ライセンス)。
# 原本はFreesoundのダウンロード名(先頭がサウンドID)で照合する。作者・ライセンスはOggのメタデータに埋め込む。
TRACKS = [
    ("808956__*.wav", "city-base", "Ambience", "klankbeeld", "CC BY 4.0"),
    ("242539__*.wav", "city-ambience", "Ambience", "klankbeeld", "CC BY 4.0"),
    ("442296__*.wav", "drizzle", "Ambience", "olsonbock623", "CC0 1.0"),
    ("515839__*.wav", "rain", "Ambience", "vincefred", "CC0 1.0"),
    ("346562__*.wav", "heavy-rain", "Ambience", "lebaston100", "CC BY 4.0"),
    ("544335__*.wav", "construction-building", "Construction", "SamuelGremaud", "CC0 1.0"),
    ("753879__*.mp3", "construction-path", "Construction", "BO_Nanuz", "CC0 1.0"),
    ("628966__*", "vehicle-car", "Vehicles", "GazarianGorik", "CC0 1.0"),
    ("455045__*", "vehicle-train", "Vehicles", "ProductionNow", "CC0 1.0"),
]

# 長い環境音は常駐させずStreaming、同じクリップを複数の位置で同時に3D再生する短いループはCompressed In Memory+モノラル。
STREAMING_FOLDERS = {"Ambience"}

META = """fileFormatVersion: 2
guid: {guid}
AudioImporter:
  externalObjects: {{}}
  serializedVersion: 8
  defaultSettings:
    serializedVersion: 2
    loadType: {load_type}
    sampleRateSetting: 0
    sampleRateOverride: 44100
    compressionFormat: 1
    quality: 0.5
    conversionMode: 0
    preloadAudioData: 0
  platformSettingOverrides: {{}}
  forceToMono: {mono}
  normalize: 1
  loadInBackground: 0
  ambisonic: 0
  3D: 1
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def write_meta(target: pathlib.Path, folder: str):
    """UnityのAudioImporter設定。既存のmetaはGUID参照を壊さないよう触らない。"""
    meta = target.with_name(target.name + ".meta")
    if meta.exists():
        return
    streaming = folder in STREAMING_FOLDERS
    guid = hashlib.md5(f"city-genesis/audio/{folder}/{target.name}".encode()).hexdigest()
    meta.write_text(META.format(guid=guid, load_type=2 if streaming else 1, mono=0 if streaming else 1), newline="\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=pathlib.Path, required=True)
    parser.add_argument("--ffmpeg", type=pathlib.Path, required=True)
    parser.add_argument("--track", help="指定した出力名だけ変換する（例: city-base）")
    args = parser.parse_args()
    audio_root = pathlib.Path(__file__).resolve().parents[1] / "../unity/Assets/Resources/Audio"
    for pattern, key, folder, author, license_name in TRACKS:
        if args.track and key != args.track:
            continue
        matches = sorted(args.source.glob(pattern))
        if not matches:
            raise SystemExit(f"{key}: 原本が見つかりません: {args.source / pattern}")
        source = matches[0]
        info = json.loads(subprocess.check_output([
            str(args.ffmpeg.with_name("ffprobe.exe")), "-v", "error", "-show_entries", "format=duration",
            "-of", "json", str(source)], text=True))
        end = float(info["format"]["duration"]) - 2
        # 末尾と先頭を等電力クロスフェードし、継ぎ目を曲中に移してループ端を連続させる。
        graph = (f"[0:a]asplit=3[b][t][h];[b]atrim=start=2:end={end},asetpts=PTS-STARTPTS[body];"
                 f"[t]atrim=start={end},asetpts=PTS-STARTPTS[tail];"
                 "[h]atrim=end=2,asetpts=PTS-STARTPTS[head];"
                 "[tail][head]acrossfade=d=2:c1=qsin:c2=qsin[seam];"
                 "[body][seam]concat=n=2:v=0:a=1,loudnorm=I=-24:TP=-3:LRA=11[out]")
        output = audio_root / folder
        output.mkdir(parents=True, exist_ok=True)
        target = output / f"{key}.ogg"
        # Unityの標準AudioImporterで扱えるVorbisを原本から生成する。
        subprocess.run([str(args.ffmpeg), "-y", "-v", "error", "-i", str(source),
                        "-filter_complex", graph, "-map", "[out]", "-map_metadata", "-1",
                        "-ar", "44100", "-c:a", "libvorbis", "-q:a", "3",
                        "-metadata", f"artist={author}", "-metadata", f"copyright={license_name}",
                        "-metadata", f"comment=https://freesound.org/s/{source.name.split('__')[0]}/; loop crossfade and loudness normalization",
                        str(target)], check=True)
        write_meta(target, folder)
        print(f"{key}: {source.stat().st_size:,} -> {target.stat().st_size:,} bytes", flush=True)


if __name__ == "__main__":
    main()
