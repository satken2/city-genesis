# Unity vehicle audio

出所とライセンスは2026-09-15確認済み。ゲーム共通クレジットはtauri/src/data/credits.ts。

| ファイル | 用途 | 作者 | Freesound | ライセンス |
| --- | --- | --- | --- | --- |
| vehicle-car.ogg | 一般車・路線バスのエンジン(速度でピッチ変化) | GazarianGorik | [Engine_loop_NO-shepard-tone-effect.wav](https://freesound.org/people/GazarianGorik/sounds/628966/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |
| vehicle-train.ogg | 鉄道編成の走行(音程固定・速度でテンポとゲイン変化) | 開発者の自録音 | 対象外 | 自作素材・外部クレジット不要 |
| vehicle-train-motor.ogg | モーター音(速度で再生速度・音程・ゲイン変化) | JotrainG | [Electric Train Motor Idle - Loop (Queensland Rail InterCity Express)](https://freesound.org/people/JotrainG/sounds/700034/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |

原本からOgg Vorbis品質3・44.1kHzへ変換し、-24 LUFS正規化とループ境界2秒クロスフェードを行う。再生成: tauri/tools/prepare_unity_audio.py(.metaも無ければ生成する)。Unity AudioImporterはCompressed In Memory・モノラル強制(同じクリップを複数の車両で同時に3D再生するため)。VehicleAmbienceが車両の位置から3Dループ再生する。

鉄道の原本は開発者提供の `0e06-1043-43d7-bc07-118a8ae6a481.mp3` と `700034__jotraing__electric-train-motor-idle-loop-queensland-rail-intercity-express.wav`。自録音であることは2026-09-20の開発者申告、モーター音のCC0は同日の配布ページ確認による。モーター音は2.432秒の完成済みループなのでクロスフェードせず正規化のみ行う。走行音は`atempo`で音程を保って0.65倍(`vehicle-train-slow.ogg`)・1.5倍(`vehicle-train-fast.ogg`)も生成する。実行時は3つのテンポを音量補間し、走行音のpitchは1で固定する。モーター音だけピッチと再生速度を同時に変える。
