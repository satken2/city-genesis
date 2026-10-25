---
name: genesis-terrain-lod
description: City Genesisの地形/LOD/セル分割まわり(heightmap、LODティア、セルデータ、超遠景メッシュ、Unity差分ロード、海岸線と埋立地の岸壁・帯の接地)を調査・実装・修正するときに使う。「地形がガタガタ」「LODの切り替えがおかしい」「TerrainManager/CellDataLoaderを触りたい」「Z-fighting対策」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。
---

> **パス表記の基準**: 公開側のパスは `city-genesis` リポジトリのルート起点。`genesis-core/`・`genesis-gallery/` は別の非公開リポジトリのルートを示し、並置を前提としない。利用できる場合だけ実際の配置に読み替え、手元に無い場合は公開側で確認できる範囲を扱う。

このSkillはコード本体を持たない「道しるべ」です。全体の考え方は以下のドキュメントに、個別の仕様と意図はソースのコメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `docs/ja/geometries/D_地形形状.md`(オリジナル地形とシナリオの地形の区別・シナリオの地形APIは D.10)
- `docs/ja/geometries/E_LOD処理.md`
- `docs/ja/geometries/H_バックエンドデータロード.md`

## 地形エディタの位置づけ
- Tauriの地形エディタ(`tauri/src/components/terrain/`)は「シナリオに含まれる地形」を編集するツールで、シナリオ管理の「地形を編集」から開き、`PUT /api/v1/scenarios/{id}/terrain` で保存する。オリジナル地形(`genesis-core/assets/terrains/terrain_maps.yaml` の標高・サムネイル・メタデータ)は編集しない。
- 水面・川・資源・プロップ・カメラ・地表種別・気候区分はシナリオ(都市)の保存値で、オリジナル地形は持たない。ゲーム中の地形・LODの処理はセッションの都市のデータだけを見る。
- 建設物のあるシナリオでは標高・水面・川がロックされる(D.10)。標高を変えたときの遠景・海岸線・区画の種別と勾配の作り直しはコアが保存時に行う。
- DLC由来の暗号化されたシナリオは地形エディタの対象外(シナリオの地形APIが409 `SCENARIO_ENCRYPTED`)。新しく作った資源のIDは保存の応答 `resource_ids` で返る。

## 読むべきドキュメント(詳細ロジック、非公開)
- `genesis-core/docs/game-logic/LOD地形データ管理詳細.md`

## 読むべきAGENTS.md
- `unity/AGENTS.md` (ルートのデータロード全体像・座標系変換の要点)
- `unity/Assets/Scripts/DataLoaders/AGENTS.md` (地形の節: 知らないと事故る順序制約と規約。ティア表・海岸線・超遠景メッシュ・Z-fighting対策の仕様は D / E / H 文書。海岸線は埋立の完了で広がり `session:reload` のたびに取り直す。埋立地の岸壁・帯・天端と帯の中の接地 max(地形, 天端) は D 文書の D.12)
- `genesis-core/app/services/AGENTS.md` (地形データの管理)

## 編集対象になりやすいディレクトリ/ファイル
- コア側の地形・埋立地の形状を変える必要がある場合は、非公開コアの担当者と契約を揃える。岸壁・帯・天端はコアが返す派生値を使い、Unity で判定を再実装しない。
- Unity: `unity/Assets/Scripts/DataLoaders/TerrainManager.cs`, `LodConfig.cs`, `CellDataLoader.cs`, `CameraCellChecker.cs`, `ReclaimedLandStore.cs` / `ReclaimedLandManager.cs`(埋立地の岸壁), `unity/Assets/Scripts/MeshGenerators/ReclaimedLandMeshBuilder.cs`, `unity/Assets/Scripts/Utils.cs`(`GetGroundHeight`)

## 進め方
1. 公開ドキュメントで地形データ構造・LOD段階の全体像を把握する
2. 非公開ドキュメントで考え方を、ソースのコメントで細かい仕様を、DataLoaders/AGENTS.mdで順序制約と罠を確認する
3. 実装ファイルを編集する
4. 個別の仕様はソースのコメントに書く。全体の考え方やモジュール間の契約が変わる変更をしたときだけ、ドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
