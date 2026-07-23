using UnityEngine;

/// <summary>
/// パーセル用のビジュアルモード適用。
/// エラーハイライト時以外は3D MeshRendererを有効化せず、
/// データ可視化の塗りつぶしは ParcelManager の OverlayMeshRenderer で一括描画する。
/// </summary>
public class VisualParcelObject : MonoBehaviour, IVisualModeHandler
{
    private MeshRenderer rendererComp;

    private VisualParcelStats stats;
    private bool isErrorHighlighted = false;

    private void Awake()
    {
        rendererComp = GetComponent<MeshRenderer>();
    }

    private void Start()
    {
        RegisterWithVisualModeHandler();
    }

    /// <summary>
    /// VisualModeHandler へ自オブジェクトを登録する。
    /// Instance が未生成の場合はコルーチンで待機してから再試行する。
    /// </summary>
    private void RegisterWithVisualModeHandler()
    {
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.RegisterObject(InputObjectType.PARCEL, this);
            ApplyVisualMode(VisualModeHandler.Instance.GetMode(InputObjectType.PARCEL));
        }
        else
        {
            StartCoroutine(WaitAndRegisterVisualModeHandler());
        }
    }

    private System.Collections.IEnumerator WaitAndRegisterVisualModeHandler()
    {
        while (VisualModeHandler.Instance == null)
        {
            yield return null;
        }
        VisualModeHandler.Instance.RegisterObject(InputObjectType.PARCEL, this);
        ApplyVisualMode(VisualModeHandler.Instance.GetMode(InputObjectType.PARCEL));
    }

    private void OnDestroy()
    {
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.UnregisterObject(InputObjectType.PARCEL, this);
        }
    }

    /// <summary>
    /// VisualParcelStats を返す。ParcelManager のデータ可視化ループ内で使用する。
    /// 初回アクセス時に GetComponent で取得してキャッシュする。
    /// </summary>
    public VisualParcelStats Stats
    {
        get
        {
            if (stats == null) stats = GetComponent<VisualParcelStats>();
            return stats;
        }
    }

    /// <summary>
    /// ビジュアルモードを適用する。
    /// エラーハイライト時のみ3D MeshRenderer を有効化し、
    /// それ以外のモードでは常に MeshRenderer を無効にする。
    /// データ可視化の塗りつぶし描画は ParcelManager のオーバーレイで一括処理される。
    /// </summary>
    public void ApplyVisualMode(int mode)
    {
        if (rendererComp == null) return;
        if (stats == null) stats = GetComponent<VisualParcelStats>();

        if (isErrorHighlighted)
        {
            rendererComp.enabled = true;
            rendererComp.material.color = Color.red;
            return;
        }

        // エラーハイライト以外では MeshRenderer を常に無効にする。
        // 境界線やデータ可視化の塗りつぶしはすべて
        // ParcelManager の OverlayMeshRenderer で描画する。
        rendererComp.enabled = false;
    }
}


