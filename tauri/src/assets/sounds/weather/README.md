# Weather ambience

原本をそのまま配布せず、ゲームの環境音として加工したOgg Vorbisのみを格納する。取得元の個別ページとライセンスは2026-09-13に確認済み。ゲーム内表示の正は`src/data/credits.ts`。

| ファイル | 作者 | Freesound | ライセンス |
| --- | --- | --- | --- |
| city-ambience.ogg | klankbeeld | [City from pasture](https://freesound.org/people/klankbeeld/sounds/242539/) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |
| drizzle.ogg | olsonbock623 | [Rain with distant traffic](https://freesound.org/people/olsonbock623/sounds/442296/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |
| rain.ogg | vincefred | [Rain_03](https://freesound.org/people/vincefred/sounds/515839/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |
| heavy-rain.ogg | lebaston100 | [Rain without thunder](https://freesound.org/people/lebaston100/sounds/346562/) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |

44.1kHzへリサンプル、Vorbis品質3、-24 LUFS・最大-3dBTPへ音量調整。先頭と末尾の2秒を等電力クロスフェードして、録音の継ぎ目を滑らかにした。再生時間は各原本より2秒短い。共通環境音は常時、雨音は対応する天候のみ重ねる。通常雨の原本には遠雷も含まれる。

再生成（tauriディレクトリから。FFmpeg実行ファイルの場所を指定する）:

```powershell
python tools/prepare_weather_audio.py --source C:/Users/User/Downloads --ffmpeg <ffmpeg.exeの絶対パス>
```
