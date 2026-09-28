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
- Unity プレイヤー(Windows)は表示ディスプレイ・解像度・全画面モードを終了時にレジストリ `HKCU\Software\<companyName>\<productName>`(この作品は `DefaultCompany\Realcity`)の `UnitySelectMonitor_h…`・`Screenmanager Resolution Width/Height_h…`・`Screenmanager Fullscreen mode_h…` に記録し、次回起動時に復元する。3D画面の表示先の不具合はここを見る。`companyName` は Unity 既定値の `DefaultCompany` のままなので、変えると記録先が変わり、一度だけ既定の表示先に戻る。
- 描画を使わない独立検証の Unity プロジェクトは、`Packages/manifest.json` を組み込みモジュール(`com.unity.modules.physics` / `terrain` / `terrainphysics` など、使うものだけ)にするとパッケージの解決を待たずに起動し、`Tests/Quay` は起動から終了まで約1分で済む。
- 検証用の Unity スクリプトの構文だけを先に確かめるなら、`UnityEngine.CoreModule.dll` などを HintPath で参照する netstandard2.1 の csproj をレポジトリの外に作って `dotnet build` すると数秒で済む(Unity の起動を待たない)。
- `Tests/*/run*.ps1` を Windows PowerShell 5.1(`powershell.exe`)で動かすとき、BOM無しのUTF-8だと日本語コメントの直後の行が黙って読み飛ばされることがある。2026-09-28 に `Tests/BuildingPlacement/run-unity.ps1` で検証用シェーダーを作る行が実行されず、`CheckGuideCoordinates` が `Shader.Find` の null で落ちていた(BOM付きUTF-8で保存し直して解消)。日本語を含む .ps1 はBOM付きで保存する。
- 建設可能な区画のハイライト(`ParcelManager.IsBuildable`)は区画の建築規制と区画種別だけを見て、空きや所有者を見ない。敷地が市有地に限られる市庁舎でも民有地の区画が色付きで出て、選ぶとプレビューで拒否される。
- 地形に接地させる生成器を独立の Unity プロジェクトで検査するときは、`Terrain.CreateTerrainGameObject` で Terrain を1枚置けば、`TerrainManager` が無くても `Utils.GetGroundHeight` が `Terrain.activeTerrains` から高さを拾う。`drawHeightmap = false` にすれば描画だけ止められる(撮影用の地面は別のメッシュで描く)。高さを変えたら `Utils.InvalidateHeightCache` を呼ぶ。
- 本編の Unity エディタが開いていると、Blender から書き出した新しい FBX はエディタが先に取り込み、`.meta` とミラープレハブ(`ModelPrefabGenerator`)を作る。独立プロジェクトの生成物を本編へ写すときは、既にあるものを上書きしない(GUID が変わり、先に作られたミラーの参照が切れる)。
- 建物ライブラリの `BD_WINDOW_LIGHT` はほぼ黒(RGB 0.07, 0.11, 0.11)で、ガラス張りの建物全体に使うと黒い箱に見える。全面ガラスの外壁には `BD_BLUE_GLASS`(0.13, 0.31, 0.51)を使う。
- 幾何だけの静的処理(`CoastlineStore`・`SeaRoute`・`PortBuildingMeshBuilder.ResolveWorldWaterlines` など)は、`Tests/BuildRuntime.ps1` が作る `Temp/bin/Debug/Assembly-CSharp.dll` と `UnityEngine.CoreModule.dll`・`Newtonsoft.Json.dll`・`Assets/Plugins/NetTopologySuite.dll` を参照する net8.0 のコンソールから実行して検査できる(`CoastlineStore.SetFromGeoJson` に合成の海岸線を入れる)。`Debug.Log` などネイティブ呼び出しを通る経路と `Time`・`Random` は落ちるので、ログ用フラグが偽の経路だけを呼ぶ。private static はリフレクションで呼べる。
- 別ワークツリーの Unity スクリプトをコンパイル確認するときは、本体の `Assembly-CSharp.csproj` を写してレポジトリの外に置き、`Assets\Scripts` の明示列挙をワークツリーの `Assets\Scripts\**\*.cs` のワイルドカードに、相対の `HintPath`(`Library\...`)を本体の絶対パスに置き換える。Dreamteck の `ProjectReference` は本体の `Library\ScriptAssemblies\Dreamteck.*.dll` への参照に替える(プロジェクト参照のままだと本体の `Temp` にビルド成果物を書く)。ワイルドカードにすれば、Unity が csproj を再生成していない新しい .cs も載る。
- API の地区ポリゴン(`zone.polygon`)は外周が時計回り(XZで符号付き面積が負)で、末尾に始点と同じ点が付いて届く(穴は含まない)。`BuildingMeshBuilder.TriangulateEarClip` / `TriangulateOrFan` は反時計回り前提なので、そのまま渡すと必ず耳切りに失敗して扇形分割に落ち、凹型の地区では三角形が外へはみ出して重なる(2026-09-28、地区編集のホバー塗りで発生。Editor.log に「耳が見つかりませんでした」が出る)。`WaterSurfaceManager.AppendRingMesh` と同じく、閉じ点を除いて `PolygonUtils.SignedArea` が負なら反転してから渡す。
 
## UI / Tauri

- チュートリアルの初期配置は現行HTTP契約を確認する。`POST /parcels` は `mode: empty_area` などを受け付け、旧チュートリアルにある `mode: manual` は未対応。空き領域作成の成功応答は `{ "parcel": 数値ID }`。道路マスターの名称は「2車線道路」であり、表示文に残る「2車線生活道路」で検索すると見つからない。新基盤は属性でバリアントを解決する。

