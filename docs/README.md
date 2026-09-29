# City Genesis Docs

City Genesis のドキュメント管理リポジトリ。`docs/` は MkDocs Material でビルドして Cloudflare Pages で公開する。genesis-coreレポジトリの`docs/` はクローズドソースのコアシミュレータ開発者のみが参照する内部ドキュメントであり、絶対に公開しない。

## 前提：オープンソースとクローズドソースの構成

City Genesis は3つのコンポーネントで構成される。

| コンポーネント | 技術スタック | ソースコード |
|---|---|---|
| コアシミュレータ（バックエンド） | Python / FastAPI | **クローズドソース** |
| グラフィックプログラム（フロントエンド） | Unity | オープンソース |
| UIプログラム | Tauri | オープンソース |

`docs/` は、オープンソース化した後にフロントエンド・UI開発者がコアシミュレータの内部ロジックを知らずとも開発できるよう、外部仕様として書かれたドキュメントである。genesis-coreレポジトリの`docs/` はコアシミュレータ開発者自身のための内部設計ドキュメントである。

## フォルダ構成

```
genesis-docs/
├── mkdocs.yml              # MkDocs 設定ファイル
├── requirements.txt        # Python 依存パッケージ
├── fetch_openapi.py        # バックエンドから openapi.json を取得するスクリプト
├── docs/                   # 公開対象（MkDocs のビルドソース）
│   ├── index.md            # トップページ
│   ├── overrides/          # MkDocs テンプレート上書き
│   ├── economics/          # 経済シミュレーションの概念・データモデル（アルゴリズム不含）
│   ├── geometries/         # 形状データの仕様＋フロントエンドのレンダリング実装仕様
│   ├── programs/           # コンポーネント間連携仕様（IPC、起動順序、入力制御）
│   ├── api/                # OpenAPI 3.1 仕様
│   └── images/             # ドキュメント内の画像アセット
├── private/                # 非公開（MkDocs のビルド対象外。絶対に公開しない）
│   ├── economics/          # 経済シミュレーションのアルゴリズム詳細・計算式
│   ├── game-logic/         # ゲーム内処理のアルゴリズム詳細（地区処理、論理長計算等）
│   └── programs/           # ビルド手順・運用等の内部開発プロセス
└── site/                   # ビルド出力（.gitignore 対象）
```

## 分類ルール

### ルール1：「What」vs「How」

| 基準 | `docs/`（公開） | genesis-coreレポジトリの`docs/`（非公開） |
|---|---|---|
| 視点 | 「何があるか」「どういう概念か」 | 「どう計算するか」「どう実装するか」 |
| 例 | 「市場には供給者と需要者がいて、価格で取引される」 | 「マッチングスコア = (price + transport) / attraction + noise」 |

概念の定義、データモデル（プロパティ名・型・取り得る値）、ステートマシン（statusの遷移）、ゲームルールとしてプレイヤーが知覚できる挙動は `docs/` に置く。スコア関数の数式と係数、マッチングの貪欲法の詳細手順、賃金・家賃調整の具体的な増減率、ペナルティコストの掛け方等の計算詳細は genesis-coreレポジトリの`docs/` に置く。

### ルール2：観測可能性

| 基準 | `docs/`（公開） | genesis-coreレポジトリの`docs/`（非公開） |
|---|---|---|
| 観点 | APIレスポンスやIPCイベントとして外部から観測できること | バックエンド内部の計算過程（結果しか見えないこと） |
| 例 | エージェントの status（PENDING, ACTIVE等）、パスの座標データ | マッチングのスコアリング、賃金調整の内部計算 |

### ルール3：コンポーネント帰属

| 実装先 | `docs/`（公開） | genesis-coreレポジトリの`docs/`（非公開） |
|---|---|---|
| Unity / Tauri（オープンソース） | その実装に必要なすべての仕様 | — |
| バックエンド（クローズドソース） | 外部インターフェースとデータモデルのみ | 内部アルゴリズムの詳細 |
| 両方にまたがる | インターフェース契約（IPC、API） | 各自の内部処理 |

