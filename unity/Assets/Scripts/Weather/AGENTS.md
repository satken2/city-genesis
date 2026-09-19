# Weather

Unityの環境表現だけを扱う。経済シミュレーションの天気データや共有定数は作らない。

- `WeatherPreset`: 11種類の空・光・雨量・抽選ウェイトの正。季節や都市日付とは独立。
- `WeatherEpochTracker`: 初回取得と実際のエポック増分を区別する。編集リロードでは天気を変更しない。
- `WeatherController`: AppRootが生成。ApiClient経由でcity.epochだけ読む。専用System.Randomの重み付き独立抽選で、同一天気の連続を許す。UnityEngine.Randomの状態に影響しない。
- `weather:query` / `weather:set {id}`を受け、`weather:state`で現在値と候補をCockpitへ送る。nullの選択は固定解除。天候ID・ウェイト・雨量の正はUnity、翻訳・絵文字はTauri、環境音はUnity。
- `WeatherRain`: カメラ近傍だけで雨粒を描く。Resourcesの`RainStreak.shader`を使い、ゲーム外・無効化時に停止する。都市オブジェクトへの衝突判定や経済状態の変更はしない。
- `ConstructionAmbience`: AppRootが生成する工事音再生。BuildingManager/PathManagerがデータキャッシュ同期時に `Register/Unregister/Clear` で現場(バックエンド座標の点列。建物=中心1点、パス=施工先端の1点)を登録し、0.25秒ごとにカメラから250m以内の近い順8件を3Dループ再生する(音源プール8本・Linear減衰15〜250m・ドップラー0・0.5秒フェード・Tauriから受けた音量に基準ゲイン `Gain`=0.6を掛ける)。音源はResources/Audio/Construction。GameObjectに紐づけない。座標はFloatingOriginのずれを含まず、ルート直下の不動アンカーの位置をずれとして足す。
- `VehicleAmbience`: AppRootが生成する走行音再生。VehicleManager(一般車=Kind.Car)とTransitVehicleManager(路線バス=Kind.Bus・鉄道編成=Kind.Train)が毎フレーム `Report(kind, id, ワールド座標, 速度)` で通知し、1秒通知が無い発音体は自動で外れる(`Remove`/`Clear`は即時)。車6本+鉄道3編成×4音源（走行音3テンポ＋モーター）の音源プールで近い順に割り当て(車120m・鉄道300m)、車はピッチ=`CarPitch(速度)`、鉄道は自録の走行音とモーター音を重ね、`TrainTempo`で音程固定の0.65倍・等倍・1.5倍ループを混ぜる。走行音のpitchは常に1。`TrainMotorPitch`だけが音程と再生速度を変える。`TrainGain`は停車時0、`TrainMotorGain`は停車時も小さく残す。全音源を同時にミュート・停止・解放する。音声専用検証は`Tests/Weather/run-vehicle-audio.ps1`。音源はResources/Audio/Vehicles。
- `CameraAmbience`: AppRootが生成する環境音再生。Resources/Audio/AmbienceのStreamingクリップ5種類をループする。Utils.GetGroundHeightから地表相対高度を求め、100m〜10mで近景音を増やす。Tauriからは音量だけ受け取る。公開programs/サウンドシステム.mdを参照。
- シェーダーは`Assets/Resources/Weather/Atmosphere.shader`。ビルド時にストリッピングされないようResourcesから直接ロードする。
- シーンの主光源を再利用する。無効化時に元の環境を戻し、ランタイムの所有アセットは破棄時に解放する。
- F9パネルはキーボード操作のみ。天気パネルのキーを追加するときはカメラ・Steam撮影キーと競合させない。

仕様は`docs/docs/geometries/U_天気と環境光.md`、独立したUnityでの検証は`Tests/Weather/run-unity.ps1`。
