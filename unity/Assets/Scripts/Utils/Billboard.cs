using UnityEngine;

/// <summary>
/// オブジェクトを常にカメラの方向に向けるコンポーネント。
/// アイコンやテキストなどに使用して、常にカメラの方を向くようにする。
/// </summary>
public class Billboard : MonoBehaviour
{
    private Camera mainCamera;

    private void Start()
    {
        CacheMainCamera();
    }

    private void CacheMainCamera()
    {
        mainCamera = Camera.main;
        if (mainCamera == null)
        {
            // Camera.main ではなく FindObjectsByType で検索する。
            // シーン内に Camera タグを持つオブジェクトが複数ある場合や、
            // 動的に生成される場合に備えたフォールバック。
            var cameras = Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude);
            foreach (var cam in cameras)
            {
                if (cam.CompareTag("MainCamera"))
                {
                    mainCamera = cam;
                    break;
                }
            }
        }
        if (mainCamera == null)
        {
            Debug.LogWarning("Billboard: メインカメラが見つかりません。");
        }
    }

    private void LateUpdate()
    {
        // Camera.main は毎フレームアクセスすると内部でハッシュテーブル検索が走るため、
        // Start 時にキャッシュした参照を使い回す。
        // カメラが動的に破棄・再生成された場合のみ再検索する。
        if (mainCamera == null)
        {
            CacheMainCamera();
            if (mainCamera == null) return;
        }

        // カメラの方向を向く
        transform.rotation = mainCamera.transform.rotation;
    }
}

