# 車両描画の回帰検証

## 実行

- `./run.ps1`: 一般車・公共交通・路上駐車・歩行者・ドライブの描画と動きをすべて確かめる。
- `./run.ps1 -ParkingOnly`: 路上駐車だけを確かめる。
- `./run.ps1 -TransitOnly`: 公共交通の走行・折り返し・車両選択と、車両モデルだけを確かめる。
- `./run.ps1 -DriveOnly`: ドライブ(走行中の車・列車を選んで眺める視点)に関わる部分だけを確かめる。
- `./run.ps1 -PedestrianModelsOnly`: 歩行者の FBX・歩行姿勢・描画時間だけを確かめる。

FBX を足した直後の初回は、ミラーを作ったところで検査へ進まずに終わることがある(`result.txt` が `RUNNING` のまま)。そのときはもう一度実行する。

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。Unity の場所は csproj から求め、`-UnityExe` で指定もできる。

結果は `unity/Temp/VehicleUnityQA` に出る(`result.txt`・`qa.log`・路上駐車の見本 `parking-preview.png`)。
