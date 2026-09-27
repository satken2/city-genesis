# Weather

Unity の環境表現だけを扱う。天気・時刻・気候から空・光・霧・降水を決め、環境音・工事音・走行音も再生する。経済・マスター・セーブには書き込まない(気候区分だけは `GET /api/v1/city` の `climate` を読む)。

- 仕様の正は `geometries/U_天気と環境光.md` と `programs/サウンドシステム.md`(docs パスは `city-genesis/docs/docs/` 起点)。

## ファイル

| ファイル | 役割 |
| --- | --- |
| `WeatherPreset.cs` | 13種類の天気の見た目・降水量・平均持続時間の正。昼間の見た目で、太陽の向きと夕焼けは持たない(時刻が決める) |
| `WeatherClimate.cs` | 気候区分(`Constants.CLIMATE_*`)×季節(`current_date` の月)ごとの天気の出現割合(%)の表の正。各行の合計は100 |
| `WeatherChain.cs` | 隣り合う天気を1歩ずつ進むメトロポリス法の移り変わり。長時間の滞在割合が表に一致する。`Sample` は途中から見始めるときの選び直し |
| `WeatherClock.cs` | 描画用の時刻(日の出→日没600秒、夜は描かない)。`BeginNight`(日没1.5秒→黒幕0.5秒) / `RequestDawn`(黒幕解除0.5秒→朝1.5秒) / `Advance`(日の出を始めた瞬間だけ true)。環境光の`Darkness`と画面の`FadeAlpha`は独立 |
| `WeatherEpochTracker.cs` | 初回取得(セッション開始)と実際のエポック増分の区別。編集リロードでは何もしない |
| `WeatherController.cs` | AppRoot が生成。毎フレーム時刻・天気を進めて空・光・霧・環境光・降水へ反映。`epoch:prepare`で日没と黒幕を開始し、描画後に`epoch:darkened`を一度返す。`epoch:executing false`まで黒幕を保持。天気は`weather:query` / `weather:set`を受けて`weather:state`を返す |
| `WeatherPrecipitation.cs` | カメラ近傍だけの雨粒・雪片(`RainStreak.shader` / `Snowflake.shader`) |
| `CameraAmbience.cs` | 環境音。地表相対高度で近景音を混ぜ、雨量で隣り合う2段の雨音を配分する |
| `ConstructionAmbience.cs` | 工事現場の3Dループ音。`BuildingManager` / `PathManager` が `Register` / `Unregister` / `Clear` で現場を登録 |
| `VehicleAmbience.cs` | 走行音。`VehicleManager` / `TransitVehicleManager` が毎フレーム `Report` で通知し、1秒途切れた発音体は自動で外れる |

## 知らないと事故ること

- `WeatherClimate` の表を変えたら `U_天気と環境光.md` の表も同時に直す。隣接の組(`WeatherChain`)を変えたら全気候・全季節で `Connected` が真であること(QA が検査する)。
- 抽選は専用の `System.Random` を使い、`UnityEngine.Random` の状態に影響させない。
- 天気ID・出現割合・降水量の正は Unity、翻訳・絵文字は Tauri。`weather:set {id: null}` は固定解除。
- シェーダーは `Assets/Resources/Weather/`(`Atmosphere` / `RainStreak` / `Snowflake`)。ビルド時のストリッピングを避けるため Resources から直接ロードする。
- シーンの主光源を再利用し、追加の太陽を作らない。無効化時に元の環境を戻し、ランタイム所有アセットは破棄時に解放する。都市オブジェクトには触れない。
- 工事音の現場座標はバックエンド座標(FloatingOrigin のずれを含まない)で持ち、再生時にずれを足す。GameObject には紐づけない。
- 音量は `ambience:volume` の購読(受信前は無音)。`AudioListener.volume` は書き換えず、所有する AudioSource だけに適用する。
- F9 パネルのキー(F6/F7 天気、F10 固定、F11 選び直し、F3/F4 時刻、F5 停止)を増やすときは、カメラ・Steam 撮影(F12)・LOD統計(F8)と競合させない。
- `Tests/Weather/run-unity.ps1` は本フォルダの `*.cs` と `Constants.cs`・`Utils/PolylineUtils.cs`・`UI/UiFontProvider.cs` だけを写して単独コンパイルする。他ファイルへの依存を増やすと QA が壊れる。音声だけの検証は `run-vehicle-audio.ps1`。
