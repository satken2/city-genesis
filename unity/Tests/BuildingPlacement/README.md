# 建物配置の回帰検証

## 実行

- `./run.ps1`: 道路沿いの配置の幾何計算(道路の両側・奥行き、道路への接触と食い込みの判定)を .NET 上で確かめる。
- `./run-unity.ps1`: 本編のコンポーネントで、配置方法の切り替え・道路への吸着・寸法の固定・選び直しなどの配置操作を確かめる。

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。`run-unity.ps1` は Unity の場所を csproj から求め、`-UnityExe` で指定もできる。

`run.ps1` の結果はコンソールに、`run-unity.ps1` の結果は `unity/Temp/BuildingPlacementQA` に出る(`result.txt`・`qa.log`)。
