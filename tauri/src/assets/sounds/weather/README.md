# Weather ambience

原本をそのまま配布せず、ゲームの環境音として加工したOgg Opusのみを格納する。取得元の個別ページとライセンスは2026-09-13に確認済み。ゲーム内表示の正は`src/data/credits.ts`。

| ファイル | 作者 | Freesound | ライセンス |
| --- | --- | --- | --- |
| city-base.opus | klankbeeld | [city pasture Den Bosch](https://freesound.org/people/klankbeeld/sounds/808956/) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |
| city-ambience.opus | klankbeeld | [City from pasture](https://freesound.org/people/klankbeeld/sounds/242539/) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |
| drizzle.opus | olsonbock623 | [Rain with distant traffic](https://freesound.org/people/olsonbock623/sounds/442296/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |
| rain.opus | vincefred | [Rain_03](https://freesound.org/people/vincefred/sounds/515839/) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |
| heavy-rain.opus | lebaston100 | [Rain without thunder](https://freesound.org/people/lebaston100/sounds/346562/) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |

-24 LUFS・最大-3dBTPへ音量調整し、Opus VBR 64kbps(heavy-rainのみ原本がモノラルなので40kbps)で符号化する。Opusは常に48kHz動作なので、リサンプル指定は不要。先頭と末尾の2秒を等電力クロスフェードして、録音の継ぎ目を滑らかにした。再生時間は各原本より2秒短い。共通環境音は常時、雨音は対応する天候のみ重ねる。通常雨の原本には遠雷も含まれる。

ベースは`city-base.opus`を常時再生する。`city-ambience.opus`は地表相対高度250m〜25mで徐々に増やし、低所ではベースより優勢にする。雨音は別レイヤー。[サウンドシステム](../../../../../docs/docs/programs/サウンドシステム.md)にミックス仕様をまとめている。

ベースの供給WAVは16.108604秒・4,639,906 bytes、変換後は100,583 bytes。原本ファイル名は`808956__klankbeeld__city-pasture-den-bosch-nethernlands-1013-am-170328_1113.wav`。5音源の合計は4,983,929 bytes。

再生成（tauriディレクトリから。FFmpeg実行ファイルの場所を指定する。`--track city-base`を追加するとベースだけを変換）:

```powershell
python tools/prepare_weather_audio.py --source C:/Users/User/Downloads --ffmpeg <ffmpeg.exeの絶対パス>
```
