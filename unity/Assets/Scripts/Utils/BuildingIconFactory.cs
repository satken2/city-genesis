using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Factory for retrieving building-type icons.
/// </summary>
public class BuildingIconFactory : MonoBehaviour
{
    // Singleton instance.
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

    // Dictionary of icons by building type.
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
    /// Register building-type icons in the Dictionary.
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
    /// Get an icon by building type ID.
    /// </summary>
    /// <param name="buildingTypeId">Building type ID.</param>
    /// <returns>The corresponding Sprite, or null if not found.</returns>
    public Sprite GetIcon(int buildingTypeId)
    {
        if (buildingTypeIcons != null && buildingTypeIcons.ContainsKey(buildingTypeId))
        {
            return buildingTypeIcons[buildingTypeId];
        }
        return null;
    }

}

