# Weather

Unityの環境表現だけを扱う。経済シミュレーションの天気データや共有定数は作らない。

- `WeatherPreset`: 11種類の空・光・雨量・抽選ウェイトの正。季節や都市日付とは独立。
- `WeatherEpochTracker`: 初回取得と実際のエポック増分を区別する。編集リロードでは天気を変更しない。
- `WeatherController`: AppRootが生成。ApiClient経由でcity.epochだけ読む。専用System.Randomの重み付き独立抽選で、同一天気の連続を許す。UnityEngine.Randomの状態に影響しない。
- `weather:query` / `weather:set {id}`を受け、`weather:state`で現在値と候補をCockpitへ送る。nullの選択は固定解除。天候ID・ウェイト・雨量の正はUnity、翻訳・絵文字はTauri、環境音はUnity。
- `WeatherRain`: カメラ近傍だけで雨粒を描く。Resourcesの`RainStreak.shader`を使い、ゲーム外・無効化時に停止する。都市オブジェクトへの衝突判定や経済状態の変更はしない。
- `CameraAmbience`: AppRootが生成する環境音再生。Resources/Audio/AmbienceのStreamingクリップ5種類をループする。Utils.GetGroundHeightから地表相対高度を求め、100m〜10mで近景音を増やす。Tauriからは音量だけ受け取る。公開programs/サウンドシステム.mdを参照。
- シェーダーは`Assets/Resources/Weather/Atmosphere.shader`。ビルド時にストリッピングされないようResourcesから直接ロードする。
- シーンの主光源を再利用する。無効化時に元の環境を戻し、ランタイムの所有アセットは破棄時に解放する。
- F9パネルはキーボード操作のみ。天気パネルのキーを追加するときはカメラ・Steam撮影キーと競合させない。

仕様は`docs/docs/geometries/U_天気と環境光.md`、独立したUnityでの検証は`Tests/Weather/run-unity.ps1`。
