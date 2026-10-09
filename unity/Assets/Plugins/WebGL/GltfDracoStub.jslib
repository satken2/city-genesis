// GLTFUtility の Draco 展開(GLTFUtilityDracoLoader)が Web ビルドで静的リンクする関数。
// Draco のネイティブライブラリは同梱しておらず、exe でも呼んだ時点で DllNotFoundException になる。Web はリンク時に関数の実体が要るので、
// 失敗を返すだけの実装を置く。Draco 圧縮の glTF は exe と同じく例外で読み込みに失敗し、ModelManager は Resources のモデルを使う。
mergeInto(LibraryManager.library, {
  DecodeDracoMeshStep1: function () { return 0; },
  DecodeDracoMeshStep2: function () { return 0; },
  GetAttribute: function () { return 0; },
  GetAttributeByType: function () { return 0; },
  GetAttributeByUniqueId: function () { return 0; },
  GetMeshIndices: function () { return 0; },
  GetAttributeData: function () { return 0; },
  ReleaseDracoMesh: function () {},
  ReleaseDracoAttribute: function () {},
  ReleaseDracoData: function () {},
});
