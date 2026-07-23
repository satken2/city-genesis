# src-tauri/tools フォルダ

Tauriアプリに同梱して配布する補助スクリプト置き場。`tauri.conf.json` の `bundle.resources` に登録されており、`app.path().resolve(..., BaseDirectory::Resource)` で実行時パスを解決できる。

## height_map_bin2npy.py

地形エディタの `.npy` エクスポート機能(`src/lib.rs` の `export_terrain_as_npy` コマンド)から、ユーザー環境の `python` / `python3` コマンドで呼び出される。地形エディタの作業コピー(`meta.json` + `near.bin`)を、バックエンドが読み込める `.npy` ファイルに変換する。

**このファイルは `genesis-core/tools/height_map_bin2npy.py` の複製である。** 配布パッケージに同梱する必要があるためこちらにもコピーを置いているが、変換ロジック自体はバックエンド側リポジトリの `tools/` に由来する。どちらか一方を変更した場合は、もう一方も同じ内容に更新すること。

呼び出しには Python と numpy がユーザー環境にインストール済みであることが前提となる(このアプリ自体はPythonをバンドルしない)。
