"""Freesound原本をループ用Unity用Ogg Vorbisへ変換する。原本は公開リポジトリへコピーしない。"""
import argparse
import json
import pathlib
import subprocess

# 既存の作者・ライセンスを保持してUnityへ取り込む。
TRACKS = [
    ("808956__klankbeeld__city-pasture-den-bosch-nethernlands-1013-am-170328_1113.wav", "city-base", "klankbeeld", "CC BY 4.0"),
    ("242539__klankbeeld__city-from-pasture-nl-summer-140713_0298.wav", "city-ambience", "klankbeeld", "CC BY 4.0"),
    ("442296__olsonbock623__rain-with-distant-traffic.wav", "drizzle", "olsonbock623", "CC0 1.0"),
    ("515839__vincefred__rain_03-wav-24bit-96khz.wav", "rain", "vincefred", "CC0 1.0"),
    ("346562__lebaston100__rain-without-thunder.wav", "heavy-rain", "lebaston100", "CC BY 4.0"),
]

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=pathlib.Path, required=True)
    parser.add_argument("--ffmpeg", type=pathlib.Path, required=True)
    parser.add_argument("--track", help="指定した出力名だけ変換する（例: city-base）")
    args = parser.parse_args()
    output = pathlib.Path(__file__).resolve().parents[1] / "../unity/Assets/Resources/Audio/Ambience"
    output.mkdir(parents=True, exist_ok=True)
    for filename, key, author, license_name in TRACKS:
        if args.track and key != args.track:
            continue
        source = args.source / filename
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
        target = output / f"{key}.ogg"
        # Unityの標準AudioImporterで扱えるVorbisを原本から生成する。
        subprocess.run([str(args.ffmpeg), "-y", "-v", "error", "-i", str(source),
                        "-filter_complex", graph, "-map", "[out]", "-map_metadata", "-1",
                        "-ar", "44100", "-c:a", "libvorbis", "-q:a", "3",
                        "-metadata", f"artist={author}", "-metadata", f"copyright={license_name}",
                        "-metadata", f"comment=https://freesound.org/s/{filename.split('__')[0]}/; loop crossfade and loudness normalization",
                        str(target)], check=True)
        print(f"{key}: {source.stat().st_size:,} -> {target.stat().st_size:,} bytes", flush=True)

if __name__ == "__main__":
    main()
