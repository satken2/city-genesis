# 送電線の検証

## 実行

```powershell
./run.ps1
```

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。

結果は `unity/Temp/PowerLinesQA` に出る(`result.txt`・`qa.log`・受電架構の確認画像 `gantry-close.png`)。合格すると、本編に無い接写モデル(`p1101`・`p1102` の `*_close`)の meta とミラープレハブを本編の `Assets` へ写す(既存の meta と GUID が食い違えば止まる)。