フロントエンド（Unity）のレンダリング実装（LOD処理、メッシュ生成、データロード・差分同期、アート制作仕様）は `docs/` に置く。バックエンドの内部処理（スポーンロジック、経路探索、市場マッチングの計算式）は genesis-coreレポジトリの`docs/` に置く。

### ルール4：プログラム間連携の帰属

| 種類 | 場所 | 理由 |
|---|---|---|
| IPCプロトコル仕様 | `docs/` | 両コンポーネントの開発契約 |
| 起動順序・セッション管理 | `docs/` | 各コンポーネントの責務範囲 |
| 入力制御（Unity） | `docs/` | Unity開発者の実装対象 |
| ビルド・梱包の運用手順 | genesis-coreレポジトリの`docs/` | 内部開発プロセス |
| スタブパターン等の開発手法 | genesis-coreレポジトリの`docs/` | 内部開発プロセス |

### ルール5：経済ドキュメントの概念とアルゴリズムの分離

`docs/economics/` には概念レイヤーのみを記載する。計算アルゴリズムは対応する genesis-coreレポジトリの`docs/economics/` のファイルに記載する。

**`docs/economics/` に書く内容:**
- 「市場には商品種別がある」「賃金は労働力に応じて決まる」「家賃は需要過剰で上がり、空室で下がる」等の概念
- データモデル（プロパティ名、型、取り得る値）
- ステートマシン（statusの遷移図）
- ゲームルールとしてプレイヤーが知覚できる挙動

**genesis-coreレポジトリの`docs/economics/` に書く内容:**
- スコア関数の数式と係数
- マッチングの貪欲法の詳細手順
- 賃金・家賃調整の具体的な増減率
- 生産目標の計算式
- ペナルティコストの掛け方

対応関係は、`docs/economics/8_商品市場.md` に対して genesis-coreレポジトリの`docs/economics/8_商品市場マッチング詳細ロジック.md` のように、同じ番号で対応付ける。

### ルール6：ジオメトリ・グラフィック関連の帰属

| 種類 | 場所 | 理由 |
|---|---|---|
| データ形式の定義（Bezier曲線の構造、ポリゴンの座標系） | `docs/` | フロントエンドがデータを解釈するためのインターフェース仕様 |
| メッシュ生成パイプライン | `docs/` | Unity開発者が実装するもの |
| LODシステム | `docs/` | Unity開発者が実装するもの |
| データロード・差分同期 | `docs/` | Unity開発者が実装するもの |
| アート制作仕様（建物パーツ等） | `docs/` | オープンソースのアーティストが参照するもの |
| パス交差の世界モデルルール | `docs/` | ゲームの概念定義 |

### ルール7：ゲームロジックの帰属

| 種類 | 場所 | 例 |
|---|---|---|
| ゲームのルールとして表現される挙動 | `docs/` | 「道路には論理長があり、渋滞で増える」「地区は連結性を保つ」 |
| その計算の内部実装 | genesis-coreレポジトリの`docs/` | 論理長の3段階計算式、地区切断のグラフ理論的検出アルゴリズム |

ゲームルールとしての概念説明は対応する `docs/economics/` のファイルに含め、計算アルゴリズムは genesis-coreレポジトリの`docs/game-logic/` に置く。

## ローカルでのビルド・プレビュー

```bash
# 依存パッケージのインストール
pip install -r requirements.txt

# ビルド
mkdocs build

# ライブプレビュー（ブラウザで http://127.0.0.1:8000 を開く）
mkdocs serve
```

## OpenAPI 仕様の更新

開発用のバックエンド（genesis-core）を起動した状態でスクリプトを実行すると、`docs/api/openapi.json` が更新される。
製品版・モックのバックエンドは `/openapi.json` を配信しない。

