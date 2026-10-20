# プロップ描画の検証

## 実行

- `./run.ps1`: 散布・個別配置・森林(GPU 描画を含む)・草・駅階段のプロップの配置と描画を確かめる。
- `./run.ps1 -PlacementOnly`: 描画画像の比較を省き、モデル・配置・GPU の読み戻しまでを確かめる。

PowerShell 7・Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。

結果は `unity/Temp/PropsUnityQA` に出る(`result.txt`・`qa.log`・描画画像の PNG)。
