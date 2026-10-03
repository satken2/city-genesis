# City Genesis Docs

`city-genesis` リポジトリに同居する、Unity・TauriのOSS開発者向けドキュメント。MkDocsの公開ソースは `docs/`、教材の作者向け設計書はサイト外の `tutorials/` に置く。コア内部の詳細は別リポジトリの `genesis-core/docs/` にあり、公開対象に含めない。

## 文書の役割

| 配置 | 内容 |
| --- | --- |
| `docs/economics/` | 経済の概念・データモデル・観測できるゲームルール |
| `docs/geometries/` | 形状データ・描画の仕組み・モデル制作の共通規約 |
| `docs/programs/` | UIの仕様と、API・IPC・セッション等の共有契約 |
| `docs/api/` | 自動生成するAPI資料 |

執筆の共通ルールは [AGENTS.md](AGENTS.md) に定義する。同じ仕様は一つのページで説明し、他のページから参照する。

## ビルドとプレビュー

このディレクトリをカレントにして実行する。

```powershell
python -m pip install -r requirements.txt
python -m mkdocs build --strict
python -m mkdocs serve
```

プレビューは `http://127.0.0.1:8000`。ページ追加・削除時は `mkdocs.yml` のnavと関連リンクを揃え、ビルドと表示を確認する。

## API資料の生成

`fetch_openapi.py` はOpenAPIを取得し、`docs/api/openapi.json` と仕様を埋め込んだ `docs/api/index.html` を生成する。生成物は手編集しない。

```powershell
python fetch_openapi.py
python fetch_openapi.py --source http://localhost:5000/openapi.json
python fetch_openapi.py --source ./my-spec.json
```

指定が無い場合はlocalhost:5000を試し、接続できなければ `../unity/openapi.json` を使う。実行中のコアが別ポートの場合は `--source` で指定する。
