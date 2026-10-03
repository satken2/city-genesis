using UnityEngine;

/// <summary>パスの表示区間の分類。地上と地下の材質の扱いだけを区別し、生成・更新・寿命は親パスに従う。</summary>
public sealed class PathSection : MonoBehaviour
{
    public bool IsUnderground;
}