```bash
# バックエンドが localhost:5000 で起動中の場合
python fetch_openapi.py

# バックエンドが起動していない場合は city-genesis/unity/openapi.json からフォールバック
python fetch_openapi.py

# URL またはファイルパスを直接指定
python fetch_openapi.py --source http://localhost:5000/openapi.json
python fetch_openapi.py --source ./my-spec.json
```

実行後、`docs/api/openapi.json` をコミットして push すれば Cloudflare Pages が自動的に再デプロイする。

## Cloudflare Pages デプロイ

Cloudflare Pages が `main` ブランチへの push を検知して自動ビルド・デプロイする。

### ビルド設定

| 項目 | 値 |
|------|----|
| Production branch | `main` |
| Build command | `pip install -r requirements.txt && mkdocs build` |
| Build output directory | `site` |

### site_url の更新

Cloudflare Pages でプロジェクトを作成後、实际のURLが決まったら `mkdocs.yml` の `site_url` を更新する。

## ドキュメントの追加・変更手順

1. 内容が `docs/`（公開）か genesis-coreレポジトリの`docs/`（非公開）かを上記ルールで判定する
2. 該当するmdファイルを追加・編集する
3. `docs/` に新規ファイルを追加した場合は `mkdocs.yml` の `nav` にも追記する
4. `mkdocs serve` でローカル確認する
5. commit & push → Cloudflare Pages が自動デプロイ

### 判定に迷った場合

以下の質問に「はい」と答えられるなら genesis-coreレポジトリの`docs/` に入れる。

- この情報はバックエンド（クローズドソース）の計算手順を説明しているか？
- フロントエンド開発者はこの情報を知らなくても実装できるか？
- この情報が公開されても問題ないか？（アルゴリズムの秘匿性）

以下の質問に「はい」と答えられるなら `docs/` に入れる。

- この情報はフロントエンド（Unity / Tauri）の実装に必要か？
- この情報は「何があるか」「どういう概念か」を説明しているか？
- この情報はAPIやIPCのインターフェース仕様か？

## ビルド済みバイナリの配置方法

ビルド済みのゲームクライアントやUIのバイナリは、以下のいずれかの方法でホスト・公開します。

### 方法1：GitHub Releases での公開（推奨）

オープンソースであるフロントエンド・UIリポジトリの GitHub Releases 機能を利用します。

1. **手動アップロード**
   - GitHubリポジトリの「Releases」から新しいリリースを作成（タグ例：`v1.0.0`）。
   - 各プラットフォーム向けビルドファイル（`.zip`, `.dmg`, `.tar.gz` など）をドラッグ＆ドロップしてリリースに添付します。
2. **GitHub Actions による自動化**
   - リポジトリにCI/CDワークフローを構築し、Gitタグプッシュ時に自動ビルド＆自動リリース作成を行うように設定します。

### 方法2：Google Cloud Storage (GCS) バケットでの公開

特定のストレージバケット（例：`genesis-releases`）を用意し、一般公開のダウンロードリンクとして機能させます。

1. **GCSバケットの用意**
   - Google Cloud Consoleで一般公開アクセス（全員に読み取り権限：`allUsers` に `Storage Object Viewer`）を設定したバケットを作成します。
2. **バイナリのアップロード**
   - `gcloud` CLI（Google Cloud SDK）を使用してバケットにアップロードします。
     ```bash
     # Windows ビルドのアップロード
     gcloud storage cp ./dist/CityGenesis_Windows_x64.zip gs://genesis-releases/v1.0.0/CityGenesis_Windows_x64.zip

     # macOS ビルドのアップロード
     gcloud storage cp ./dist/CityGenesis_macOS.dmg gs://genesis-releases/v1.0.0/CityGenesis_macOS.dmg

     # Linux ビルドのアップロード
     gcloud storage cp ./dist/CityGenesis_Linux.tar.gz gs://genesis-releases/v1.0.0/CityGenesis_Linux.tar.gz
     ```
   - 必要に応じて、カスタムドメイン（例：`download.city-genesis.com`）やCDNをバケットの前面に構築します。



