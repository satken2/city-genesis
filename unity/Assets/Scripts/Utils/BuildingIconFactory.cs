using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 建物タイプのアイコンを取得するためのファクトリークラス。
/// </summary>
public class BuildingIconFactory : MonoBehaviour
{
    // シングルトンインスタンス
    public static BuildingIconFactory Instance { get; private set; }

    [Header("Building Type Icons")]
    [SerializeField] private Sprite apartmentIcon;
    [SerializeField] private Sprite houseIcon;
    [SerializeField] private Sprite retailIcon;
    [SerializeField] private Sprite factoryIcon;
    [SerializeField] private Sprite officeIcon;
    [SerializeField] private Sprite farmIcon;
    [SerializeField] private Sprite mineIcon;
    [SerializeField] private Sprite portIcon;
    [SerializeField] private Sprite otherIcon;
    [SerializeField] private Sprite slumIcon;

    // 建物タイプごとのアイコン管理用Dictionary
    private Dictionary<int, Sprite> buildingTypeIcons;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        InitializeBuildingTypeIcons();
    }

    /// <summary>
    /// 建物タイプごとのアイコンをDictionaryに登録
    /// </summary>
    private void InitializeBuildingTypeIcons()
    {
        buildingTypeIcons = new Dictionary<int, Sprite>();

        try
        {
            buildingTypeIcons[Constants.BUILDING_APARTMENT] = apartmentIcon;
            buildingTypeIcons[Constants.BUILDING_HOUSE] = houseIcon;
            buildingTypeIcons[Constants.BUILDING_RETAIL] = retailIcon;
            buildingTypeIcons[Constants.BUILDING_FACTORY] = factoryIcon;
            buildingTypeIcons[Constants.BUILDING_OFFICE] = officeIcon;
            buildingTypeIcons[Constants.BUILDING_FARM] = farmIcon;
            buildingTypeIcons[Constants.BUILDING_MINE] = mineIcon;
            buildingTypeIcons[Constants.BUILDING_PORT] = portIcon;
            buildingTypeIcons[Constants.BUILDING_OTHER] = otherIcon;
            buildingTypeIcons[Constants.BUILDING_PUBLIC] = slumIcon;
        }
        catch (Exception e)
        {
            Debug.LogError($"BuildingIconFactory: アイコンの登録に失敗しました: {e.Message}");
        }
    }

    /// <summary>
    /// 建物タイプIDからアイコンを取得する。
    /// </summary>
    /// <param name="buildingTypeId">建物タイプID</param>
    /// <returns>対応するSprite。見つからない場合はnull</returns>
    public Sprite GetIcon(int buildingTypeId)
    {
        if (buildingTypeIcons != null && buildingTypeIcons.ContainsKey(buildingTypeId))
        {
            return buildingTypeIcons[buildingTypeId];
        }
        return null;
    }

}

