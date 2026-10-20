# Unity ambience

出所とライセンスは2026-09-13確認済み。ゲーム共通クレジットはtauri/src/data/credits.ts。

| ファイル | 作者 | Freesound | ライセンス |
| --- | --- | --- | --- |
| city-base.ogg | klankbeeld | [city pasture Den Bosch](https://freesound.org/people/klankbeeld/sounds/808956/) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |
| city-ambience.ogg | klankbeeld | [City from pasture](https://freesound.org/people/klankbeeld/sounds/242539/) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |
| drizzle.ogg | olsonbock623 | [Rain with distant traffic](https://freesound.org/people/olsonbock623/sounds/442296/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |
| rain.ogg | vincefred | [Rain_03](https://freesound.org/people/vincefred/sounds/515839/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |
| heavy-rain.ogg | lebaston100 | [Rain without thunder](https://freesound.org/people/lebaston100/sounds/346562/) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |

原本WAVからOgg Vorbis品質3・44.1kHzへ変換し、-24 LUFS正規化とループ境界2秒クロスフェードを行う。再生成: tauri/tools/prepare_weather_audio.py。Unity AudioImporterはStreaming。CameraAmbienceがベース・地表近景・雨音を再生する。近景閾値は地表から100m〜10m。
