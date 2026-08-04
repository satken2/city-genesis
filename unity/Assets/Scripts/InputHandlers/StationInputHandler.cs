using UnityEngine;

/// <summary>
/// 駅舎オブジェクト用のInputHandler。
///
/// 駅舎は StationManager が生成する駅(Station)の建屋オブジェクトで、通常モードのクリックで
/// 駅詳細パネル(station_detail)を開く。駅は駅パス(ホーム)の敷設で自動作成され、所属ホームの
/// 全喪失で自動消滅する概念のため、ブルドーザ等の編集モードでは一切反応しない
/// (モードプリセット側で MODE_NONE に設定される)。
/// </summary>
public class StationInputHandler : MonoBehaviour, IInputHandler
{
    public int stationId;

    public int ObjectTypeId => InputObjectType.STATION;

    public void onClick(int mode)
    {
        switch (mode)
        {
            case InputMode.MODE_NORMAL:
                HandleNormalClick();
                break;
            default:
                // 編集系モードでは何もしない(駅舎は表示専用オブジェクト)。
                break;
        }
    }

    public void onRightClick(int mode)
    {
        // 駅舎に右クリック操作はない。
    }

    public void onHoverStart(int mode)
    {
        // 駅舎はVisualMode(ヒートマップ)の対象外のためハイライト処理もない。
    }

    public void onHover(int mode)
    {
    }

    public void onHoverEnd(int mode)
    {
    }

    private void HandleNormalClick()
    {
        // 駅舎クリックで駅詳細ウィンドウを開く。ウィンドウの解決はTauri側の
        // window-registry.json(station_detail)が行うため、Unityはエンティティ参照を送るだけでよい。
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("station_detail", "station", stationId.ToString());
        }
    }
}
