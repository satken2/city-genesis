# Weather

Unityの環境表現だけを扱う。経済シミュレーションの天気データや共有定数は作らない。

- `WeatherPreset`: 10種類の空・光の正。季節や都市日付とは独立。
- `WeatherEpochTracker`: 初回取得と実際のエポック増分を区別する。編集リロードでは天気を変更しない。
- `WeatherController`: AppRootが生成。IPCは既存イベントを購読し、ApiClient経由でcity.epochだけ読む。専用System.Randomで選び、UnityEngine.Randomの状態に影響しない。
- シェーダーは`Assets/Resources/Weather/Atmosphere.shader`。ビルド時にストリッピングされないようResourcesから直接ロードする。
- シーンの主光源を再利用する。無効化時に元の環境を戻し、ランタイムの所有アセットは破棄時に解放する。
- F9パネルはキーボード操作のみ。天気パネルのキーを追加するときはカメラ・Steam撮影キーと競合させない。

仕様は`docs/docs/geometries/U_天気と環境光.md`、独立したUnityでの検証は`Tests/Weather/run-unity.ps1`。
