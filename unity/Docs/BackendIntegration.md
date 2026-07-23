# PythonバックエンドのビルドとUnityへの統合手順

このドキュメントでは、Python FastAPIバックエンドをNuitkaを使用して実行可能ファイル（exe）化し、Unityプロジェクトに統合する手順を説明します。

## 1. Nuitkaの準備

Python環境にNuitkaをインストールします。

```bash
pip install nuitka
```

## 2. バックエンドのビルド

Pythonバックエンドのルートディレクトリで以下のコマンドを実行し、exe化します。
`--onedir` オプションを使用することで、依存関係を含めたフォルダ形式で出力されます。

※ `main.py` はバックエンドのエントリーポイントとなるファイル名に置き換えてください（例: `app.py`, `server.py`など）。

```bash
python -m nuitka --standalone --onedir --follow-imports --enable-plugin=numpy --output-dir=Build main.py
```

ビルドが成功すると、`Build/main.dist`（またはエントリーポイント名に基づくフォルダ）が生成されます。このフォルダ内には `main.exe` と必要なライブラリが含まれています。

## 3. Unityへの配置

1. Unityプロジェクトの `Assets` フォルダ直下に `StreamingAssets` フォルダがあることを確認します（なければ作成してください）。
   - パス: `Assets/StreamingAssets`
2. ビルドで生成された `.dist` フォルダの中身を、`StreamingAssets/Backend` フォルダに配置します。
   - 構成例:
     ```
     Assets/
       StreamingAssets/
         Backend/
           main.exe  <-- エントリーポイントのexe
           python310.dll
           ...その他依存ファイル
     ```

## 4. 実行ファイルの名称確認

`AppRoot.cs` では、デフォルトで `backend.exe` を探すように設定します（もしくはビルド元のファイル名に合わせてください）。
ビルドされたexe名が `main.exe` の場合は、`AppRoot.cs` のコードを修正するか、exeの名前を変更してください。

## 注意事項

- **StreamingAssets**: このフォルダに配置することで、Unityのビルド後もそのままのファイル構成でコピーされ、ファイルパスでアクセス可能になります。
- **ポート競合**: 開発中（Unity Editor実行時）に別途Pythonサーバーを手動で立ち上げていると、ポートが競合する可能性があります。Unityから自動起動する場合は手動起動したものを停止してください。
