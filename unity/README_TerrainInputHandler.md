# 地形InputHandler統合について

## 概要

地形データ（Terrain）も新しいInputHandlerシステムに統合され、パーセルエディターの機能も含めて一元的に管理されるようになりました。

## 変更内容

### 1. TerrainInputHandlerクラスの作成

- **ファイル**: `Assets/Scripts/Input/TerrainInputHandler.cs`
- **機能**: 
  - 地形のクリック・ホバーイベント処理
  - マーカー表示（道路敷設モード、編集モード時）
  - パーセルエディターとの統合
  - 地点選択機能

### 2. InputHandlerクラスの更新

- **ファイル**: `Assets/Scripts/Input/InputHandler.cs`
- **追加機能**:
  - `InputObjectType.TERRAIN`の追加
  - `HandleTerrainHover`メソッドの追加
  - `CallObjectHover`メソッドの追加
  - テレインオブジェクト処理の統合

### 3. TerrainLoaderクラスの更新

- **ファイル**: `Assets/Scripts/DataLoaders/TerrainLoader.cs`
- **追加機能**:
  - 地形作成時に自動的に`TerrainInputHandler`をアタッチ

### 4. ParcelEditorInputHandlerの廃止

- **削除ファイル**: `Assets/Scripts/Input/ParcelEditorInputHandler.cs`
- **理由**: 機能を`TerrainInputHandler`に統合

## 使用方法

### 基本的な動作

1. **通常モード** (`InputMode.MODE_NORMAL`)
   - 地形クリック: 地形情報の表示
   - パーセルエディターの地点選択モード時: 地点選択

2. **建設モード** (`InputMode.MODE_CONSTRUCT`)
   - 地形ホバー: マーカー表示
   - 地形クリック: 建設可能地点の確認
   - パーセルエディターの地点選択モード時: 地点選択

3. **編集モード** (`InputMode.MODE_EDIT`)
   - 地形ホバー: マーカー表示
   - 地形クリック: 地形編集機能
   - パーセルエディターの地点選択モード時: 地点選択

### マーカー表示

マーカーは以下の条件で表示されます：
- パーセルエディターの地点選択モード時
- 道路敷設モード時 (`InputMode.MODE_CONSTRUCT`)
- 編集モード時 (`InputMode.MODE_EDIT`)

### 必要なリソース

以下のマテリアルをResourcesフォルダに配置することをお勧めします：
- `MarkerMaterial.mat`: マーカー用マテリアル

## カスタマイズ

### マーカーの外観

```csharp
public class TerrainInputHandler : MonoBehaviour
{
    [Header("Marker Settings")]
    public GameObject markerPrefab;      // カスタムマーカープリファブ
    public Material markerMaterial;      // マーカー用マテリアル
    public float markerSize = 1.0f;      // マーカーサイズ
}
```

### パーセルエディター統合

```csharp
// ParcelEditorManagerとの連携
if (IsParcelEditorPointSelectionMode())
{
    parcelEditorManager?.OnPointSelected(position);
    return;
}
```

## パフォーマンス最適化

- マーカー位置は0.1f以上移動した場合のみ更新
- Escキーによるキャンセル処理
- 不要なホバー処理のスキップ

## 今後の拡張

- カスタムマーカーアニメーション
- 地形編集機能の実装
- 建設可能地点の高度な判定
- 地形情報表示UIの改善 