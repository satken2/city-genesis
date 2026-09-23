# 単発効果音

`building-explosion.ogg` は、民間建物の爆破操作が成功したときだけ `VisualEffectManager.PlayExplosion` から再生する。

- 原音: [Explode001 — mitchelk / Freesound](https://freesound.org/people/mitchelk/sounds/136765/)
- ライセンス: [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)。2026-09-23に配布ページを確認。商用利用・改変・再配布可。
- 原本: `136765__mitchelk__explode001.wav`、44.1 kHz / 16 bit / stereo、4.054694秒、715,324 bytes。
- 配布用: 44.1 kHz / mono / Ogg Vorbis quality 4、35,450 bytes。長さと音量は維持し、3D発音体向けにモノラル化。原本WAVはリポジトリに含めない。
- Unityでは短い単発音の遅延を避けるため、Decompress On Load・Preload Audio Dataを使用する。
- ゲーム内の掲載データは `tauri/src/data/credits.ts`。

```powershell
ffmpeg -i 136765__mitchelk__explode001.wav -map_metadata -1 -ac 1 -ar 44100 -c:a libvorbis -q:a 4 building-explosion.ogg
```
