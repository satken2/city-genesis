# 単発効果音

## building-explosion.ogg

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

## transit-point-place.ogg

`transit-point-place.ogg` は、公共交通の路線設置エディタ(`TransitPlanManager`)で停留所・経由点を置いたとき(クリックでの追加・挿入と、ドラッグで動かして離したとき)に再生する。UIの操作音なので2Dで鳴らし、音量はUIの効果音設定(`ambience:volume`)に従う。

- 原音: [drip.wav — Nightflame / Freesound](https://freesound.org/people/Nightflame/sounds/188642/)
- ライセンス: [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)。2026-09-26に配布ページを確認。商用利用・改変・再配布可。
- 原本: `188642__nightflame__drip.wav`、44.1 kHz / 16 bit / mono、0.081秒、7,172 bytes(配布ページの長さ0:00.080・7.0 KBと一致)。
- 配布用: 44.1 kHz / mono / Ogg Vorbis quality 4、4,162 bytes。長さと音量は維持。原本WAVはリポジトリに含めない。
- Unityでは短い単発音の遅延を避けるため、Decompress On Load・Preload Audio Dataを使用する。
- ゲーム内の掲載データは `tauri/src/data/credits.ts`。

```powershell
ffmpeg -i 188642__nightflame__drip.wav -map_metadata -1 -ac 1 -ar 44100 -c:a libvorbis -q:a 4 transit-point-place.ogg
```
