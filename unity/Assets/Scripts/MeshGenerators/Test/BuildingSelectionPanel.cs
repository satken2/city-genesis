using UnityEngine;
using TMPro;

public class BuildingSelectionPanel : MonoBehaviour
{
    [SerializeField] private TMP_Dropdown agentVariantDropdown;
    [SerializeField] private TMP_InputField floorsInput;
    [SerializeField] private TMP_InputField visualVariantInput;
    [SerializeField] private TMP_InputField visualVariant2Input;
    [SerializeField] private TMP_InputField visualVariant3Input;
    [SerializeField] private TMP_InputField widthInput;
    [SerializeField] private TMP_InputField heightInput;
    [SerializeField] private TMP_Dropdown shapeDropdown;

    public int SelectedAgentVariant
    {
        get
        {
            if (agentVariantDropdown == null || agentVariantDropdown.options.Count == 0) return 0;
            int idx = agentVariantDropdown.value;
            if (idx < 0 || idx >= agentVariantDropdown.options.Count) return 0;
            return int.TryParse(agentVariantDropdown.options[idx].text, out int v) ? v : 0;
        }
    }

    public int Floors
    {
        get
        {
            if (floorsInput == null) return 5;
            return int.TryParse(floorsInput.text, out int f) ? Mathf.Max(1, f) : 5;
        }
    }

    public float VisualVariant
    {
        get
        {
            if (visualVariantInput == null) return 0.5f;
            return float.TryParse(visualVariantInput.text, out float v) ? Mathf.Clamp01(v) : 0.5f;
        }
    }

    public float VisualVariant2
    {
        get
        {
            if (visualVariant2Input == null) return 0.5f;
            return float.TryParse(visualVariant2Input.text, out float v) ? Mathf.Clamp01(v) : 0.5f;
        }
    }

    public float VisualVariant3
    {
        get
        {
            if (visualVariant3Input == null) return 0f;
            return float.TryParse(visualVariant3Input.text, out float v) ? Mathf.Clamp01(v) : 0f;
        }
    }

    public float Width
    {
        get
        {
            if (widthInput == null) return 25f;
            return float.TryParse(widthInput.text, out float v) ? Mathf.Max(0.1f, v) : 25f;
        }
    }

    public float Height
    {
        get
        {
            if (heightInput == null) return 25f;
            return float.TryParse(heightInput.text, out float v) ? Mathf.Max(0.1f, v) : 25f;
        }
    }

    private void Awake()
    {
        // プレビュー表示で使用する底面形状の選択肢をプルダウンに動的設定します。
        // インスペクター上のデフォルト状態に依存せず、常に正しい形状リスト（四角形、三角形、台形、五角形）を保証します。
        if (shapeDropdown != null)
        {
            shapeDropdown.ClearOptions();
            shapeDropdown.AddOptions(new System.Collections.Generic.List<string>
            {
                "四角形",
                "三角形",
                "台形",
                "五角形"
            });
        }
    }

    /// <summary>
    /// プレビュー表示する建物の底面形状のタイプ番号を取得します。
    /// 0: 四角形（矩形）、1: 三角形、2: 台形、3: 五角形
    /// </summary>
    public int ShapeType
    {
        get
        {
            if (shapeDropdown == null || shapeDropdown.options.Count == 0) return 0;
            return shapeDropdown.value;
        }
    }

    public void SetDefaults()
    {
        if (visualVariantInput != null && string.IsNullOrEmpty(visualVariantInput.text))
            visualVariantInput.text = "0.5";
        if (visualVariant2Input != null && string.IsNullOrEmpty(visualVariant2Input.text))
            visualVariant2Input.text = "0.5";
        if (visualVariant3Input != null && string.IsNullOrEmpty(visualVariant3Input.text))
            visualVariant3Input.text = "0.0";
        if (floorsInput != null && string.IsNullOrEmpty(floorsInput.text))
            floorsInput.text = "5";
        if (widthInput != null && string.IsNullOrEmpty(widthInput.text))
            widthInput.text = "25";
        if (heightInput != null && string.IsNullOrEmpty(heightInput.text))
            heightInput.text = "25";
    }

    public void PopulateFromMasterData()
    {
        if (agentVariantDropdown == null)
        {
            Debug.LogWarning("[BuildingPanel] agentVariantDropdown が設定されていません。");
            return;
        }

        agentVariantDropdown.ClearOptions();
        var options = new System.Collections.Generic.List<string>();

        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            var dict = MasterDataStore.Instance.GetAgentMasterDictionary();
            foreach (var kvp in dict)
            {
                if (kvp.Value.building == true)
                {
                    options.Add(kvp.Key.ToString());
                }
            }
            Debug.Log($"[BuildingPanel] マスターデータから {options.Count} 件の建物エージェントを取得しました。");
        }
        else
        {
            Debug.LogWarning("[BuildingPanel] MasterDataStore が未初期化です。フォールバック値を使用します。");
        }

        if (options.Count == 0)
        {
            options.Add("16");
            options.Add("32");
        }

        agentVariantDropdown.AddOptions(options);
    }
}
