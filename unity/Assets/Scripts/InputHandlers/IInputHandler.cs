
using UnityEngine;

/// <summary>
/// すべての入力ハンドラーが実装するインターフェース。
/// オブジェクトタイプごとのswitch分岐をなくし、ポリモーフィックにディスパッチするために使用。
/// </summary>
public interface IInputHandler
{
    /// <summary>InputObjectType 定数 (BUILDING=1, PARCEL=2, PATH=3, NODE=4, TERRAIN=5, ZONE=6)</summary>
    int ObjectTypeId { get; }

    void onClick(int mode);
    void onHoverStart(int mode);
    void onHoverEnd(int mode);
    void onHover(int mode);
    void onRightClick(int mode);
}
