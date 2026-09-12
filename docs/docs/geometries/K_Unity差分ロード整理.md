# Unityの差分ロード

取得範囲、標高の確定、物体の描画品質を分離する。カメラ移動だけでは標高や都市データを変更しない。

## 更新の流れ

1. `CameraCellChecker` が位置・回転・投影条件の変化を通知し、`LodConfig` が常駐集合を計算する。
2. 集合の変更または編集後の再取得で `CellDataLoader` が `LoadBatch` を発行し、旧バッチをキャンセルする。
3. 地形APIの派生版を比較し、未取得／変更セルだけを取得・適用する。全景も編集の版が変わった場合に更新する。
4. `TerrainManager` は裏で完成したタイルだけを公開し、実際に更新したセルを通知する。
5. 既存道路・建物・プロップの地形追従を更新する。中断時の未処理セルは次のバッチへ引き継ぐ。
6. `ParcelManager` の区画取得完了を待って、新しい建物・道路・プロップ等の取得を始める。
7. 物体の描画LODは別のワーカーが判定する。生成済みの同じ形状がキャッシュにあれば再利用する。

地形イベントによる道路・建物の再生成はバッチ中に合流させ、セル枚数分の重複生成を避ける。すべての生成コルーチンは `RenderWorkScheduler.Start/Stop` を通す。Unityのコルーチン停止だけに資源解放を依存させない。

## 版と取得

| データ | 差分の基準 | 通信 |
| --- | --- | --- |
| 表示地形 | 編集セルと隣接依存を含む派生版 | `/terrain/cell-versions` → `/terrain/surface` |
| 全景地形 | 編集の版の変化 | `/terrain/far` |
| 建物 | `version`、描画キーに含む形状・敷地・地形版 | `/buildings/?bbox=...&limit=500&cursor=0` |
| 道路 | `version` と接地・交差点・接続形状の再生成世代 | `/paths/?bbox=...&limit=500&cursor=0` |
| 区画・地区 | 各データの `version` | 既存BBOX一覧 |
| プロップ | 配置署名と障害物署名 | 配置・障害物の同じスナップショット |

建物・道路の一覧には `next_cursor` がある。nullになるまで取得し、全ページ成功後に一覧から消えたIDを削除扱いにする。ページの失敗や後退したカーソルでは同期を確定しない。都市の状態が変更されたときは旧 `LoadBatch` をキャンセルして新しい一覧を取得する。

地形の `/surface` はfloat32 LEの正方格子で、`X-Grid-Rows/Cols` と `X-Cell-Version` を返す。開放状態の問い合わせは `/acquired-cells` へ分離する。表示取得では土地を開放しない。版は表示タイルの適用成功後だけ記録する。

## セッション切り替え

地形の候補、常駐タイル、全景、描画キャッシュ、空間ハッシュ、進行中ロードを破棄する。`HeightmapTextureManager.Invalidate` はGPU移動窓を無効化する。前の都市の標高・候補・接続点を新しい都市で再利用しない。

`session:reset` はUIが新しいセッションの作成要求を出す直前に送るため、`session:reload` が届くまでカメラ由来のロードを止める（準備中のバックエンドから中途半端なタイルを取り込むと、同じ版のタイルが差分リロードで更新されず残る）。`session:reload` で解除し、`CameraCellChecker` の視点検出を未初期化へ戻す。現在セルが未設定に戻った後はカメラが静止していても次の判定で必ずセル通知が走り、カメラを動かさなくても新しい都市のロードが始まる。

## プロップの差分同期

配置の署名には散布／手置きのID・versionと割当予算、除外の署名には障害物のkey/signatureを使う。障害物だけが変わったときは散布し直さず、同じ候補の表示可否だけを更新する。地形変更では対象セルのYだけを更新する。旧描画バッチは新しいバッチの完成まで保持する。

範囲・予算の詳細は[E_LOD処理](E_LOD処理.md)、配置と除外の規則は[M_地形プロップ](M_地形プロップ.md)を参照。

## 取得と描画完了の区別

建物の遅延生成は、対象セルがロード完了集合へ登録されるまで要求を保持する。未登録セルの要求を選出してから削除してはいけない。一括取得中も生成ワーカーが動くため、背面の建物だけ永久に生成待ちから消える競合になる。次の取得範囲から外れた要求は新しいロード開始時に除く。

