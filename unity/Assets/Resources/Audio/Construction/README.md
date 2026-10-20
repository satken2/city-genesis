# Unity construction site audio

出所とライセンスは2026-09-15確認済み。ゲーム共通クレジットはtauri/src/data/credits.ts。

| ファイル | 用途 | 作者 | Freesound | ライセンス |
| --- | --- | --- | --- | --- |
| construction-building.ogg | 建物の建設中・解体中 | SamuelGremaud | [CONSTRUCTION WORK](https://freesound.org/people/SamuelGremaud/sounds/544335/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |
| construction-path.ogg | 道路・線路の工事中 | BO_Nanuz | [Construction Site](https://freesound.org/people/BO_Nanuz/sounds/753879/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |

原本からOgg Vorbis品質3・44.1kHzへ変換し、-24 LUFS正規化とループ境界2秒クロスフェードを行う。再生成: tauri/tools/prepare_unity_audio.py。Unity AudioImporterはCompressed In Memory・モノラル強制(同じクリップを複数の現場で同時に3D再生するため、Streamingにしない)。ConstructionAmbienceが工事中の建物・パスの位置から3Dループ再生する。
