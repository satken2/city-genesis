# docs/ 公開ドキュメント

City Genesis の公開ドキュメント(mkdocs サイト)。読み手は UI(Tauri)とグラフィック(Unity)の OSS 開発者。
シミュレーション内部の計算式・アルゴリズムは非公開レポジトリ `genesis-core/docs/` に書き、ここには書かない。

## フォルダ構成

```
docs/
├── mkdocs.yml          # MkDocs 設定。新規ページは nav に追記する
├── requirements.txt
├── fetch_openapi.py    # OpenAPI仕様から api/openapi.json と api/index.html を作る(下記)
├── tutorials/          # シナリオ作者向けのステップ設計(基本・地区・インフラ・鉄道)と共通部品表(MkDocsソース階層外)
├── docs/               # ビルドソース(実ファイルはこの1段深い階層)
│   ├── index.md
│   ├── overrides/      # テンプレート上書き
│   ├── economics/      # 経済シミュレーションの概念・データモデル(アルゴリズム不含)
│   ├── geometries/     # 形状データの仕様とフロントエンドのレンダリング実装仕様
│   ├── programs/       # コンポーネント間連携(IPC、起動順序、入力制御)と UI の実装仕様
│   ├── api/            # OpenAPI 3.1(自動生成)
│   └── images/
└── site/               # ビルド出力(.gitignore)
```

## API Reference の再生成

`docs/api/openapi.json` と、それを埋め込んだ `docs/api/index.html` は `fetch_openapi.py` で作り直す。`--source` を省くと起動中の開発用バックエンド(`http://localhost:5000/openapi.json`)から取得し、`--source` には URL か openapi.json のパスを渡せる。URL から取得するときは、ローカルAPIの認証トークン(環境変数 `GENESIS_API_TOKEN` → `<ユーザーフォルダ>/api_token`)と `Host` をループバック(`127.0.0.1`・`localhost`・`[::1]`)の URL にだけ付ける。製品版・モックのバックエンドは `/openapi.json` を配信しないので、開発用のバックエンドを使う。サーバーを起動しないときは、開発用のコアで `from api import app; app.openapi()` の出力をファイルに書いて `--source` に渡す。このとき `api` の import はユーザーフォルダのトークンファイルを書き換えるので、`XDG_DATA_HOME`(Windows は `LOCALAPPDATA`)を一時フォルダに向けて実行する。

## 公開 docs と非公開 docs の振り分け

| 公開 `docs/docs/` | 非公開 `genesis-core/docs/` |
| --- | --- |
| 「何があるか」「どういう概念か」 | 「どう計算するか」「どう実装するか」 |
| API レスポンス・IPC イベントとして外から観測できること | バックエンド内部の計算過程(結果しか見えないこと) |
| 概念の定義、データモデル(プロパティ名・型・取り得る値)、status のステートマシン、プレイヤーが知覚できるゲームルール | スコア関数の数式と係数、マッチングの手順、賃金・家賃の調整率、ペナルティの掛け方 |
| Unity/Tauri の実装仕様すべて(LOD、メッシュ生成、データロード・差分同期、入力制御、アート制作仕様) | スポーン・経路探索・市場マッチングなどコア内部のアルゴリズム |
| IPC プロトコル、起動順序・セッション管理(両コンポーネントの開発契約) | ビルド・梱包手順、スタブパターン等の開発手法 |

- `docs/economics/N_xxx.md`(概念)と `genesis-core/docs/economics/N_xxx詳細ロジック.md`(計算)は同じ番号で対応付ける。ゲームルールとしての概念は economics/ に、その計算アルゴリズムは `genesis-core/docs/game-logic/` に置く。
- 迷ったら「バックエンドの計算手順の説明か」「フロント開発者が知らなくても実装できるか」「公開するとアルゴリズムの秘匿上問題があるか」のいずれかが「はい」なら非公開側。

## 書いてはいけないもの(economics/ で特に厳守)

- 操作説明書・UI 挙動の描写(「〜パネルで確認できる」「グラフは実線で表示」等)。UI の実装仕様が必要なら programs/ に書く。
- API に露出しない内部変数・定数名。書いてよい名前は API のプロパティ(`genesis-core/app/schemas.py` で確認)、マスターデータのキー、`consts.py` から Tauri/Unity に同期される共有定数のみ。バックエンド専用の定数は「一定の閾値」等の定性表現にし、ゲームルール上意味のある値だけを残す。
- 内部の計算手順・関数名・実装ファイル名(非公開側の「詳細ロジック」へ)。
- 旧仕様との比較・変更経緯。現在の仕様だけを書く。

## 追加・変更手順

1. 上の基準で公開/非公開を判定し、該当する md を編集する。
2. `docs/docs/` に新規ファイルを追加したら `mkdocs.yml` の `nav` にも追記する。
3. `mkdocs serve` でローカル確認する。commit & push で Cloudflare Pages が自動デプロイされる。

## 見出しへのリンク

アンカーは `mkdocs.yml` の `toc.slugify` に `pymdownx.slugs.slugify`(case: lower)を指定して生成される。日本語はそのまま残り、英字は小文字化され、`\w`・`-`・空白以外の文字(`.`・括弧・全角記号)は消え、空白は `-` になる。

- 例: `### 9.1. ステート別の表示プリセット` → `#91-ステート別の表示プリセット`、`## 8.7 柱(pillar)とキャップ(pillar_cap)` → `#87-柱pillarとキャップpillar_cap`
- `_1` のような連番アンカーは既定の slugify の形式で、この設定では生成されないのでリンク先に使わない。
- 同じページに同名の見出しがあると2つ目以降に `_1`, `_2` が付くので、リンク先の見出しは一意にしておく。