道路の `pathVersionCache` は受信版ではなく、生成・復元が完了した版を記録する。API取得や高優先度キューへの投入時には更新しない。同じ版でも描画オブジェクトが存在しない道路は再生成対象とする（意図的な遠景トンネル非表示は除く）。これにより建設計画の更新中にロードが中断されても、次の差分取得で生成できる。道路バッチの終了処理はfinallyで実行し、中断・例外時に地形追従を永久待機させない。

回帰検証は `Tests/Lod/run.ps1`。未ロードセルの建物キュー保持と、建設計画中の道路のHTTP取得→生成前中断→同版再取得を検証する。


## ロード遅延の計測

### カメラ移動時の再取得・再生成件数

`UNITY_LOG_TIMING` が有効なら、既存の5秒ごとの `[TIMING] Scheduler` に `count/名前=件数` を追記する。物体ごとのログは出さず、各窓の報告後に件数をゼロへ戻す。無効時は件数を蓄積しない。

| 名前 | 意味 |
| --- | --- |
| `camera.sameCells` / `camera.loadBatches` / `camera.changedCells` | 同一常駐集合で省略した通知数／更新バッチ数／変更セルの延べ数。バッチには明示リロードも含む |
| `building.fetchExistingCells` / `path.fetchExistingCells` | 再取得要求へ含めたロード済みセル数。`fetchNewCells` は未ロード分。マージBBOXの外接範囲やHTTP要求数ではない |
| `building.received` / `path.received` | ページ受信件数。途中キャンセル・再試行の受信分も含む |
| `building.dataUnchanged` / `path.dataUnchanged` | 一覧の適用時に再生成不要と判定した件数 |
| `building.lodScanned` / `path.lodScanned` / `roadSegment.lodScanned` | LOD走査へ渡した候補の延べ数。建物・道路本体は非表示等の早期除外候補も含む |
| `building.deferredLodChanged` | 建物の遅延要求と生成直前の判定が異なった件数 |
| `building.buildRequested` / `path.buildRequested` / `roadSegment.buildRequested` | 生成要求がキャッシュで解決できなかった件数。成功数ではない |
| `building.generated` / `path.generated` | 既存のメッシュ生成時間計測へ報告された件数。道路の遅延区間は別の `roadSegment` 件数で読む |
| `building.reuseActive` / `path.reuseActive` | 現在の管理オブジェクトをそのまま利用した件数（非表示からの復帰を含む） |
| `building.reuseCached` / `path.reuseCached` / `roadSegment.reuseCached` | 退避キャッシュから復元した件数 |
| `path.forceRebuildRequested` / `path.lodRebuildRequested` | 道路の強制形状更新／通常LOD切り替えの要求数。重複除外前の値 |
| `path.terrainRebuildRequested` | 強制更新要求のうち地形追従由来の件数 |
| `terrainFollow.interrupted` / `terrainFollow.requestedCells` | 更新バッチへの置換で中断した追従処理数／追従対象セルの延べ数 |
| `renderCache.localEvicted` / `renderCache.globalEvicted` | 個別／全体メモリ上限で破棄した退避オブジェクト数 |

追従処理の中断時は、新しい `LoadBatch#` に `FOLLOWUP_RESTART pendingCells=...` も出す。処理済み対象の一部が再実行される可能性を調べる入口であり、この値だけでは重複再生成数は確定しない。

静止・回転だけ・平行移動・セル境界の往復をそれぞれ5秒以上記録する。`fetchExistingCells` と `dataUnchanged` が多ければ取得範囲の差分化、`terrainFollow.interrupted` と地形由来の強制更新が多ければ中断後の継続単位、退避破棄とキャッシュミスが多ければ保持容量を検討する。CPU時間・最大step・F8のp95/p99も合わせて判断する。全範囲取得は編集・エポック実行後の差分更新にも使うため、カメラ由来の取得だけを分けるには要求理由と取消し時の未完了セルを扱う必要がある。

### 工程時間

`LocalConstants.UNITY_LOG_TIMING` を有効にすると、Unity ConsoleとEditor.log（Windowsでは `%LOCALAPPDATA%/Unity/Editor/Editor.log`）、製品実行時はPlayer.logに計測を出す。建物パーツの大量ログは計測へ影響するため `UNITY_LOG_BUILDING` を無効にする。

