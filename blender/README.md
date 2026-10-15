# blender/ 3Dモデルの原本

City Genesis の建物・道路・プロップ・車両・駅舎の3Dモデルの原本(Blender ライブラリ)です。
`unity/Assets/Models/` 配下の FBX はここから書き出した生成物であり、FBX を直接編集しません。

| ファイル | 内容 | 書き出し先 |
|---|---|---|
| `buildings.blend` | 建物パーツ(壁・角・prebuilt・オープンスペース装飾など) | `unity/Assets/Models/Buildings/` |
| `paths.blend` | 道路・線路・ロープウェイ等のパーツと道路設備 | `unity/Assets/Models/Paths/` |
| `props.blend` | 地形プロップ・歩行者・工事車両・環境演出の乗り物 | `unity/Assets/Models/Props/` |
| `vehicles.blend` | 走行描画用の自動車・列車 | `unity/Assets/Models/Vehicles/` |
| `stations.blend` | 駅舎 | `unity/Assets/Models/Stations/` |

- 1カテゴリ1ファイルで、コレクション名は FBX のフルパス名(例 `a200/0/base-w1`)。マテリアルはファイル内で実体を共有する。
- Blender 5.2 形式。4.x では開けない。
- テクスチャ画像は `unity/Assets/Models/*/Textures/` を相対パスで参照する。
- FBX への書き出し、規約検証、閲覧用ギャラリーは別レポジトリ `genesis-gallery` の制作パイプラインが担う。
- `*.blend1`(Blender の自動バックアップ)はコミットしない。
