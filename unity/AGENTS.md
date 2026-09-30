# Unity(グラフィック)

City Genesis の3D描画プログラム。バックエンド(FastAPI)とは HTTP、UI(Tauri)とは MemoryMappedFile で連携し、いずれもユーザーPC の localhost 内でしか通信しない。ゲームのメインUIはここではなく `city-genesis/tauri` にある。

- 作業対象ディレクトリの `AGENTS.md` を先に読む。作業別の手順は Skill にある(genesis-mesh-generation / genesis-terrain-lod / genesis-input-mode / genesis-visual-mode / genesis-ipc / genesis-building-geometry / genesis-path-geometry / genesis-district-parcel-geometry)。
- 本文の docs パスは公開ドキュメント `city-genesis/docs/docs/` 起点。

## 構成(`Assets/Scripts`)

| 場所 | 役割 |
| --- | --- |
| `API/` | `ApiClient`(HTTP。版ヘッダーの付与)と `ApiRequest`、サブクライアント、応答スキーマ。`GeneratedModels.cs` は自動生成 |
| `DataLoaders/` | 取得データから Unity オブジェクトを作る各 Manager。セル常駐・LOD・共通生成予算・キャッシュ(`DataLoaders/AGENTS.md`) |
| `MeshGenerators/` | 建物・道路・線路・区画・遠景陸地のメッシュ生成。バリアント→タイプ→既定の順にフォールバック(`MeshGenerators/AGENTS.md`) |
| `Editors/` | 道路・建物・区画・地区・プロップ編集の状態管理と確定送信(`Editors/AGENTS.md`) |
| `InputHandlers/` | クリック・ドラッグ・キー入力=入力モード(`InputHandlers/AGENTS.md`) |
| `VisualHandlers/` | 表示/非表示・ハイライト・データ可視化=ビジュアルモードと常駐オーバーレイ(`VisualHandlers/AGENTS.md`) |
| `IPC/` | Tauri との MemoryMappedFile 通信、編集ステート・表示状態の同期(`IPC/AGENTS.md`) |
| `Tutorial/` | Tauriの指示に従う一時マーカーと操作制限。都市データ・シナリオ進捗を保持しない |
| `Vehicles/` | 描画専用の交通演出(一般車・公共交通・歩行者・路駐・索道・環境演出)(`Vehicles/AGENTS.md`) |
| `Weather/` | 天気・時刻・環境光・降水と、環境音・工事音・走行音(`Weather/AGENTS.md`) |
| `Utils/`、`Utils.cs` | 汎用ユーティリティ。新しい汎用処理を書く前に必ず確認(`Utils/AGENTS.md`) |
| `UI/` | 3D側のUI部品: ツールチップ、モード表示、ラジアルメニュー、カーソルHUD、多言語フォント `UiFontProvider`、標識、吹き出し、方位磁針 `CompassPanel`(専用レイヤー `Compass` の上空に実体を置き専用カメラで描く) |
| `Grid/`、`Steam/` | グリッドオーバーレイ、Steam 認証(`SteamAuthClient`。チケットを `POST /system/verify` へ送る) |
| `AppRoot.cs` | 起動処理。天気・音・エフェクトのシングルトンをここで生成する(シーン配置しない) |
| `GameStateManager.cs` | ゲーム状態(シングルトン)と状態変化イベント。`IsGameStarted()`=データ取得の許可、`IsWorldVisible`=表示の許可 |
| `MasterDataStore.cs` | マスターデータの保持。道路幅・車線数などの描画パラメータの正。起動時にバックエンドの応答を待って取得し(取れるまで2秒ごとに再試行)、以後は取り直さない。開発中にマスターを変えてバックエンドだけ再起動したら Unity も再起動する(知らないバリアントを `GetPathMaster` で引くと例外になる) |
| `CameraController.cs` / `.Cinematic.cs` / `CameraCellChecker.cs` | カメラ操作・撮影モードとバックエンド `City.camera` との視点同期 / 視野中心セルの判定と `CellDataLoader` への通知(ロードの起点) |
| `Constants.cs` / `LocalConstants.cs` | 共有定数(自動生成) / Unity ローカルの定数とログフラグ |
| `ZoneEditContext.cs`、`InputUtil.cs`、`LoadingOverlay.cs` | 地区編集の状態、入力抑制、ロード中の黒画面 |

