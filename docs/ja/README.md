[English](../en/README.md) | Japanese (original)

# City Genesis

![City Genesis](../images/logo.png)

ジオラマメーカーでも渋滞シミュレーターでもない、本物の都市シミュレーション

## 特徴

- **自律経済シミュレーション** — 数十万の市民や店舗が消費・生産・投資を自律的に行い、有機的な都市経済を構築します。
- **ゼロサム金融システム** — 信用創造・中央銀行による破綻処理・税制までをシミュレート。お金が無から湧くことはなく、すべての支払いは誰かの受け取りになります。
- **流体交通モデル** — 都市の経済活動に応じたリアルな交通フローは、沿線店舗の売上や昼間人口の集積に直結。
- **サプライチェーン** — 原材料から加工・小売まで、需要と供給のバランスがすべての価格を決定します。
- **不動産市場** — 立地に応じた地価や家賃の変動や、不動産需要に応じたダイナミックな都市景観を形成。
- **オープンソース** — UnityフロントエンドとTauri UIはオープンソース。シミュレーションコアは別の非公開コンポーネントです。

## ドキュメント

日本語が原本です。英語ページは日本語原本からの翻訳です。

### 経済シミュレーション

経済主体・市場・金融・生産・消費・雇用・教育・貿易の概念

- [全体概要](economics/1_全体概要.md)
- [経済主体](economics/2_経済主体.md)
- [パスとノード](economics/3_パスとノード.md)
- [地区と土地区画](economics/4_地区と土地区画.md)
- [建物](economics/5_建物.md)
- [不動産市場](economics/6_不動産市場.md)
- [世帯](economics/7_世帯.md)
- [雇用と教育](economics/8_雇用と教育.md)
- [商品市場](economics/9_商品市場.md)
- [生産活動](economics/10_生産活動.md)
- [消費活動](economics/11_消費活動.md)
- [金融システム](economics/12_金融システム.md)
- [環境](economics/13_環境.md)
- [事故と災害](economics/14_事故と災害.md)
- [予算管理](economics/15_予算管理.md)
- [スコアと達成目標](economics/16_スコアと達成目標.md)
- [財の供給と輸送](economics/17_財の供給と輸送.md)
- [公共交通機関](economics/18_公共交通機関.md)
- [駅と駅舎](economics/19_駅と駅舎.md)
- [観光](economics/20_観光.md)

### コンポーネント連携

コア・Unityグラフィック・Tauri UIの構成と通信の仕組み

- [コンポーネント連携](programs/コンポーネント連携.md)
- [IPC連携](programs/IPC連携の整理.md)
- [セッションと保存](programs/セーブデータ仕様.md)
- [バージョニングと互換性](programs/バージョニングと互換性.md)
- [UI共通基盤](programs/UI共通基盤.md)
- [Unity入力制御](programs/Unity入力制御.md)
- [Unity表示モード](programs/Unity表示モード.md)
- [通知とアラート](programs/通知レベル管理.md)
- [チュートリアル基盤](programs/チュートリアル基盤.md)
- [サウンドシステム](programs/サウンドシステム.md)

### 幾何データ

パス・土地・建物・地形の形状仕様

- [パス形状](geometries/A_パス形状.md)
- [土地形状](geometries/B_土地形状.md)
- [建物形状](geometries/C_建物形状.md)
- [地形形状](geometries/D_地形形状.md)
- [LOD処理](geometries/E_LOD処理.md)
- [建物メッシュの生成 — 共通仕様](geometries/F_建物メッシュの生成.md)
- [建物メッシュの生成 — 生成方式](geometries/F1_建物メッシュの生成方式.md)
- [道路メッシュの生成](geometries/G_道路メッシュの生成.md)
- [バックエンドデータロード](geometries/H_バックエンドデータロード.md)
- [パスの交差処理](geometries/J_パスの交差処理.md)
- [地形プロップ](geometries/M_地形プロップ.md)
- [線路メッシュの生成](geometries/N_線路メッシュの生成.md)
- [車両の走行描画](geometries/O_車両の走行描画.md)
- [交通シミュレーションと走行描画の整合](geometries/T_交通シミュレーションと走行描画の整合.md)
- [マテリアル共有とエクスポート](geometries/P_マテリアル共有とエクスポート.md)
- [駅舎パスのメッシュ生成](geometries/Q_駅舎パスのメッシュ生成.md)
- [マテリアルの解決](geometries/R_マテリアルの解決.md)
- [火災エフェクトの描画](geometries/S_火災エフェクトの描画.md)
- [天気と環境光](geometries/U_天気と環境光.md)
- [環境演出の乗り物](geometries/V_環境演出の乗り物.md)
- [歩行者の描画](geometries/W_歩行者の描画.md)
- [アセットパック](geometries/X_アセットパック.md)

### 開発への参加

- [翻訳用語対応表](contributing/翻訳用語対応表.md)
- [素材とクレジット](contributing/素材クレジット管理.md)

### API リファレンス

バックエンド REST API の全エンドポイント仕様

[OpenAPI](../api/openapi.json)

© 2024-2026 City Genesis Project
