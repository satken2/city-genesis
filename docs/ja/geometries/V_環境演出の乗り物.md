# V. 環境演出の乗り物(高高度の航空機と沖合の船舶)

空を横切る航空機と、海を進む船舶をUnityだけで描く。経済シミュレーションへ何も書き戻さず、保存もしない飾りである。

航空機と沖合の船は、都市の状態に関わらず一定の頻度で現れる。空港が近くに無くても、高い空を旅客機が横切って見えるようにするためである。入港船だけは都市の状態に従い、稼働中の港へ外洋から向かって岸壁に横付けし、荷役の時間だけ留まる。港ごとにエポックに1回抽選し、エポックが進むと入れ替える。

船は保存された海岸線を陸地の正とし、外洋だけを航行する。海岸線は埋立の完了で広がるので、取り直して版が変わると沖合の船を配り直す。乗り物はGameObjectを持たずに毎フレーム描き、交通演出の時計 `TrafficClock` で進むので、交通の一時停止中は止まる([車両の走行描画](O_車両の走行描画.md))。船の航跡は海面に重ねる半透明の帯で、泡のノイズを海面と共有する(`Assets/Shaders/WaterNoise.hlsl`)。

実装の入口は `AmbientVehicleManager`(発生・移動・描画。入港船は `AmbientVehicleManager.PortCalls.cs`)、`SeaRoute`(入港船の航路)、`ShipWake`(航跡)。

## モデルの命名

モデルは `Assets/Models/Props/` に置き、描画コードが固定名で読む。船は接写 `_close` と遠景 `_far` を持ち、投影サイズで切り替える。航空機は通常のモデルだけで描く。手動配置用に、同じ形状を数値IDの別名として `prop_master` に登録している([地形プロップ](M_地形プロップ.md))。原本は `blender/props.blend`。

| モデル名 | 乗り物 |
|---|---|
| `ambient_airliner`・`ambient_propeller`・`ambient_helicopter` | 旅客機・プロペラ機・ヘリコプター |
| `ambient_container_ship`・`ambient_tanker`・`ambient_cruise_ship`・`ambient_fishing_boat` | 沖合の船。漁船以外は同じ種類の港への入港船にも使う |
| `ambient_bulk_carrier` | ばら積み船。入港船だけに使い、別名を持たない |
