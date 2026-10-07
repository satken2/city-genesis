# unity

City Genesis の3D描画と入力を担当する Unity プロジェクト。都市データはコアから HTTP で取得し、Tauri のUIとは MMF で連携する。同じプロジェクトからブラウザ版の Web ビルド(WebGPU。`Assets/Editor/WebBuild.cs`)も作り、そのときUIとはページの JS で連携する。作業対象のサブディレクトリに AGENTS.md がある場合はそちらも読む。

## Assets/Scripts の領域

| ディレクトリ | 責務 |
| --- | --- |
| `API/`・`IPC/` | コアとの HTTP、UIとの MMF 通信 |
| `DataLoaders/` | データ取得・差分更新・LOD・描画物の寿命 |
| `MeshGenerators/` | 建物・道路・区画・地形のメッシュ生成 |
| `InputHandlers/`・`Editors/` | 入力の配送と編集状態の管理 |
| `VisualHandlers/` | 表示モード・ハイライト・可視化 |
| `Vehicles/`・`Weather/` | 交通・環境・音の描画演出 |
| `Tutorial/` | UIから指示された一時的な案内と制限 |
| `Utils/`・`UI/` | 共通処理と3D側のUI部品 |

## 共通ルール

- ワールド原点はマップ中心、東は +X、北は +Z。パス・ノードの相対Yと絶対標高、Unity とコアのセル番号を混同せず、既存の変換を使う。原点シフトは行わない。
- 地形高さは `Utils.GetGroundHeight` を使う。形状・高さ・接続の共有計算を各利用側で再実装しない。
- HTTP は `ApiClient`、UI連携は共通 IPC を通す。`UnityWebRequest` を直接作らない。
- 入力モードと表示モードは別の責務。編集時の切り替えは共通のステート・表示管理を通す。
- 実体の識別は `ObjectIdInfo` を使い、GameObject 名を解析しない。クリック対象のコライダー・タグ・入力ハンドラを整合させる。
- 大量の生成・更新は共通フレーム予算と既存の逆引き・空間索引を使う。毎フレームの全件走査を増やさない。
- Unity API はメインスレッドで扱う。生成したメッシュ・材質の所有と解放を明確にし、共有アセットを破棄しない。
- 別スレッドに逃がす重い計算は `Task.Run` ではなく `Utils/BackgroundWork` を通す(Web ビルドにはスレッドが無いため)。
- Windows と Web ビルドの違い(スレッド・Win32・IPC の運び方)は `BackgroundWork`・`WindowUtil`・`IpcTransport` の中だけで切り替える。呼び出し側に `#if UNITY_WEBGL` を書かない。
- モデルの原本は `blender/`、FBX は書き出し物、Resources のミラープレハブは生成物。モデルを移動・削除したら対応するミラーも整合させる。
- 冗長ログは `UnityLog.If` の共通フラグを使う。ホットパスで無条件に出力せず、起動引数や認証情報をログへ載せない。

## 参照先

- 起動・通信: `docs/docs/programs/コンポーネント連携.md`・`docs/docs/programs/IPC連携の整理.md`。
- ロード・LOD: `docs/docs/geometries/H_バックエンドデータロード.md`・`docs/docs/geometries/E_LOD処理.md`。
- 検証は `unity/Tests/` の対象ごとの README と実行ハーネスを使う。本編のシーン・マスター・モデルを検証用に変更しない。
