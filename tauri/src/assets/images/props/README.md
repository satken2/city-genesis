# プロップのサムネイル

`unity/Assets/Models/Props/{ID}.fbx` を `tauri/tools/render_prop_thumbnails.py` で撮影した透過PNG。
通常版の数値IDのみを対象にし、元のFBX・材質・テクスチャを変更しない。
自作モデルのレンダリングであり、新しい外部素材は含まない。
看板121〜126の面に使われるAI生成画像の記録は公開M文書§M.7.7を参照する。

`city-genesis` から `py -3 tauri/tools/render_prop_thumbnails.py` で差分生成する。
特定IDだけの更新は `--only 1,2`、全件の再撮影は `--force`。
PNGはGit管理し、ローカルのハッシュキャッシュ `.thumbnail-cache.json` は管理しない。
分類は `tauri/src/data/propCategories.json`、表示名はバックエンドの `prop_types` が正。
