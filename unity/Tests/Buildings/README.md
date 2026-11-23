# 建物の回帰検証

## 実行

- `./run-unity.ps1`: 角パーツ(`*-c1.fbx`)がどの内角・向きでも両側の辺に沿うことと、フェンスのパーツの解決を、本編の `ModularBodyBuilder` で確かめる。
- `./run-generation.ps1`: 実FBXの多層躯体の生成結果と、生成時間・一時Mesh数を確かめる。
- `./run-crane.ps1`: 工事中クレーンの組み立てと旋回を確かめる。
- `./run-open-space.ps1`: 駐車場・駅前ロータリー・公園・運動公園などのオープンスペースの配置と描画を、さまざまな形の区画で確かめる。
- `./run-collapsed.ps1`: 倒壊した建物の瓦礫の山が建物の高さに応じて広がること、接写の折れた柱が底面の中に立つこと、遠景が瓦礫の範囲を覆う板1枚であること、傾斜地で地形に沿うこと、生成時間を、本番の材質と URP の描画で確かめる。
- `./run-mine-yard.ps1`: 金属鉱山(露天掘り)を起伏のある土地に、採石場を山のふもとに、掘る前と掘った後の地形で組み立て、設備が前庭に収まること、受入口が掘る側を向くこと、散らした重機・岩が敷地に収まることを、実FBXと URP の描画で確かめる。生成時間の内訳(地面・道・設備)も `result.txt` に出す。合格すると、配置 JSON を隣の `genesis-gallery` リポジトリの `pipeline/layouts` へ写す。
- `./run-farm-ground.ps1`: 畑・ビニールハウス・穀物農場を傾斜地に組み立て、地面パネルとタイルの土が同じ土のテクスチャを同じ縮尺で使い、平均色が元の単色の材質と一致することを、実FBXと URP の描画で確かめる。

`run-generation.ps1` は `-OutputName baseline` で変更前の生成結果を保存し、変更後に `-BaselineDirectory <baseline の絶対パス>` を付けて実行すると生成メッシュの差を検査する。

PowerShell 7・Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。Unity の場所は csproj から求め、`-UnityExe` で指定もできる。

結果は `unity/Temp/` の `BuildingCornerQA`・`BuildingGenerationQA/<OutputName>`(既定は `current`)・`ConstructionCraneQA`・`OpenSpaceQA`・`CollapsedDebrisQA`・`MineYardQA`・`FarmGroundQA` に出る(どれも `result.txt`・`qa.log`。生成は `timings.csv`、オープンスペースは PNG の見本と配置 JSON と、運動公園と公園の生成時間を比べた `timings.csv`、倒壊・鉱山の前庭・農地は `Captures` に PNG も出す)。`run-open-space.ps1` は合格すると、配置 JSON を隣の `genesis-gallery` リポジトリの `pipeline/layouts` へ写し、本編に無い a3001〜a3004・a3008 のミラープレハブと meta を本編の `Assets` へ写す。