- `LoadBatch#` は同じ更新要求の経過時間を追う。START→TERRAIN_DONEが地形取得・適用、TERRAIN_DONE→FOLLOWUP_DONEが既存物の地形追従、PARCEL_START→PARCEL_DONEが区画待ちである。OBJECTS_STARTは建物・道路の取得開始であり、表示完了ではない。SUPERSEDED/RESETは後続要求への置換またはセッション終了で、表示失敗を意味しない。
- `ApiClient network/` と `deserialize/` は通信・デコードの完了をコルーチンが観測するまでの経過時間。スケジューラーの再開待ちも含むため、サーバーCPU時間とは区別する。
- `Scheduler` は5秒ごとにiterator型別の同期実行時間 `cpu`、最重ステップ `maxStep`、実行回数 `steps` を集約する。`queueFrames` は実行可能な処理が予算制限により次フレームへ残った回数、`underBudgetQueueFrames` は同期実処理の合計が予算未満でもポンプ全体の時間制限で残った回数。複数ワーカーの合計なので、実フレーム数や壁時計時間への単純換算はできない。cpuが小さく後者が多ければ、待機確認・iterator切替など管理処理の負荷を確認する。maxStepが大きければ当該iteratorの分割不能な同期処理を調べる。
- `buildingsPending` と `pathsPending` は建物詳細・道路地形追従/LOD更新キューの残件数。道路一括生成は別経路なので `pathBatch` とPathManagerの工程ログも合わせて読む。キューが0でも画面内の全オブジェクト表示完了を保証しない。

起動時は視点を固定してログを採り、表示後に回転だけ、次にセルを跨ぐ移動を行う。それぞれのバッチ・待ち数・cpuを比較すると、地形待ち、再取得、描画LOD生成を区別できる。共通予算は目標60FPSなら約4.17ms/フレーム。各ワーカーは最大0.5msまたは1件の処理境界で `Continue` を返して順番を譲り、予算が残れば同じフレームで再開する。明示的なnullは次フレーム待ちのままなので、アイドルや通信完了のポーリングを空回ししない。


## フレーム予算による生成の進行

区画・道路・建物・地形の生成中断は、次フレーム待ちとは区別する。RenderWorkSchedulerの共通ポンプは、実行可能なiteratorを待ち行列の先頭から1ステップ進めて末尾へ戻す。通信完了待ちや次フレーム待ちは飛ばし、後続の生成を止めない。1つのUnityコルーチンが先頭待ちでフレームを跨ぐ方式にはしない。

同期処理の合計と、待機確認・iteratorの切り替えを含むポンプ全体の経過時間の両方を共通予算で制限する。1件のメッシュ生成やUnity API呼出しの途中では中断できないため、重い1ステップによる予算超過は残り得る。その場合は残りの仕事を次フレームへ送り、maxStepの計測で生成器の分割対象を特定する。

区画はデータが不変でも1件ずつ必ず1フレーム待つ方式を使わず、予算内で差分を適用する。全件の区画適用完了後に建物などを開始する依存順序、地形の完成後差し替え、キャンセル時の未完成データ破棄は保つ。待機中のネストしたiteratorやメッシュもStart/Stopの対で解放する。


## 道路の非同期生成と表示の差し替え

長い道路のSplinePartLodは本体GameObjectの作成後に固定区間を生成するため、本体の作成を表示完了とみなさない。PathPresentation.Replaceで旧道路を保持し、新しい道路の各パーツが初回LODを確定した時点で一括表示する。生成途中の新しいRendererはforceRenderingOffで伏せ、旧道路との重複描画を防ぐ。既存のforceRenderingOff値は復元する。

再生成が連続した場合は未完成の候補から表示中の旧道路を引き継ぎ、未完成の候補だけを退避する。完成時に旧道路をキャッシュへ退避し、アンロード・全消去では保持中の旧道路を非表示にして破棄する。キャッシュからの復元と遠景への切替も同じ入口を通す。建物の主メッシュは同期生成なので、この道路パーツ専用の待ち合わせには含めない。回帰検証はTests/Lod/run.ps1のRoadPresentation。
