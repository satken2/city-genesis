
using UnityEngine;

/// <summary>
/// Manager for InputHandler system initialization and management
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
        // Automatically find InputHandler.
        if (inputHandler == null)
        {
            inputHandler = FindAnyObjectByType<InputHandler>();
            if (inputHandler == null)
            {
                Debug.LogError("InputSystemManager: InputHandlerが見つかりません。シーンにInputHandlerを配置してください。");
                return;
            }
        }

        // Set the click event callback.
        inputHandler.SetClickEvent(OnObjectClicked);
        
        // Set the hover event callback.
        inputHandler.SetHoverEvent(OnObjectHovered);
    }

    private void OnObjectClicked(int objectType, Vector3 position)
    {
        // Add global processing here if needed.
        // Examples: sound effects, logging, etc.
    }

    private void OnObjectHovered(int objectType, Vector3 position, GameObject hoveredObject)
    {
        // Hover runs every frame, so do not log it.
        // Debug.Log($"InputSystem: {InputObjectType.GetName(objectType)} がホバーされました at {position}");

        // Add global hover processing here if needed.
        // Examples: cursor changes, UI updates, etc.
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