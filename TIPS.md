# TIPS

作業中に得た細かいTipsや気付き。あとでまとめて確認する。

## Unity

- `UnityEngine.Object` 派生の値に `??` / `?.` / `is null` を使わない。これらは参照比較なので、破棄済みオブジェクトや、エディタの `GetComponent` が欠けたコンポーネントの代わりに返す偽 null を null と見なさない(`GetComponent<T>() ?? AddComponent<T>()` はエディタで追加されない)。`== null` か `TryGetComponent` で判定する。
- 偽 null を返すのは、組み込みコンポーネント型(`AudioSource`・`MeshFilter` など)に対する `GetComponent<T>()` / `Component.GetComponent<T>()` / `GetComponent(Type)` だけ。自作 MonoBehaviour が欠けているときは本物の null が返る。`GetComponentInParent` / `GetComponentInChildren` / `TryGetComponent` は組み込み型でも本物の null。偽 null のプロパティを `?.` で読むと MissingComponentException になる(6000.4.2f1 の編集モード・プレイモードで実測)。
- `Awake` が例外を投げたコンポーネントは Unity が無効化し、以後 `Update` / `LateUpdate` が呼ばれない。他スクリプトから直接呼ぶメソッドは動くため、「操作は通るのに何も描かれない」形で現れる(例: 公共交通の路線設置で、停留所は置けるのにマーカーが一切出なかった)。
- 超遠景の陸地(`UltraFarLandMeshBuilder`)は、地形セルの版の合計が変わったときだけ作り直される(`TerrainManager.EnsureOverview`)。`CoastlineStore.Version` が変わっただけでは作り直されないので、海岸線だけが変わる機能を足すときは再構築の契機を別に足す。
- `InputHandler.UpdateTerrainPosition` は `Terrain` コンポーネントを持つ最初の当たりからカーソルのXZを取り、y=0平面との交点は地形に何も当たらないときだけ使う。`terrainLayerMask` に Ground が入っていれば、常駐セルの海の上では海面ではなく海底に当たった位置のXZになる(シーンの設定値は未確認。InputHandlers/AGENTS.md の「海域ではy=0平面との交点フォールバックで正しく出る」と食い違う)。
- `GET /buildings`(multi_agent)の応答には `parcel_id` が無い。そのため `BuildingManager.ResolveBuildingBaseHeight` の「モジュラー建物は `parcel.altitude` に接地する」分岐は一括ロードの建物では通らず、建物は中心点の地形標高に置かれている。`parcel.altitude` は接道パスの地面からの相対高さ(地表の道路なら0)なので、一括ロードに `parcel_id` を足すとモジュラー建物がワールドY≈0へ沈む。
- 海面は Y≈0 の板で、海岸線のマスクは無い。湖は footprint 全体を水位の高さの板で描く。どちらも地形を水位より下げると、バックエンドの海岸線や区画種別と関係なくその場所に水が見える。
