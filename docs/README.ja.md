[English](README.md) | Japanese (original)

# City Genesis ドキュメント

Unity・Tauri の OSS 開発者向け公開ドキュメントです。GitHub 上で Markdown を直接読める構成で、サイトのビルドは不要です。**日本語が原本で、英語はその翻訳です。** 非公開コアの内部説明は別リポジトリの `genesis-core/docs/` に置きます。

- [日本語の紹介・目次](ja/README.md)
- [英語の紹介・目次](en/README.md)

## 配置と更新

| 配置 | 内容 |
| --- | --- |
| `ja/` | 日本語原本。`ja/README.md` が目次 |
| `en/` | 英訳。`en/README.md` が目次 |
| `{ja,en}/economics/` | 経済の概念と観測できるゲームルール |
| `{ja,en}/geometries/` | 形状データと描画の共通の仕組み |
| `{ja,en}/programs/` | フロントの全体構成と共通基盤、モジュール間の通信 |
| `{ja,en}/contributing/` | 翻訳用語と素材クレジットの約束 |
| `images/` | 共通の画像と図 |

日本語を先に編集し、対応する英訳を同じ変更で揃えます。内容が食い違う場合は日本語を優先します。ページの追加・削除・移動では両言語の目次と関連リンクを更新します。執筆の共通ルールは [AGENTS.md](AGENTS.md) に定め、同じ概念は1ページで説明して他から参照します。
