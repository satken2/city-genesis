using UnityEngine;

/// <summary>パスの表示区間の分類。地上・地下の表示と材質に使い、生成・更新・寿命は親パスに従う。</summary>
public sealed class PathSection : MonoBehaviour
{
    public bool IsUnderground;
}
