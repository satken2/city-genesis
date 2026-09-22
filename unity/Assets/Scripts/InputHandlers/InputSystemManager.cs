
using UnityEngine;

/// <summary>
/// InputHandlerシステムの初期化と管理を行うマネージャークラス
/// </summary>
public class InputSystemManager : MonoBehaviour
{
    public static InputSystemManager Instance { get; private set; }

    [Header("References")]
    public InputHandler inputHandler;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        // InputHandlerの自動検索
        if (inputHandler == null)
        {
            inputHandler = FindAnyObjectByType<InputHandler>();
            if (inputHandler == null)
            {
                Debug.LogError("InputSystemManager: InputHandlerが見つかりません。シーンにInputHandlerを配置してください。");
                return;
            }
        }

        // クリックイベントのコールバックを設定
        inputHandler.SetClickEvent(OnObjectClicked);
        
        // ホバーイベントのコールバックを設定
        inputHandler.SetHoverEvent(OnObjectHovered);
    }

    private void OnObjectClicked(int objectType, Vector3 position)
    {
        // 必要に応じて、ここでグローバルな処理を追加
        // 例：効果音の再生、ログの記録など
    }

    private void OnObjectHovered(int objectType, Vector3 position, GameObject hoveredObject)
    {
        // ホバーはフレーム毎に呼ばれるので、ログは出力しない
        // Debug.Log($"InputSystem: {InputObjectType.GetName(objectType)} がホバーされました at {position}");

        // 必要に応じて、ここでグローバルなホバー処理を追加
        // 例：カーソル変更、UI更新など
    }

    #region Public Methods

    public void SetInputMode(int objectType, int mode)
    {
        if (inputHandler != null)
        {
            inputHandler.SetMode(objectType, mode);
        }
    }

    public void SetModePreset(int preset)
    {
        if (inputHandler != null)
        {
            inputHandler.SetModePreset(preset);
        }
    }

    public void SetBuildingMode(int mode)
    {
        SetInputMode(InputObjectType.BUILDING, mode);
    }

    #endregion
} 