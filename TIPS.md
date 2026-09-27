# TIPS

作業中に得た細かいTipsや気付き。あとでまとめて確認する。

## Unity

- 非表示のUnityでInput Systemへ合成キーを送る検証は、検証プロセスの`Application.runInBackground = true`、`InputSystem.settings.backgroundBehavior = IgnoreFocus`、`editorInputBehaviorInPlayMode = AllDeviceInputAlwaysGoesToGameView`を設定する。既定のままでは`QueueStateEvent`後に`InputSystem.Update`してもキーがゲーム側へ届かず、操作本体が正しくても失敗する(`Tests/Camera`のハーネス)。
- `UnityEngine.Object` 派生の値に `??` / `?.` / `is null` を使わない。これらは参照比較なので、破棄済みオブジェクトや、エディタの `GetComponent` が欠けたコンポーネントの代わりに返す偽 null を null と見なさない(`GetComponent<T>() ?? AddComponent<T>()` はエディタで追加されない)。`== null` か `TryGetComponent` で判定する。
- 偽 null を返すのは、組み込みコンポーネント型(`AudioSource`・`MeshFilter` など)に対する `GetComponent<T>()` / `Component.GetComponent<T>()` / `GetComponent(Type)` だけ。自作 MonoBehaviour が欠けているときは本物の null が返る。`GetComponentInParent` / `GetComponentInChildren` / `TryGetComponent` は組み込み型でも本物の null。偽 null のプロパティを `?.` で読むと MissingComponentException になる(6000.4.2f1 の編集モード・プレイモードで実測)。
- `Awake` が例外を投げたコンポーネントは Unity が無効化し、以後 `Update` / `LateUpdate` が呼ばれない。他スクリプトから直接呼ぶメソッドは動くため、「操作は通るのに何も描かれない」形で現れる(例: 公共交通の路線設置で、停留所は置けるのにマーカーが一切出なかった)。
- 超遠景の陸地(`UltraFarLandMeshBuilder`)は、地形セルの版の合計が変わったときだけ作り直される(`TerrainManager.EnsureOverview`)。`CoastlineStore.Version` が変わっただけでは作り直されないので、海岸線だけが変わる機能を足すときは再構築の契機を別に足す。
- `InputHandler.UpdateTerrainPosition` は `Terrain` コンポーネントを持つ最初の当たりからカーソルのXZを取り、y=0平面との交点は地形に何も当たらないときだけ使う。`terrainLayerMask` に Ground が入っていれば、常駐セルの海の上では海面ではなく海底に当たった位置のXZになる(シーンの設定値は未確認。InputHandlers/AGENTS.md の「海域ではy=0平面との交点フォールバックで正しく出る」と食い違う)。
- `GET /buildings`(multi_agent)の応答には `parcel_id` が無い。区画に依存する処理(`ResolveParcelContext` の区画ポリゴンを使うフェンス・敷地装飾、`buildingIdsByParcel`、車・歩行者の建物出入口 `RoadsideAccess`)は一括ロードの建物では働かず、区画が取れるのは建設プレビューだけ。接地高は区画に依存しない(陸上は中心点の地形標高、海上区画だけ座標から引いた区画の `altitude`)。
- `unity/Assembly-CSharp.csproj` は `dotnet build Assembly-CSharp.csproj -nologo -clp:ErrorsOnly` で数秒のコンパイル確認に使える(出力は `unity/Temp` 配下)。Unity が再生成するまで新しい .cs は載らないので、2026-09-27 時点では `WaterSurfaceStore` 関連の無関係なエラーが4件出る。それ以外のエラーは同じ実行で報告されるので確認には使える。
- 海面は Y≈0 の板で、海岸線のマスクは無い。湖は footprint 全体を水位の高さの板で描く。どちらも地形を水位より下げると、バックエンドの海岸線や区画種別と関係なくその場所に水が見える。
- `CoastlineStore.IsLand` は呼ぶたびに全リングの全辺を走査する(父島・香港の海岸線で数千〜約7千頂点)。敷設中のように毎フレーム多数の点を判定するときは、地形高さなど安い条件で先に候補を絞ってから呼ぶ(`RoadPlanManager.CheckSeaLakeCrossings` は「地形が水面より低い点」だけを判定する)。
- 組み立て後の建物メッシュの頂点数がパーツの取り込み頂点数よりずっと大きいときは、元メッシュのサブメッシュごとの `GetIndices` の最小〜最大の幅の和と比べる。`Mesh.CombineMeshes` はサブメッシュ単位の結合でもこの幅を丸ごと写すので、和が組み立て後の頂点数とほぼ一致すれば、材質をまたいで溶接された頂点による範囲の重なりが原因。`CombineModuleMeshes` を通るパーツはこれを詰め直すが、通らない経路の未参照頂点は2026-09-27の全バリアント測定で、モジュラー建物(model_type 1)の最終メッシュの約17%(壁・角・エントランス等の直接配置)、車両基地(model_type 9)の約16%(線路タイルの切り詰めで末尾に頂点を足してから `SetTriangles` するため)。
 
## UI / Tauri

- チュートリアルの初期配置は現行HTTP契約を確認する。`POST /parcels` は `mode: empty_area` などを受け付け、旧チュートリアルにある `mode: manual` は未対応。空き領域作成の成功応答は `{ "parcel": 数値ID }`。道路マスターの名称は「2車線道路」であり、表示文に残る「2車線生活道路」で検索すると見つからない。新基盤は属性でバリアントを解決する。

- セーブデータやシナリオテンプレートのサムネイル画像表示は、シナリオ固有のサムネイル画像が存在すれば最優先で表示し、未設定または404の場合は舞台となる地形マップのサムネイル(`/api/v1/terrain/maps/{id}/thumbnail`)へフォールバックする(`ScenarioThumbnail.tsx`)。

- 別ワークツリーの未コミット編集とのマージ可否を `git merge-file` で試すときは、先に `tr -d '\r'` で改行を揃える。ワークツリー側の作業ファイルはCRLF、`git show` の取り出しはLFなので、揃えないと全行が変更扱いになり、実際には衝突しない変更でもファイル全体が衝突と出る。

## docs

- この環境には mkdocs が入っておらず、`python` はストアのエイリアスで起動しない。`py -m venv outputs/mkdocs-venv` に `docs/requirements.txt` を入れ、`docs/` で `outputs/mkdocs-venv/Scripts/python.exe -m mkdocs build --strict -d <リポジトリ外>` を実行すると、既定の `site/` を上書きせずに strict 検査だけできる。
- `[[ページ名]]` のウィキリンクを解決するプラグインは入っていないので、そのまま文字として表示される。`[[名前]](xxx)` と書くと `xxx` が相対リンクとして扱われ、`--strict` のビルドが失敗する。ページへのリンクは `[表示名](F2_地面アセットのメッシュ生成.md)` の形で書く。
