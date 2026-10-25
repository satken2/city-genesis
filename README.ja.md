<p align="center">
  <img src="docs/images/logo_small.png" alt="City Genesis" width="480">
</p>

<p align="center">
  <strong>ジオラマメーカーでも渋滞シミュレーターでもない、本物の経済シミュレーション。</strong>
</p>

<p align="center">
  <a href="https://store.steampowered.com/app/5021240/CityGenesis/">Steam ストアページ</a>
</p>

[English](README.md) | Japanese (original)

# City Genesis

City Genesis は、都市建設と経済のシミュレーションゲームです。プレイヤーは市長としてインフラと公共サービスを整え、市民や企業の活動を通じて街を育てます。

このリポジトリには、オープンソースの Unity グラフィックと Tauri UI、モデル原本、開発者向けドキュメントを収めています。シミュレーションコアは別の非公開コンポーネントで、HTTP API を通じて利用します。

## ゲームの考え方

ゲームは**計画**と**シミュレーション**を交互に進めます。都市を調べ、予算を配分し、建設計画を用意してから、明示的に次のエポックを実行します。考えている間に経済は進みません。

重いシミュレーションは、描画ループから独立したエポック実行中に行います。計画中も車や歩行者は動きますが、コアの結果を視覚化する演出です。フレームレート、カメラ位置、描画の細かさが経済の結果を変えてはいけません。

## 構成

Unity は都市の描画と空間的な操作を担当します。Tauri は Unity のウィンドウに React の UI を重ね、都市シミュレーションに必要なパネル・表・グラフを Web 技術で表現します。都市の状態と経済のルールはコアが持ちます。

![Unity グラフィックと Tauri UI がそれぞれ HTTP でコアへ接続し、MemoryMappedFile の IPC で操作状態を交換する構成](docs/images/architecture.svg)

| コンポーネント | 担当 | ソース |
| --- | --- | --- |
| Unity / C# | 3D描画、可視化、選択、配置 | [`unity/`](unity/) |
| Tauri / Rust / React / TypeScript | UI、ウィンドウ管理、アプリの寿命 | [`tauri/`](tauri/) |
| コア | 都市の状態、シミュレーション、HTTP API を通じた保存 | 別の非公開リポジトリ |

両フロントはコアの HTTP API から都市データを読み、編集を送ります。直接の IPC では選択・編集状態・表示の指示を交換します。たとえば Unity で建物を選ぶと UI に詳細が開き、UI で建設ツールを選ぶと Unity の入力モードが切り替わります。

デスクトップアプリは Tauri が起動・管理します。ブラウザ版は UI と Unity のソースを共有し、デスクトップのウィンドウ管理と共有メモリを Web シェルと JavaScript のブリッジに置き換えます。

## 開発

デスクトップ版は Windows を対象にしています。

