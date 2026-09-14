# Unity vehicle audio

出所とライセンスは2026-09-15確認済み。ゲーム共通クレジットはtauri/src/data/credits.ts。

| ファイル | 用途 | 作者 | Freesound | ライセンス |
| --- | --- | --- | --- | --- |
| vehicle-car.ogg | 一般車・路線バスのエンジン(速度でピッチ変化) | GazarianGorik | [Engine_loop_NO-shepard-tone-effect.wav](https://freesound.org/people/GazarianGorik/sounds/628966/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |
| vehicle-train.ogg | 鉄道編成の走行(速度でゲイン変化) | ProductionNow | [Train Rumble and Rattle](https://freesound.org/people/ProductionNow/sounds/455045/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |

原本からOgg Vorbis品質3・44.1kHzへ変換し、-24 LUFS正規化とループ境界2秒クロスフェードを行う。再生成: tauri/tools/prepare_unity_audio.py(.metaも無ければ生成する)。Unity AudioImporterはCompressed In Memory・モノラル強制(同じクリップを複数の車両で同時に3D再生するため)。VehicleAmbienceが車両の位置から3Dループ再生する。
