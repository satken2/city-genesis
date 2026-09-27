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
- Git Bash の `sed -i` は CRLF のファイル(このレポジトリの作業ツリーは `core.autocrlf=true` で全行 CRLF)を書き戻すとき、置換しなかった行の CR も落として LF にする。`.mat` / `.meta` を複製して一部だけ書き換えるときは、書き換え後に行末をそろえ直すか、Edit で直接書き換える。
- `ReclaimedLandMeshBuilder` や `ReclaimedLandStore` のように Unity のネイティブ API(Mesh・Terrain・Debug)を通らない純データのクラスは、`Tests/BuildRuntime.ps1` の出力 DLL と `UnityEngine.CoreModule.dll`・NTS・Newtonsoft を参照する .NET 8 のコンソールから直接呼んで検査できる(`Tests/Intersections` と同じ形。private の入れ子型もリフレクションで Newtonsoft に渡せる)。
- `TerrainCollider` のレイが当たる地形の三角形は、ハイトマップの各マスを (0,0)-(1,1) の対角線で切ったもの(`TerrainData.GetInterpolatedHeight` と同じ)。もう一方の対角線で計算すると、起伏の大きい所で数mずれる(2026-09-27、`Tests/Quay` の照合)。
- 描画を使わない独立検証の Unity プロジェクトは、`Packages/manifest.json` を組み込みモジュール(`com.unity.modules.physics` / `terrain` / `terrainphysics` など、使うものだけ)にするとパッケージの解決を待たずに起動し、`Tests/Quay` は起動から終了まで約1分で済む。
- 検証用の Unity スクリプトの構文だけを先に確かめるなら、`UnityEngine.CoreModule.dll` などを HintPath で参照する netstandard2.1 の csproj をレポジトリの外に作って `dotnet build` すると数秒で済む(Unity の起動を待たない)。
 
## UI / Tauri

- チュートリアルの初期配置は現行HTTP契約を確認する。`POST /parcels` は `mode: empty_area` などを受け付け、旧チュートリアルにある `mode: manual` は未対応。空き領域作成の成功応答は `{ "parcel": 数値ID }`。道路マスターの名称は「2車線道路」であり、表示文に残る「2車線生活道路」で検索すると見つからない。新基盤は属性でバリアントを解決する。

- セーブデータやシナリオテンプレートのサムネイル画像表示は、シナリオ固有のサムネイル画像が存在すれば最優先で表示し、未設定または404の場合は舞台となる地形マップのサムネイル(`/api/v1/terrain/maps/{id}/thumbnail`)へフォールバックする(`ScenarioThumbnail.tsx`)。
