# M. 地形プロップ（装飾オブジェクト）

## M.1. 役割と保存データ

地形プロップは森・茂み・岩などの純粋な装飾で、経済シミュレーション、配置コスト、維持費、天然資源とは接続しない。通常プレイでも未開放セルでも配置できる。

| 型 | 保存する内容 | 用途 |
|---|---|---|
| PropScatter | prop_type、region、density（本/ha）、seed、version | 森などの範囲散布 |
| PropInstance | prop_type、XZ座標、rotation、scale、version | 明示的な個別配置 |

regionはGeoJSON MultiPolygonのcoordinates形式で、外周・穴・複数パートを保持できる。木ごとの座標は保存しない。標高も保存せず、Unityが表示中の地形から取得する。モデルはResources/Models/Props/{prop_type}からModelManager経由で取得する。

## M.2. 非破壊の重なり除外

**都市の開発ではregionも個別配置レコードも変更・削除しない。** 散布範囲の編集・プロップの撤去はユーザーの明示操作でのみ行う。描画側が現在の障害物と重なる候補点を非表示にする。障害物を撤去すると同じ位置・回転・スケールの個体が再表示される。これは装飾の表示復元であり、樹木の成長シミュレーションではない。

| 対象 | 除外条件 |
|---|---|
| 土地区画のみ | 除外しない。区画の作成順にも依存しない |
| 建物 | 保存された建物底面をワールド座標へ変換した範囲 |
| 地面パネル | draw_floorが有効で、Unityに対応floorマテリアルが存在する建物の区画全体 |
| 道路・線路などの物理パス | 保存された占有ポリゴン |
| 完全地下のパス | 除外しない |
| 公共交通の論理パス | 除外しない |
| 高架・橋、部分的な地下パス | 初版は占有ポリゴン全体を除外する。高さに応じた部分除外は対象外 |

計画確定済みの建物・パスも障害物になる。解体中・倒壊中も実体レコードがある間は除外を維持する。マッシュアップ施設は親建物の保存底面を使い、各サブ建物間の隙間を復元する個別判定は行わない。

境界上も除外し、幹だけが境界の外にあって樹冠が壁に接する状態を抑えるため、モデルの水平半径×0.15×配置スケールを離隔に用いる。離隔上限は8m。バックエンドも取得BBOXを8m拡張し、隣接範囲の障害物を含める。

除外は小区画の生成・障害物変更時にCPUで判定して保持する。毎フレームの木×建物総当たり、全市の障害物のポリゴン結合、散布regionからのdifferenceは行わない。建物のGameObjectやColliderの存在には依存しない。

## M.3. 安定した散布

PropGeometryが永続描画と散布プレビューの共通実装である。密度から候補格子の間隔を求め、ワールド原点に固定した格子番号とseedをハッシュして、各格子内の位置・回転・スケールを生成する。格子間隔は平方根（10000÷density）、下限0.5m。スケールは0.8〜1.2倍である。

候補点は小区画の半開区間に一意に所属する。region内かつ穴の外の点のみ採用し、重なる複数パートでも同じ点を二重生成しない。全範囲の乱数列を先頭から消費する必要がなく、必要な小区画だけ直接生成できる。

同じ密度・seedなら、カメラ移動、ロード順序、描画用分割数、障害物の追加・撤去、散布範囲の編集によって共通部分の候補点は変わらない。密度またはseedの編集では配置が変わる。シーン原点の移動量は描画行列に適用し、保存座標とハッシュには混ぜない。

## M.4. 既存LODとの統合

[E_LOD処理](E_LOD処理.md)のデータティア・描画LOD・生成保持予算の分離に従う。

| 層 | 管理内容 |
|---|---|
| LodConfig / CellDataLoader | 既存セルのティア、ロード対象、LoadBatch |
| セル内の小区画 | 散布・除外・GPUバッチ生成の単位。独立した地形セルではない |
| DrawLod | 個体ごとのClose / Normal / Far / Hidden |
| フレーム・保持予算 | 生成処理の分散、常駐候補点数の制限 |

PropManagerはfarCells（Near/Dynamicを含む集合）をロードする。UltraFarには描画しない。Near/Dynamicでは画面上の大きさと既存DrawLodEvaluatorのヒステリシスでLODを選び、FarセルではFar以下に制限する。投影比0.001未満でHiddenへ、0.0015以上で再表示する。カメラ移動4m・投影設定変更・データ変更で再判定し、既存の12フレーム間隔を使う。視錐台によるバッチの表示判定は毎フレーム行う。

セルサイズの正はTerrainManager.CellSize。既定では一辺を8分割する（500mセルなら62.5m）。境界をまたがず、分割数はUnityのsubdivisionsで設定する。地形ハイトマップの513解像度、バックエンドのセルID、Dynamic地形の取得・保存単位には影響しない。

## M.5. GPU描画とモデル

PropRenderModelはプレハブ内のMeshFilter・MeshRenderer・子のローカル変換・サブメッシュ・マテリアルを取り出し、複製したマテリアルのinstancingを有効にする。永続表示では一本ごとのGameObjectやColliderを作らない。

| DrawLod | モデル |
|---|---|
| Close | {prop_type}_closeがあれば使用。なければ無印 |
| Normal | 無印の{prop_type} |
| Far | {prop_type}_farがあれば使用。なければ複雑なサブメッシュの頂点を空間集約して簡略化 |
| Hidden | 描画しない |