## 編集してはいけないファイル

- `Constants.cs`: 正は `genesis-core/consts.py`、生成器は `genesis-core/tools/sync_consts.py`(Skill consts-sync)。このレポジトリ単体では再生成できないので、定数の追加・値変更はユーザーに依頼する。
- `API/GeneratedModels.cs`: `endpoints.yaml`(正はスプレッドシート)から `tools/generate_models.py` で生成。

## 知らないと危険なこと

### 座標系と高さ

- ワールド原点はマップ中心(東=+X、北=+Z)。バックエンドのセルは左下原点の非負番号、Unity のセル番号は負値を取る。変換は必ず `TerrainManager.UnityCellToBackendCell` / `BackendCellToUnityCells`。
- カメラ距離による原点シフトは行わない。Transform だけ動かすとセル判定・高さ参照・保存座標が食い違う。
- ノード・パスの Y は地面からの相対高さ。Y=0 のパスは3D上で浮いて見えても「完全に地面に沿っている」とみなし、高架扱いにしない。内部計算では道路を平面上にあるものとして扱う(`geometries/A_パス形状.md`)。
- 地形高さは `Utils.GetGroundHeight` だけを使い、別名のラッパーを置かない。埋立地の帯の中は max(地形, 岸壁の天端) を返す。タイル差し替え時は `Utils.InvalidateHeightCache()`。

### オブジェクトの識別(変更禁止級)

- GameObject 名は `Building_{agentId}` / `Path_{pathId}` / `Parcel_{parcelId}` / `Node_{nodeId}`。メタデータは `ObjectIdInfo`(`id` / `status` / `isFarObject` / `role`)で持ち、名前の文字列パースはしない。
- 入力のタイプ判定はタグ(`BuildingObject` / `ParcelObject` / `PathObject` / `PathNode` / `ZoneObject` / `SeaObject`)。コライダーとタグが無いとクリックできない。海面は `Terrain` を持たないため `SeaObject` で地形扱い。都市外の背景地形と埋立地の岸壁はタグ無しで、`InputHandler.IsTerrainSurfaceObject` が地形扱いにする。

### モード系は2系統

- 入力モード(`InputSystemManager` / `InputHandlers/`)はクリックで何が起きるか、ビジュアルモード(`VisualModeHandler` / `VisualHandlers/`)はどう見えるか。編集モードでは UI からの IPC を起点に両方をセットで切り替える(`programs/Unity入力制御.md` §9、`programs/Unity表示モード.md`)。
- ドライブ(`Vehicles/DriveMode`)は描画専用の乗車操作で、両モードの定数を増やさない(`geometries/O_車両の走行描画.md` §O.12)。
- 撮影(`CameraController.Cinematic.cs`)もUnity内のカメラ操作として管理する。`GameScene`の`MinimapToggleButton`を`cinematicModeButton`へ接続し、モード1=画面中央の地面・建物・海面の周回、モード2=視線角と絶対高度を保つ水平移動。回転軸取得・周回計算・操作中断は通常カメラと共有する。編集との排他は`InputHandler.IsNormalMode`、3D選択の抑止は`CameraController.BlocksWorldInput`を使う。操作・速度・復帰の仕様は`programs/Unity入力制御.md`「撮影モード」、検証は`Tests/Camera/run-unity.ps1`。

### ロードと生成予算