[GitHub Releases](https://github.com/satken2/city-genesis/releases)では、コア・Tauri・Unity を含む Steam 向けと同じ製品版一式を ZIP で無料配布します。Steam経由で起動されていない場合は、一部機能が制限されます。

### 初回の準備

必要なツールは次のとおりです。

| ツール | 用途 |
| --- | --- |
| Unity Hub と [`ProjectVersion.txt`](unity/ProjectSettings/ProjectVersion.txt) に記載された版の Unity Editor | 3D画面の起動・Unity 側のテスト |
| Node.js 22 以降と npm | UI の依存関係の取得・起動・テスト |
| Rust の stable MSVC ツールチェーン、Microsoft C++ Build Tools の「C++ によるデスクトップ開発」、WebView2 Runtime | Tauri の起動。導入方法は [Tauri の前提条件](https://v2.tauri.app/start/prerequisites/#windows)を参照 |
| .NET 8 SDK | Unity 側の .NET テストを実行する場合に必要 |

クローン後や依存関係が変わる更新を取り込んだ後は、リポジトリ直下で UI の依存関係をインストールします。

```powershell
npm --prefix tauri ci
```

Unity Hub に `unity/` を追加して開き、パッケージの取得・アセットの読み込み・スクリプトのコンパイルが終わるまで待ちます。Unity 側の .NET テストを実行する場合は、エディタから C# プロジェクトファイルも生成し、`unity/Assembly-CSharp.csproj` があることを確認してください。

### 初回起動

1. 配布 ZIP をリポジトリの外へ展開します。コアは `Core/` の中身一式を使うため、`genesis.exe` だけを取り出さないでください。
2. 配布物の `Core/` フォルダで PowerShell を開き、次を実行します。このターミナルは起動したままにします。

   ```powershell
   $env:GENESIS_API_PORT = "5000"
   .\genesis.exe
   ```

   [http://127.0.0.1:5000/docs](http://127.0.0.1:5000/docs) が開けば、コアの準備は完了です。ポート5000は他のコアやアプリと重複しないようにしてください。
3. 別の PowerShell をリポジトリ直下で開き、UI を起動します。

   ```powershell
   cd tauri
   npm run tauri dev
   ```

4. Unity Editor で `unity/Assets/Scenes/GameScene.unity` を開いて再生します。UI から都市を作成するかセーブを開き、3D画面に都市が表示されることを確認します。

この構成では、開発中の UI と Unity Editor が `http://127.0.0.1:5000` のコアへ接続します。終了するときは UI と Unity の再生を止め、コアのターミナルで `Ctrl+C` を押します。

### テスト

UI のテストと型チェック・バンドルの確認は、リポジトリ直下から実行できます。コアや Unity の起動は不要です。

```powershell
npm --prefix tauri test
npm --prefix tauri run build
```

Unity 側の .NET テストは、初回の準備で C# プロジェクトファイルを生成してから、対象の実行スクリプトを使います。たとえば道路スナップのテストは次のとおりです。

```powershell
./unity/Tests/RoadSnapping/run.ps1
```

必要な DLL と `bin/`・`obj/` はテスト実行時に再生成されるため、Git から取得したり手動でコピーしたりする必要はありません。このテストもコアや Unity の再生は不要です。対象ごとの条件は [RoadSnapping README](unity/Tests/RoadSnapping/README.ja.md) など、`unity/Tests/` の各 README を参照してください。UI の個別テスト・ブラウザ版・配布ビルドは [UI README](tauri/README.ja.md) にあります。

## ドキュメント

グラフィックと UI の開発者に必要な概念と契約を説明します。ゲーム用語は[翻訳用語対応表](docs/ja/contributing/翻訳用語対応表.md)に揃えます。

- [シミュレーションの概念](docs/ja/economics/1_全体概要.md)：都市が表すものとエポックの位置付け。
- [コンポーネント連携](docs/ja/programs/コンポーネント連携.md)・[IPC](docs/ja/programs/IPC連携の整理.md)：3つのコンポーネントの協調。
- [データロード](docs/ja/geometries/H_バックエンドデータロード.md)・[UI共通基盤](docs/ja/programs/UI共通基盤.md)：描画と UI の共通の仕組み。
- [互換性](docs/ja/programs/バージョニングと互換性.md)：フロント・コア・IPC 契約の版の関係。

個別の HTTP 操作は、コアの `/docs`・`/openapi.json` またはリポジトリの [OpenAPI 仕様](docs/api/openapi.json)を参照してください。

## コントリビューション

Unity・Tauri のソースと、それを説明するドキュメントへの Pull Request を歓迎します。大きな変更は先に Issue で相談してください。

経済シミュレーションは非公開のコアが計算しているため、このリポジトリへの Pull Request だけでは変更が完結しません。次のものは Issue で提案してください。

- 経済シミュレーションの変更
- コアのインターフェースの変更
- 公開ドキュメントのうち、経済シミュレーションを説明する内容の変更

Pull Request では次に従ってください。

- 作業前に [`AGENTS.md`](AGENTS.md) と、変更するディレクトリの `AGENTS.md` を読んでください。
- ドキュメントは日本語が原本です。日本語を先に直し、英訳も同じ Pull Request でそろえます。用語は[翻訳用語対応表](docs/ja/contributing/翻訳用語対応表.md)に従います。
- 画像・音・3Dモデルなどの素材を加えるときは、出所と利用条件を確認し、[素材とクレジット](docs/ja/contributing/素材クレジット管理.md)に従ってクレジットに追記します。
