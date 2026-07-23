using UnityEngine;
using TMPro;
using System.Collections.Generic;

public class PathSelectionPanel : MonoBehaviour
{
    [SerializeField] private TMP_Dropdown pathVariantDropdown;

    // 線路数（複線プレビュー用）のプルダウン。1〜4本から選ぶ。
    // シーンで未割当の場合は、pathVariantDropdown を複製して実行時に自動生成する
    // （テスト用シーンなので、シーン側のUI配置に依存せずコードだけで完結させる）。
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
    /// プレビューで生成する線路数（本数）。1=単線、2=複線、以降は複々線相当。
    /// 直線・曲線・高架・地下トンネルの各サンプルで、選択本数ぶんの平行なパスを敷く。
    /// 線路バリアントでは近接線路が結合描画（複線見え化）される。
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
        // 線路数プルダウンは master data に依存しないが、パネル初期化の共通入口である
        // PopulateFromMasterData でまとめて用意する。
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
    /// 線路数プルダウンを用意する。シーンで割り当てられていなければ、
    /// バリアント選択プルダウンを複製して直下に配置する。
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
                // バリアント選択プルダウンのすぐ下に置く。
                cloneRt.anchoredPosition = srcRt.anchoredPosition + new Vector2(0f, -36f);
            }

            lineCountDropdown = clone.GetComponent<TMP_Dropdown>();
            // 複製元に付いている可能性のあるリスナーを引き継がないようにする。
            lineCountDropdown.onValueChanged.RemoveAllListeners();
        }

        if (lineCountDropdown == null) return;

        lineCountDropdown.ClearOptions();
        lineCountDropdown.AddOptions(new List<string> { "1", "2", "3", "4" });
        lineCountDropdown.value = 0;
        lineCountDropdown.RefreshShownValue();
    }
}