- `LodConfig` が最大81セルを常駐させる。旧 far/near/dynamic 集合は同じ常駐集合で、地形の正とは独立。取得順は地形→既存物の追従→区画→建物・道路。
- 生成は `RenderWorkScheduler` の共通予算で走らせ、`Start` / `Stop` を対で使う(Unity の `StopCoroutine` だけでは内側の IEnumerator が破棄されない)。常駐ワーカーを追加するときは `hasPendingWork` を指定しないと `game:loaded` が出ず初期ロードが終わらない。
- 全件走査を避ける。既存の逆引きインデックスは専用の登録・削除メソッド経由でだけ更新する(`geometries/H_バックエンドデータロード.md` §全件走査の回避)。
- `CameraCellChecker.ResetViewState` は視点検出を未初期化に戻し、カメラが静止していても次の判定で通知させる(セッション切替後にカメラを動かさなくてもロードが始まる入口)。
- `CameraController` は `game:start` で保存視点を復元し、静止するたび `PATCH /api/v1/city` へ送る。外部から視点を動かす入口は `FocusOn`(注視点・距離・方位・俯角へ滑らかに移動し、入力で中断)で、チュートリアルのカメラ案内が使う。`session:reset` / `game:end` では `SuspendCameraSync` で止める(前の視点で新しい都市の保存値を上書きしないため)。
- HTTP は必ず `ApiClient` を経由する。定型は単体=`Get`、一括=`GetBulk`(MessagePack は自動判別)、`Post`、`Patch`。それ以外(バイナリ応答、PUT・DELETE、並列送信、取消しつき)は `ApiClient.Request(method, path, json, timeoutSeconds)`(画像などバイナリの本文は `Request(method, path, body, contentType, timeoutSeconds)`)が返す `ApiRequest` を `Send(isCanceled)` で送り、結果は `.Web` から読む(並列は全部 `Begin()` してから1件ずつ `Send()`)。`UnityWebRequest` を直接作らない。版ヘッダー `X-City-Genesis-API` が付かない。

### アセットと描画

- モデルは `Assets/Models` の FBX と `Assets/Resources/Models` のミラープレハブ(自動生成)。Read/Write Enabled が必須(無効だとパーツが無言で消える)、FBX の削除・移動後は `Tools/Model Prefabs/Delete Orphans`(`geometries/P_マテリアル共有とエクスポート.md` §3.4)。
- 水面(`Assets/Shaders/Water.hlsl`)のフォグはピクセルのワールド位置で計算する(頂点フォグは近景が白くなる)。光沢を落として不具合を隠さない(`geometries/E_LOD処理.md` §海面)。

### ログ

- 冗長ログは `UnityLog.If(LocalConstants.UNITY_LOG_ROAD / _BUILDING / _LOADER / _TIMING, ...)`。ホットパスで無条件に `Debug.Log` を呼ばない。補間が重いときは `Func<object>` オーバーロード。`LogWarning` / `LogError` は無条件でよい。

## 検証ハーネス

`Tests/<対象>/run*.ps1` が独立した Unity プロジェクトで本編アセンブリを検証する(本編のシーン・マスター・モデルは変更しない。各 README 参照)。追加ソースを含むビルドは `Tests/BuildRuntime.ps1`。F8 で描画数・生成予算・フレーム p95/p99・キャッシュ推定量を表示する。

## 参照

上記以外の入口: `geometries/K_Unity差分ロード整理.md`(差分ロード・初期ロード完了)、`M_地形プロップ.md`、`programs/コンポーネント連携.md`(起動順序・ローカルAPIへの接続・セッション・Steam認証・カメラ同期)、`programs/IPC連携の整理.md`、`geometries/U_天気と環境光.md`、`programs/サウンドシステム.md`。

## フロントの版

`FrontendVersion.cs` は公開リポジトリ直下 `version.json` から生成する。同期は `tauri` で `npm run sync:version`。`FrontendVersionCheck` はPlay開始・Playerビルド時に同期を検査する。`BackendCompatibility` とIPCの照合が成立してからマスター取得・セッション復帰を始める。

コアのHTTP契約は起動時に一度照合する。起動準備中の取得失敗だけは再試行し、`BackendCompatibility.VersionReceived` が立った後は不一致でも取得を繰り返さない。コアはゲーム中に再起動・差し替えを行わず、定期的な版確認は置かない。通信確認1回目の失敗では都市表示を保持し、`system:backend_connection` の通知で入力とHTTP更新を止める。製品版は `AppRoot.BackendHealthPolling` が独立して `/sessions` を確認し、2回連続の失敗で終了する。Editor・Development Buildはこの監視を実行せず、Tauriの開発版が2回連続で失敗したときの `game:end` で都市表示を終了し、再接続を待つ。
