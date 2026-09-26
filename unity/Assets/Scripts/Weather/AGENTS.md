# Weather

Unityの環境表現だけを扱う。経済シミュレーションの天気データは作らない。天気・時刻・気候の3つで空・光・降水を決める。

- `WeatherPreset`: 13種類の天気(空・雲・光・雨量・雪量・平均持続時間)の正。太陽が高い昼間の見た目で、太陽の向きと夕焼けの色は持たない(時刻が決める)。夕焼けは天気ではない。
- `WeatherClimate`: 気候区分(`Constants.CLIMATE_*`。マップ固定で `GET /api/v1/city` の `climate`)×季節(`current_date` の月。北半球の暦月)ごとの天気の出現割合(%)の表の正。行の合計は100で、0はその気候・季節では自然に起きない。値を変えるときは公開 `U_天気と環境光.md` の表も同時に直す。気候区分の定数の正は `genesis-core/consts.py`(同期で `Constants.cs` に出る)。
- `WeatherChain`: 天気の移り変わり。隣り合う天気の組(雲が増えて降り出し、弱まって晴れる順)を1歩ずつ進むメトロポリス法で、長時間の滞在割合が `WeatherClimate` の重みに一致する。1回の長さは平均持続時間の0.5〜1.5倍。途中から見始めるとき(エポック明け・セッション開始)は `Sample` で割合どおりに選ぶ。隣接の組を変えたら全気候・全季節で `Connected` が真であること(QAが検査する)。
- `WeatherClock`: 描画用の時刻。日の出→日没600秒、夜は描かず20秒の薄明で次の日の出へつなぐ(方位の切替は太陽が地平線下にある間)。`BeginNight` で早送り→暗転、`RequestDawn` で暗転しきってから日の出→朝。`Advance` は日の出を始めた瞬間だけ true を返す(呼び出し側がそこで天気を選び直す)。時刻はセーブしない。
- `WeatherEpochTracker`: 初回取得(セッション開始)と実際のエポック増分を区別する。編集リロードでは何もしない。
- `WeatherController`: AppRootが生成。毎フレーム、時刻を進め、天気の移り変わりを進め、空・光・霧・環境光・降水へ反映する(都市オブジェクトには触れない)。天気の切替は45秒補間で、雲の配置(`_Seed`)はエポック明けとセッション開始でだけ変える。ApiClient経由で `city.epoch`・`climate`・`current_date` だけを読む。専用System.Randomを使い、UnityEngine.Randomの状態に影響しない。反射プローブは3秒ごと、雲影Cookieは雲量が変わったときだけ作り直す。
- エポック連動: `epoch:executing {executing}`(UI→Unity、エポック実行確認ウィンドウが開始と終了に1通ずつ)を受け、開始で `BeginNight`、終了で都市を読み直してから `RequestDawn`。読めなくても日の出へ進め、暗いままにしない。通知なしにエポックの増加を読んだときも短い暗転を挟む。エポックが進んでいれば暗いうちに新しい季節で天気を選び直す。固定中は選び直さない(暗転と日の出はする)。セッション開始の最初の読み込みで天気を選び直して朝にする。
- `weather:query` / `weather:set {id}`を受け、`weather:state`で現在値と候補(`{id, weight, rain, snow}`。weightは今の気候・季節の割合)をCockpitへ送る。nullの選択は固定解除で、その天気から移り変わりを再開する。天気ID・出現割合・降水量の正はUnity、翻訳・絵文字はTauri、環境音はUnity。
- `WeatherPrecipitation`: カメラ近傍だけで雨粒(`RainStreak.shader`、上空の面から)と雪片(`Snowflake.shader`、箱の中に湧かせて寿命の始めと終わりを透明に)を描く。降り方は0〜3の連続値で、段階の間は補間する。暗さで粒の量を減らす。降り止みは湧き出しだけ止め、ゲーム外・無効化時は即座に消す。都市オブジェクトへの衝突判定や経済状態の変更はしない。
- `ConstructionAmbience`: AppRootが生成する工事音再生。BuildingManager/PathManagerがデータキャッシュ同期時に `Register/Unregister/Clear` で現場(バックエンド座標の点列。建物=中心1点、パス=施工先端の1点)を登録し、0.25秒ごとにカメラから250m以内の近い順8件を3Dループ再生する(音源プール8本・Linear減衰15〜250m・ドップラー0・0.5秒フェード・Tauriから受けた音量に基準ゲイン `Gain`=0.6を掛ける)。音源はResources/Audio/Construction。GameObjectに紐づけない。座標はFloatingOriginのずれを含まず、ルート直下の不動アンカーの位置をずれとして足す。
- `VehicleAmbience`: AppRootが生成する走行音再生。VehicleManager(一般車=Kind.Car)とTransitVehicleManager(路線バス=Kind.Bus・鉄道編成=Kind.Train)が毎フレーム `Report(kind, id, ワールド座標, 速度)` で通知し、1秒通知が無い発音体は自動で外れる(`Remove`/`Clear`は即時)。車6本+鉄道3編成×4音源（走行音3テンポ＋モーター）の音源プールで近い順に割り当て(車120m・鉄道300m)、車はピッチ=`CarPitch(速度)`、鉄道は自録の走行音とモーター音を重ね、`TrainTempo`で音程固定の0.65倍・等倍・1.5倍ループを混ぜる。走行音のpitchは常に1。`TrainMotorPitch`だけが音程と再生速度を変える。`TrainGain`は停車時0、`TrainMotorGain`は停車時も小さく残す。全音源を同時にミュート・停止・解放する。音声専用検証は`Tests/Weather/run-vehicle-audio.ps1`。音源はResources/Audio/Vehicles。
- `CameraAmbience`: AppRootが生成する環境音再生。Resources/Audio/AmbienceのStreamingクリップ5種類をループする。Utils.GetGroundHeightから地表相対高度を求め、100m〜10mで近景音を増やす。雨音は `WeatherController.RainAmount`(移り変わりの途中を含む0〜3の連続値)で隣り合う2段を配分する。雪は雨量0なので無音。Tauriからは音量だけ受け取る。公開programs/サウンドシステム.mdを参照。
- シェーダーは`Assets/Resources/Weather/`(`Atmosphere.shader`・`RainStreak.shader`・`Snowflake.shader`)。ビルド時にストリッピングされないようResourcesから直接ロードする。
- シーンの主光源を再利用する。無効化時に元の環境を戻し、ランタイムの所有アセットは破棄時に解放する。
- F9パネルはキーボード操作のみ(F6/F7 天気の選択と固定、F10 固定切替、F11 選び直し、F3/F4 時刻±1時間、F5 時刻の停止)。天気パネルのキーを追加するときはカメラ・Steam撮影キー(F12)・LOD統計(F8)と競合させない。

仕様は`docs/docs/geometries/U_天気と環境光.md`、独立したUnityでの検証は`Tests/Weather/run-unity.ps1`(気候の表・移り変わりの滞在割合・時刻・エポック連動・撮影用キャプチャ。`Constants.cs`と`Utils/PolylineUtils.cs`も写して単独でコンパイルする)。
