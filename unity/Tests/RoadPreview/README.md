# 道路プレビューの保持検証

道路・線路・送電線の敷設中のプレビューと、敷設前の線形・水域横断の判定を、本編の `RoadPlanManager` で確かめる。

## 実行

```powershell
./run-unity.ps1
```

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。

結果は `unity/Temp/RoadPreviewQA` に出る(`result.txt`・`qa.log`)。
