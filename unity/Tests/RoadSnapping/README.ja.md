# 道路の並行・終端揃えスナップ検証

[English](README.md)

並行・終端揃えのスナップに加え、`PolylineUtils` と `RailJunctionGeometry` の射影、線路の吸着候補とインフラ接続の区別も確かめる。

## 準備

- `unity/ProjectSettings/ProjectVersion.txt` に記載された版の Unity Editor。
- .NET 8 SDK。

クローン後の初回は、Unity Editor で `unity/` を開き、C# プロジェクトファイルを生成する。`unity/Assembly-CSharp.csproj` があることを確認する。Unity のアセンブリは、手元にインストールした Editor のものを参照する。

## 実行

このディレクトリで実行する。

```powershell
./run.ps1
```

`run.ps1` がランタイムのアセンブリとテストをビルドし、必要な DLL を含む `bin/`・`obj/` を再生成する。これらの生成物は Git の管理対象に含めない。テストは Unity を起動せず、.NET 上で動く。

結果はコンソールに出る。