自動Farは4×8×4の格子で頂点とUVを集約し、縮退・重複三角形を除去する。元のマテリアルを引き継ぐ。128三角形以下の部品、読み取り不可のメッシュ、簡略化しても削減できない部品は元の形状を使う。専用アセットほど輪郭が忠実ではないため、目立つ種類には同じ原点・寸法の_farを用意できる。生成メッシュと複製マテリアルはPropRenderModelが所有し、全消去時に破棄する。共有FBXのメッシュ・マテリアルは変更しない。

GraphicsSettingsのInstancing VariantsはKeep Allに設定する。実行時に複製してinstancingを有効化するため、ビルド時のStrip Unusedでは必要なバリアントが除去され得る。保持によりシェーダービルド量は増える（[Unity Graphics Settings](https://docs.unity3d.com/jp/current/Manual/class-GraphicsSettings.html)）。プレビューも複製マテリアルでURPの透過ブレンドと深度書き込みOFFを明示する。

描画は小区画・種類・LOD・部品ごとにまとめ、Graphics.RenderMeshInstancedで一回最大511インスタンスを送る。バッチのBoundsにはモデルの実寸・回転・スケール・子の変換を含める。影はClose/Normalのカメラから150m未満に制限し、Farは影を落とさない。モデル・プレビューマテリアルのシェーダーはGPUインスタンシング対応が必要。

## M.6. 差分更新と負荷制限

マージBBOXで配置情報と障害物を一括取得する。候補点はプロップID/versionの組み合わせで、表示可否は障害物ID/形状signatureの組み合わせで差分更新する。道路の変更では関係する小区画の表示可否だけを更新し、候補点を再散布しない。削除や移動では旧位置側の小区画も応答との比較で更新される。

生成・高さ調整・描画バッチ構築はPerformanceConfig.FrameBudgetMsでフレーム分散する。LoadBatchと独自の世代番号で旧処理のコミットを防ぐ。同じ小区画のLOD再構築中は前の完成済みバッチを維持し、新しい完成結果へ置き換える。候補・除外データの更新で小区画を置き換える場合は、新バッチ完成まで一時的に非表示になる。

一散布レコード5,000個という上限は設けない。代わりに常駐候補点の予算maxResidentCandidates（既定250,000）を使い、カメラに近い小区画から優先する。非表示候補も復元用に数える。予算を超える小区画は区画全体を省略し、次のロードで再評価する。更新作業中は旧配列と新配列を一時的に併存させるため、瞬間的なメモリ使用量は常駐分を上回る。

地形高さは既存HeightmapTextureManagerの取得処理を共用する。Near/DynamicはTerrainFollowUpCoroutineのupdatedCells∩nearCellsでYを更新する。初回Farタイルも地形より先にプロップを取得し得るため、OnTerrainTileReadyで対象小区画の高さ更新を予約する。ティアダウンで地形データを戻さない既存仕様に従い、保持中の地形へ接地する。

## M.7. APIと配置プレビュー

| メソッド | パス | 内容 |
|---|---|---|
| GET | /api/v1/props?bbox=x1,z1,x2,z2 | scattersとinstancesを取得。既定上限は各10,000レコード |
| GET | /api/v1/props?bbox=…&include_obstacles=true | obstaclesを同梱する完全な描画用スナップショット。件数を途中で切らない |
| POST | /api/v1/props/scatters | polygon、prop_type、density、任意seedで作成 |
| PATCH | /api/v1/props/scatters/{id} | polygon、density、seedを編集 |
| DELETE | /api/v1/props/scatters/{id} | 散布範囲を撤去 |
| POST | /api/v1/props/instances | instances配列を一括作成 |
| DELETE | /api/v1/props/instances/{id} | 個別配置を撤去 |

obstaclesの各項目はkey、signature、ワールド座標のpolygon、floor_variant、visual_variantを持つ。floor_variantが0なら物理的な占有形状、非0なら地面パネル候補でありUnityがfloorマテリアルの存在を確認する。

PropPlacementManagerは同じPropGeometryとPropManagerの障害物キャッシュで散布プレビューを作り、半透明のインスタンシングで表示する。散布の位置・回転・スケールは確定後と一致する。プレビューは無印モデルを使い、障害物取得済みのロード範囲と候補点予算内に限る。範囲外まで全景プレビューする機能はない。個別配置の編集用ゴーストにはGameObjectを使うが、保存後は共通の永続レンダラが担当する。

既存プロップの削除UI、橋下の高さ判定、UltraFarの森林表現、外構・街路樹の自動生成は対象外。配置モデルの種別はマスターのprop_masterに登録する。

## M.8. 検証

バックエンドはtests/test_props.py、UnityはTests/Props/run.ps1で検証する。Unity検証はTemp/PropsUnityQAの独立プロジェクトで実アセンブリと実FBXを使い、境界重複、穴、分割数変更時の再現性、非破壊除外と復元、局所更新、Farモデル、1,000/10,000/50,000本の生成を確認する。画像とCPU計測値は同ディレクトリに出力する。CPU計測はGPU完了時間や本編のフレームレートを意味しない。実行条件と計測記録はUnityリポジトリ内の`Tests/Props/README.md`に記載する。