- セーブデータやシナリオテンプレートのサムネイル画像表示は、シナリオ固有のサムネイル画像が存在すれば最優先で表示し、未設定または404の場合は舞台となる地形マップのサムネイル(`/api/v1/terrain/maps/{id}/thumbnail`)へフォールバックする(`ScenarioThumbnail.tsx`)。

- `utils/ipc.ts` の `sendEditBuildingUpdateDirectParams`(`edit_building:update_direct_params`)はどこからも呼ばれていない。建物の直接配置の寸法・回転角は `edit_building:placement_command` の差分で送っている。

- 別ワークツリーの未コミット編集とのマージ可否を `git merge-file` で試すときは、先に `tr -d '\r'` で改行を揃える。ワークツリー側の作業ファイルはCRLF、`git show` の取り出しはLFなので、揃えないと全行が変更扱いになり、実際には衝突しない変更でもファイル全体が衝突と出る。

- 起動中のTauriアプリの各ウィンドウを外から操作して検証するには、環境変数 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<ポート>` を付けて起動し、`http://127.0.0.1:<ポート>/json/list` の各ページへCDPの `Runtime.evaluate` で `window.__TAURI_INTERNALS__.invoke(...)` を送る(どのウィンドウかは `__TAURI_INTERNALS__.metadata.currentWindow.label` で分かる)。ウィンドウの実際の位置は、`SetThreadDpiAwarenessContext(-4)` でモニター別DPI対応にしたPowerShellから `GetWindowRect` で物理座標のまま測る。1420番を他の作業が使っているときは、別ポートでViteを立てて `tauri dev --no-watch --config '{"build":{"devUrl":"http://localhost:<ポート>","beforeDevCommand":""}}'` で向け先を変えられる。

- 接続先のバックエンドが無い状態(`GENESIS_UI_API_BASE_URL` を空きポートにする)で起動すると、各ウィンドウのReactは初期化待ちのまま何も描かない。ウィンドウの生成・配置の検証だけならこの状態で足りる。

- taoの `unmaximize()` と、内部で最大化フラグを落とす `set_position` / `set_size` は、最大化中のウィンドウに `ShowWindow(SW_RESTORE)` を発行するので、非表示のウィンドウでも表示されてしまう(tao 0.35.3 のソースで確認)。

- Git Bash からネイティブのコマンド(node など)へ `//` で始まる引数を渡すと、MSYSのパス変換で先頭の `//` が `/` に縮む。`///` で始まるコメント行を検索キーとして渡すと1文字ずれた位置に一致し、置換後に `/` が1本余る。

- Chromium(WebView2)の SVG は `font-size` 属性に rem を書いても効き、ルートの文字サイズを変えると即座に追従する(2026-09-28 に実測)。recharts の `fontSize` に `"0.93rem"` のような文字列を渡せるのはこのため。

- recharts 3 の `YAxis width="auto"` は目盛りの実寸で軸幅を決める。縦に並べたグラフが共通の軸幅(`Y_AXIS_WIDTH_REM`)で描画域の左端を揃えている箇所に使うと、グラフごとに幅が変わって揃わなくなる。

- ウィンドウの本文領域(`.content` / `.content-empty-shell`)だけ一時的に `font-size: 40px !important` にすると、大きさを継承している文字だけが 40px になる。文字サイズを明示していない箇所を機械的に洗い出せる。2026-09-28 時点で、本文を継承(1rem)させて書いているのは `template/Scenarios.css` を使うシナリオ画面だけで、ほかの画面はすべて明示している。

- チュートリアルウィンドウの案内文は3行で切れる(`Tutorial.css` の `-webkit-line-clamp: 3`、ウィンドウの高さは200px固定)。日本語はおよそ200字、英語などはおよそ390字までに収める。

- 開発用バックエンドの実データで UI をブラウザに描いて確かめるときは、確認用の Vite で `server.proxy` を使い `/backend` → `http://127.0.0.1:5000` へ GET だけを中継し、POST などは `window.fetch` の差し替えでモックすると、都市の状態を変えずに済む。

- 別ワークツリーの `tauri/node_modules` を本体の `tauri/node_modules` へのジャンクションにすると、`tsc` はそのまま動くが、書き込みを伴う道具は本体の `node_modules` を汚す。確認用の Vite は `cacheDir` を指定しないと本体の開発サーバーと同じ `node_modules/.vite` に書き、`npm run test:tutorial`(`tools/tutorial/run.mjs`)はバンドルを `node_modules/.cache` に書く。後者は `run.mjs` の出力先だけを書き換えた写しをレポジトリの外に置き、ワークツリーの `tauri` をカレントにして動かす(出力先の隣にも `node_modules` のジャンクションを置く。バンドルは外部パッケージを出力先から解決するため)。

## docs

- この環境には mkdocs が入っておらず、`python` はストアのエイリアスで起動しない。`py -m venv outputs/mkdocs-venv` に `docs/requirements.txt` を入れ、`docs/` で `outputs/mkdocs-venv/Scripts/python.exe -m mkdocs build --strict -d <リポジトリ外>` を実行すると、既定の `site/` を上書きせずに strict 検査だけできる。
- `[[ページ名]]` のウィキリンクを解決するプラグインは入っていないので、そのまま文字として表示される。`[[名前]](xxx)` と書くと `xxx` が相対リンクとして扱われ、`--strict` のビルドが失敗する。ページへのリンクは `[表示名](F2_地面アセットのメッシュ生成.md)` の形で書く。
