# 地形テクスチャの出所

| ファイル | 内容 | 出所 | ライセンス |
| --- | --- | --- | --- |
| `cliff-rock-albedo.png` | 崖用岩肌アルベド(Rock051 2K Color) | https://ambientcg.com/a/Rock051 | CC0 1.0 (商用利用可・クレジット不要) |
| `cliff-rock-normal.png` | 崖用岩肌法線マップ(Rock051 2K NormalGL) | https://ambientcg.com/a/Rock051 | CC0 1.0 (商用利用可・クレジット不要) |

- 崖テクスチャは `TerrainManager.EnsureCliffTextures` が **このファイル名を前提に** `Resources.Load` するため、リネームする場合はコード側も変更すること。
- 法線マップはOpenGL(Y+)規約の `NormalGL` を使用している。差し替える際もY+規約のものを選ぶこと(DirectX規約だと凹凸が反転する)。
- 法線マップのインポート設定は Texture Type = Normal map 必須(.metaで設定済み)。
