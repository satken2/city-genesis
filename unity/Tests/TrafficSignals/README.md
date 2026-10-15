# 交差点の信号機の検証

## 実行

```powershell
./run.ps1
```

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。Unity の場所は csproj から求め、`-UnityExe` で指定もできる。

結果は `unity/Temp/TrafficSignalsQA` に出る(`result.txt`・配置の一覧を含む `qa.log`・交差点ごとの俯瞰 `*-overview.png` と運転席視点 `*-driver.png`)。
