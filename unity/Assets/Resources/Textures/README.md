# 地形テクスチャの出所

| ファイル | 内容 | 出所 | ライセンス |
| --- | --- | --- | --- |
| `cliff-rock-albedo.png` | 崖用岩肌アルベド(Rock051 2K Color) | https://ambientcg.com/a/Rock051 | CC0 1.0 (商用利用可・クレジット不要) |
| `cliff-rock-normal.png` | 崖用岩肌法線マップ(Rock051 2K NormalGL) | https://ambientcg.com/a/Rock051 | CC0 1.0 (商用利用可・クレジット不要) |
| `ground-sand-albedo.jpg` | 砂の地面(Aerial Beach 01 2K Diffuse、作者 Rob Tuytel)。砂漠・雪原の濃淡に使う | https://polyhaven.com/a/aerial_beach_01 | CC0 1.0 (商用利用可・クレジット不要) |
| `ground-dry-grass-albedo.jpg` | 枯れ草の地面(Withered Grass 2K Diffuse、作者 Charlotte Baglioni)。乾燥地に使う | https://polyhaven.com/a/withered_grass | CC0 1.0 (商用利用可・クレジット不要) |

- 崖テクスチャは `TerrainManager.EnsureCliffTextures` が **このファイル名を前提に** `Resources.Load` するため、リネームする場合はコード側も変更すること。
- 地面テクスチャは `GroundCoverPalette`(`Resources/Terrain/GroundCoverPalette.asset` とコード既定値 `GroundCoverProfile.CreateDefaults`)が `Textures/ground-*` のパスで参照する。リネームする場合は両方を直すこと。
- 地面テクスチャの平均輝度(リニア)はプロファイルの `textureLuminance` に書いてある。差し替えたら測り直すこと(濃淡だけを使う種別の明るさの基準になる)。
- 法線マップはOpenGL(Y+)規約の `NormalGL` を使用している。差し替える際もY+規約のものを選ぶこと(DirectX規約だと凹凸が反転する)。
- 法線マップのインポート設定は Texture Type = Normal map 必須(.metaで設定済み)。
