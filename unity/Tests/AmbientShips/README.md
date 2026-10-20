# 環境演出の船舶の検証

## 実行

```powershell
./run.ps1
```

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。

結果は `unity/Temp/AmbientShipsQA`(`-QaRoot` で変えられる)に出る(`result.txt`・`qa.log`・船ごとの `<モデル名>_lods.png`・`_close_bow.png`・`_close_aft.png`)。
