using UnityEngine;
using TMPro;
using System.Collections.Generic;

public class PathSelectionPanel : MonoBehaviour
{
    [SerializeField] private TMP_Dropdown pathVariantDropdown;

    // Line-count dropdown for double-track previews, selecting 1–4.
    // If unassigned in scene, clone pathVariantDropdown at runtime
    // (self-contained test-scene code independent of scene UI layout).
    [SerializeField] private TMP_Dropdown lineCountDropdown;

    public int SelectedPathVariant
    {
        get
        {
            if (pathVariantDropdown == null || pathVariantDropdown.options.Count == 0) return 1;
            int idx = pathVariantDropdown.value;
            if (idx < 0 || idx >= pathVariantDropdown.options.Count) return 1;
            return int.TryParse(pathVariantDropdown.options[idx].text, out int v) ? v : 1;
        }
    }

    /// <summary>
    /// Preview line count: 1=single, 2=double, higher=quadruple-track equivalents.
    /// Straight/curved/elevated/underground samples place this many parallel paths.
    /// Track variants join nearby lines for continuous double-track appearance.
    /// </summary>
    public int SelectedLineCount
    {
        get
        {
            if (lineCountDropdown == null || lineCountDropdown.options.Count == 0) return 1;
            int idx = lineCountDropdown.value;
            if (idx < 0 || idx >= lineCountDropdown.options.Count) return 1;
            return int.TryParse(lineCountDropdown.options[idx].text, out int v) ? Mathf.Clamp(v, 1, 4) : 1;
        }
    }

    public void PopulateFromMasterData()
    {
        // Though independent of master data, prepare line-count dropdown in the shared
        // panel initialization entry point PopulateFromMasterData.
        EnsureLineCountDropdown();

        if (pathVariantDropdown == null)
        {
            Debug.LogWarning("[PathPanel] pathVariantDropdown が設定されていません。");
            return;
        }

        pathVariantDropdown.ClearOptions();
        var options = new List<string>();

        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            var dict = MasterDataStore.Instance.GetPathMasterDictionary();
            foreach (var kvp in dict)
            {
                options.Add(kvp.Key.ToString());
            }
            Debug.Log($"[PathPanel] マスターデータから {options.Count} 件の道路バリアントを取得しました。");
        }
        else
        {
            Debug.LogWarning("[PathPanel] MasterDataStore が未初期化です。フォールバック値を使用します。");
        }

        if (options.Count == 0)
        {
            options.Add("1");
        }

        pathVariantDropdown.AddOptions(options);
    }

    /// <summary>
    /// Prepare line-count dropdown; if unassigned, clone variant selector
    /// and place immediately below it.
    /// </summary>
    private void EnsureLineCountDropdown()
    {
        if (lineCountDropdown == null && pathVariantDropdown != null)
        {
            GameObject clone = Instantiate(pathVariantDropdown.gameObject, pathVariantDropdown.transform.parent);
            clone.name = "LineCountDropdown";

            RectTransform srcRt = pathVariantDropdown.GetComponent<RectTransform>();
            RectTransform cloneRt = clone.GetComponent<RectTransform>();
            if (srcRt != null && cloneRt != null)
            {
                // Place directly below variant dropdown.
                cloneRt.anchoredPosition = srcRt.anchoredPosition + new Vector2(0f, -36f);
            }

            lineCountDropdown = clone.GetComponent<TMP_Dropdown>();
            // Do not inherit listeners possibly attached to the original.
            lineCountDropdown.onValueChanged.RemoveAllListeners();
        }

        if (lineCountDropdown == null) return;

        lineCountDropdown.ClearOptions();
        lineCountDropdown.AddOptions(new List<string> { "1", "2", "3", "4" });
        lineCountDropdown.value = 0;
        lineCountDropdown.RefreshShownValue();
    }
}
