using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

public class MasterDataStore : MonoBehaviour
{
	public static MasterDataStore Instance { get; private set; }

	public event Action OnInitialized;

	[Serializable]
	public class AgentMasterItem
	{
		public string display_name;
		public string description;
		public string name;
		public int? type;
		public bool? building;
		public int? space_type;
		public Dictionary<string, float> space_limits = new();
		
		public int? bd_type;
		// Allowed parcel types (PARCEL_*). Unset values use bd_type defaults: coastal for ports, land for others.
		public int? parcel_type;
		public int? building_shape;
		public int? model_type;
		public int? max_floors;
		public int? min_floors;
		public float? height_per_floor;
		// Initial dimensions and fixed-size settings for direct placement. The panel and 3D placement preview use the same master values.
		public float? default_width;
		public float? default_height;
		public bool? resizable;
		public bool? shared_building;
		// Whether to render a ground panel, a horizontal slab covering the entire ground area. Unset (null) means disabled; opt in explicitly.
		// Resolve floor-part materials through a{variant} -> b{bd_type} -> common when rendering.
		public bool? draw_floor;
		public int? supply;
		public float? employer_skills;
		public float? max_skill;
		public float? skilled_people;
		public float? skill_multiplier;
		public float? space_need;
		public float? space_min;
		public float? space_max;
		public float? labour_need;
		public float? labour_min;
		public float? labour_max;
		public Dictionary<int, float> materials = new();
		public float? cost_ratio;
		public float? min_space;
		public float? max_space;
	}

	[Serializable]
	public class ProductItem
	{
		public string display_name;
		public string name;
		public string unit;
		public int? type;
		public int? agent_master;
		public Dictionary<int, float> materials = new Dictionary<int, float>();
		// Add space and labour when needed.
	}

	[Serializable]
	public class ProductsResponse
	{
		public Dictionary<string, ProductItem> products;
	}

	[Serializable]
	public class PathsResponse
	{
		public Dictionary<int, PathMasterItem> paths;
	}

	/// <summary>
	/// One type from the public transport master (transit_master).
	/// For each line type, such as bus or metro, define the path variant used for section paths
	/// (transit_path_id), the underlying path variants allowing stops (stop_path_variants),
	/// and the underlying path variants vehicles can traverse (route_path_variants).
	/// Used to highlight allowed paths and constrain input in stop-placement mode.
	/// </summary>
	[Serializable]
	public class TransitMasterItem
	{
		public string display_name;
		public string description;
		public int transit_path_id;
		public List<int> stop_path_variants = new();
		public List<int> route_path_variants = new();
		// Product ID of vehicle goods procured from depots. Used to look up running networks of depots supplying that product.
		public int? vehicle;
	}

	[Serializable]
	public class TransitResponse
	{
		public Dictionary<int, TransitMasterItem> transit;
	}

	[Serializable]
	public class PathTypesResponse
	{
		public Dictionary<int, PathTypeItem> path_types;
	}

	[Serializable]
	public class AgentsResponse
	{
		public Dictionary<int, AgentMasterItem> agents;
	}

	/// <summary>
	/// One sub-building definition from subbuilding_master.
	/// Defines an auxiliary building belonging to a mashup building (MODEL_MASHUP) facility variant.
	/// In the game, the backend handles placement planning and model_prefix resolution. The main renderer only looks up occupied cells (width x height) for distant views;
	/// mesh test scenes use this to generate their own placement plans.
	/// </summary>
	[Serializable]
	public class SubBuildingMasterItem
	{
		public int agent_variant;
		public string display_name;
		public string model_prefix;
		public float? width;     // Footprint width in cells (one unit = MASHUP_CELL_SIZE m; defaults to 1 cell).
		public float? height;    // Footprint depth in the same units.
		public int? max_floors;
		public int? min_floors;
		public float? height_per_floor;
		public float? weight;    // Building-count ratio for generated placements (default 1.0; 0 excludes it from proportional filling).
		public int? min_count;   // Minimum placement count (default 0).
		public int? max_count;   // Maximum placement count (unlimited if unset). The first building, with the lowest ID, forms the facility's front.
	}

	[Serializable]
	public class SubBuildingsResponse
	{
		public Dictionary<int, SubBuildingMasterItem> subbuildings;
	}

	/// <summary>
	/// One landmark definition from landmark_master.
	/// All landmarks share agent variant 1070; their IDs select the specific landmark.
	/// Unity prioritizes model_prefix (resolving landmark-{model_prefix} part folders),
	/// width / depth / floors (direct-placement ghost dimensions and floor count), and height_per_floor
	/// over agent_master. The backend enforces these values, so Unity must start with the same values
	/// to keep previews aligned with confirmed placements. cost_multiplier is UI-only.
	/// </summary>
	[Serializable]
	public class LandmarkMasterItem
	{
		public string display_name;
		public string description;
		public int? agent_variant;
		public string model_prefix;
		public float? width;             // Fixed site width (m).
		public float? depth;             // Fixed site depth (m).
		public int? floors;
		public float? height_per_floor;
		public float? cost_multiplier;   // Construction cost multiplier relative to an ordinary building with the same total floor area.
	}

	[Serializable]
	public class LandmarksResponse
	{
		public Dictionary<int, LandmarkMasterItem> landmarks;
	}

	[Serializable]
	public class BuildingTypesResponse
	{
		public Dictionary<int, BuildingTypeItem> bd_types;
	}

	[Serializable]
	public class ParcelTypesResponse
	{
		public Dictionary<int, ParcelTypeItem> parcel_types;
	}

	[Serializable]
	public class AgentTypesResponse
	{
		public Dictionary<int, AgentTypeItem> agent_types;
	}

	[Serializable]
	public class PathTypeItem
	{
		public string display_name;
	}

	[Serializable]
	public class BuildingTypeItem
	{
		public string display_name;
	}

	[Serializable]
	public class ParcelTypeItem
	{
		public string display_name;
	}

	[Serializable]
	public class AgentTypeItem
	{
		public string display_name;
		public string description;
	}

	// Use the public types in PathManager.cs for Path master data.

	public bool IsInitialized { get; private set; } = false;

	private Dictionary<string, ProductItem> productMasterByKey = new Dictionary<string, ProductItem>();
	private Dictionary<int, AgentMasterItem> agentMasterById = new Dictionary<int, AgentMasterItem>();
	private Dictionary<int, SubBuildingMasterItem> subBuildingMasterById = new Dictionary<int, SubBuildingMasterItem>();
	private Dictionary<int, LandmarkMasterItem> landmarkMasterById = new Dictionary<int, LandmarkMasterItem>();
	private Dictionary<int, PathMasterItem> pathMasterById = new Dictionary<int, PathMasterItem>();
	private Dictionary<int, TransitMasterItem> transitMasterById = new Dictionary<int, TransitMasterItem>();
	private Dictionary<int, PathTypeItem> pathTypesById = new Dictionary<int, PathTypeItem>();
	private Dictionary<int, BuildingTypeItem> buildingTypesById = new Dictionary<int, BuildingTypeItem>();
	private Dictionary<int, ParcelTypeItem> parcelTypesById = new Dictionary<int, ParcelTypeItem>();
	private Dictionary<int, AgentTypeItem> agentTypesById = new Dictionary<int, AgentTypeItem>();

	private void Awake()
	{
		if (Instance != null && Instance != this)
		{
			Destroy(gameObject);
			return;
		}
		Instance = this;
		DontDestroyOnLoad(gameObject);
	}

	/// <summary>
	/// Fetch master data. It can vary by city, so use forceReload when switching cities.
	/// Keep IsInitialized true while reloading so previous values remain usable; raise OnInitialized only on the first fetch. Callers handle redrawing.
	/// </summary>
	public IEnumerator Initialize(bool forceReload = false)
	{
		if (IsInitialized && !forceReload) yield break;
		bool firstLoad = !IsInitialized;

		// Retry until all master-data requests succeed.
		// Master APIs may not respond immediately after backend startup
		// or Unity's automatic restart after a crash. A previous bug set IsInitialized = true
		// even on failure, allowing execution with empty master data.
		while (true)
		{
			bool allSucceeded = false;
			yield return InitializeAttempt(ok => allSucceeded = ok);
			if (allSucceeded) break;

			Debug.LogWarning("MasterDataStore: マスターデータの取得に失敗しました。2秒後に再試行します");
			yield return new WaitForSeconds(2f);
		}

		IsInitialized = true;
		Debug.Log($"MasterDataStore initialize end");
		if (firstLoad) OnInitialized?.Invoke();
	}

	/// <summary>
	/// Attempt to fetch all master data once. Call onComplete(true) only when all requests succeed.
	/// Parsing failures indicate a schema mismatch and cannot be resolved by retrying,
	/// so success is judged at the network level.
	/// </summary>
	private IEnumerator InitializeAttempt(Action<bool> onComplete)
	{
		// Start requests in parallel.
		var productsCall = ApiClient.Request("GET", "/master/products").Begin();
		var pathsCall = ApiClient.Request("GET", "/master/paths").Begin();
		var transitCall = ApiClient.Request("GET", "/master/transit").Begin();
		var pathTypesCall = ApiClient.Request("GET", "/master/path_types").Begin();
		var agentsCall = ApiClient.Request("GET", "/master/agents").Begin();
		var subBuildingsCall = ApiClient.Request("GET", "/master/subbuildings").Begin();
		var landmarksCall = ApiClient.Request("GET", "/master/landmarks").Begin();
		var buildingTypesCall = ApiClient.Request("GET", "/master/bd_types").Begin();
		var parcelTypesCall = ApiClient.Request("GET", "/master/parcel_types").Begin();
		var agentTypesCall = ApiClient.Request("GET", "/master/agent_types").Begin();
		var calls = new[] { productsCall, pathsCall, transitCall, pathTypesCall, agentsCall, subBuildingsCall, landmarksCall, buildingTypesCall, parcelTypesCall, agentTypesCall };

		// Wait for all requests to complete.
		foreach (var call in calls) yield return call.Send();

		var productsReq = productsCall.Web;
		var pathsReq = pathsCall.Web;
		var transitReq = transitCall.Web;
		var pathTypesReq = pathTypesCall.Web;
		var agentsReq = agentsCall.Web;
		var subBuildingsReq = subBuildingsCall.Web;
		var landmarksReq = landmarksCall.Web;
		var buildingTypesReq = buildingTypesCall.Web;
		var parcelTypesReq = parcelTypesCall.Web;
		var agentTypesReq = agentTypesCall.Web;

		// Products
		if (productsReq.result == UnityWebRequest.Result.Success)
		{
			try
			{
				var resp = JsonConvert.DeserializeObject<ProductsResponse>(productsReq.downloadHandler.text);
				productMasterByKey = resp?.products ?? new Dictionary<string, ProductItem>();
				Debug.Log($"MasterDataStore: Product master loaded ({productMasterByKey.Count})");
			}
			catch (Exception e)
			{
				Debug.LogError($"MasterDataStore: Failed to parse products master: {e.Message}");
			}
		}
		else
		{
			Debug.LogError($"MasterDataStore: Failed to load products master: {productsReq.error}");
		}

		// Paths
		if (pathsReq.result == UnityWebRequest.Result.Success)
		{
			try
			{
				var resp = JsonConvert.DeserializeObject<PathsResponse>(pathsReq.downloadHandler.text);
				pathMasterById = resp?.paths ?? new Dictionary<int, PathMasterItem>();
				Debug.Log($"MasterDataStore: Path master loaded ({pathMasterById.Count}) Keys: {string.Join(",", pathMasterById.Keys)}");
			}
			catch (Exception e)
			{
				Debug.LogError($"MasterDataStore: Failed to parse paths master: {e.Message}");
			}
		}
		else
		{
			Debug.LogError($"MasterDataStore: Failed to load paths master: {pathsReq.error}");
		}

		// Transit (public transport master).
		if (transitReq.result == UnityWebRequest.Result.Success)
		{
			try
			{
				var resp = JsonConvert.DeserializeObject<TransitResponse>(transitReq.downloadHandler.text);
				transitMasterById = resp?.transit ?? new Dictionary<int, TransitMasterItem>();
				Debug.Log($"MasterDataStore: Transit master loaded ({transitMasterById.Count})");
			}
			catch (Exception e)
			{
				Debug.LogError($"MasterDataStore: Failed to parse transit master: {e.Message}");
			}
		}
		else
		{
			Debug.LogError($"MasterDataStore: Failed to load transit master: {transitReq.error}");
		}

		// Path types
		if (pathTypesReq.result == UnityWebRequest.Result.Success)
		{
			try
			{
				var resp = JsonConvert.DeserializeObject<PathTypesResponse>(pathTypesReq.downloadHandler.text);
				pathTypesById = resp?.path_types ?? new Dictionary<int, PathTypeItem>();
				Debug.Log($"MasterDataStore: Path type master loaded ({pathTypesById.Count})");
			}
			catch (Exception e)
			{
				Debug.LogError($"MasterDataStore: Failed to parse path types master: {e.Message}");
			}
		}
		else
		{
			Debug.LogError($"MasterDataStore: Failed to load path types master: {pathTypesReq.error}");
		}

		// Agents
		if (agentsReq.result == UnityWebRequest.Result.Success)
		{
			try
			{
				var agentsResp = JsonConvert.DeserializeObject<AgentsResponse>(agentsReq.downloadHandler.text);
				agentMasterById = agentsResp?.agents ?? new Dictionary<int, AgentMasterItem>();
				Debug.Log($"MasterDataStore: Agent master loaded ({agentMasterById.Count})");
			}
			catch (Exception e)
			{
				Debug.LogError($"MasterDataStore: Failed to parse agents master: {e.Message}");
			}
		}
		else
		{
			Debug.LogError($"MasterDataStore: Failed to load agents master: {agentsReq.error}");
		}

		// Sub buildings (sub-building master).
		if (subBuildingsReq.result == UnityWebRequest.Result.Success)
		{
			try
			{
				var resp = JsonConvert.DeserializeObject<SubBuildingsResponse>(subBuildingsReq.downloadHandler.text);
				subBuildingMasterById = resp?.subbuildings ?? new Dictionary<int, SubBuildingMasterItem>();
				Debug.Log($"MasterDataStore: SubBuilding master loaded ({subBuildingMasterById.Count})");
			}
			catch (Exception e)
			{
				Debug.LogError($"MasterDataStore: Failed to parse subbuildings master: {e.Message}");
			}
		}
		else
		{
			Debug.LogError($"MasterDataStore: Failed to load subbuildings master: {subBuildingsReq.error}");
		}

		// Landmarks (landmark master).
		if (landmarksReq.result == UnityWebRequest.Result.Success)
		{
			try
			{
				var resp = JsonConvert.DeserializeObject<LandmarksResponse>(landmarksReq.downloadHandler.text);
				landmarkMasterById = resp?.landmarks ?? new Dictionary<int, LandmarkMasterItem>();
				Debug.Log($"MasterDataStore: Landmark master loaded ({landmarkMasterById.Count})");
			}
			catch (Exception e)
			{
				Debug.LogError($"MasterDataStore: Failed to parse landmarks master: {e.Message}");
			}
		}
		else
		{
			Debug.LogError($"MasterDataStore: Failed to load landmarks master: {landmarksReq.error}");
		}

		// Building types
		if (buildingTypesReq.result == UnityWebRequest.Result.Success)
		{
			try
			{
				var resp = JsonConvert.DeserializeObject<BuildingTypesResponse>(buildingTypesReq.downloadHandler.text);
				buildingTypesById = resp?.bd_types ?? new Dictionary<int, BuildingTypeItem>();
				Debug.Log($"MasterDataStore: Building type master loaded ({buildingTypesById.Count})");
			}
			catch (Exception e)
			{
				Debug.LogError($"MasterDataStore: Failed to parse building types master: {e.Message}");
			}
		}
		else
		{
			Debug.LogError($"MasterDataStore: Failed to load building types master: {buildingTypesReq.error}");
		}

		// Parcel types
		if (parcelTypesReq.result == UnityWebRequest.Result.Success)
		{
			try
			{
				var resp = JsonConvert.DeserializeObject<ParcelTypesResponse>(parcelTypesReq.downloadHandler.text);
				parcelTypesById = resp?.parcel_types ?? new Dictionary<int, ParcelTypeItem>();
				Debug.Log($"MasterDataStore: Parcel type master loaded ({parcelTypesById.Count})");
			}
			catch (Exception e)
			{
				Debug.LogError($"MasterDataStore: Failed to parse parcel types master: {e.Message}");
			}
		}
		else
		{
			Debug.LogError($"MasterDataStore: Failed to load parcel types master: {parcelTypesReq.error}");
		}

		// Agent types
		if (agentTypesReq.result == UnityWebRequest.Result.Success)
		{
			try
			{
				var resp = JsonConvert.DeserializeObject<AgentTypesResponse>(agentTypesReq.downloadHandler.text);
				agentTypesById = resp?.agent_types ?? new Dictionary<int, AgentTypeItem>();
				Debug.Log($"MasterDataStore: Agent type master loaded ({agentTypesById.Count})");
			}
			catch (Exception e)
			{
				Debug.LogError($"MasterDataStore: Failed to parse agent types master: {e.Message}");
			}
		}
		else
		{
			Debug.LogError($"MasterDataStore: Failed to load agent types master: {agentTypesReq.error}");
		}

		bool allSucceeded =
			productsReq.result == UnityWebRequest.Result.Success &&
			pathsReq.result == UnityWebRequest.Result.Success &&
			transitReq.result == UnityWebRequest.Result.Success &&
			pathTypesReq.result == UnityWebRequest.Result.Success &&
			agentsReq.result == UnityWebRequest.Result.Success &&
			subBuildingsReq.result == UnityWebRequest.Result.Success &&
			landmarksReq.result == UnityWebRequest.Result.Success &&
			buildingTypesReq.result == UnityWebRequest.Result.Success &&
			parcelTypesReq.result == UnityWebRequest.Result.Success &&
			agentTypesReq.result == UnityWebRequest.Result.Success;

		foreach (var call in calls) call.Dispose();
		onComplete(allSucceeded);
	}

	public AgentMasterItem GetAgentMaster(int agentVariant)
	{
		return agentMasterById[agentVariant];
	}

	public IReadOnlyDictionary<int, AgentMasterItem> GetAgentMasterDictionary()
	{
		return agentMasterById;
	}

	/// <summary>
	/// Return (subbuilding_id, definition) entries for the specified variant, or an empty list if undefined.
	/// Sort by ascending ID to match the backend's sheet-row order (MasterData.get_subbuilding_ids).
	/// An empty list means the facility is a single building without separate sub-buildings.
	/// </summary>
	public List<KeyValuePair<int, SubBuildingMasterItem>> GetSubBuildingsForVariant(int agentVariant)
	{
		var result = new List<KeyValuePair<int, SubBuildingMasterItem>>();
		foreach (var kvp in subBuildingMasterById)
		{
			if (kvp.Value != null && kvp.Value.agent_variant == agentVariant)
			{
				result.Add(kvp);
			}
		}
		result.Sort((a, b) => a.Key.CompareTo(b.Key));
		return result;
	}

	/// <summary>Return the sub-building definition, or null if undefined.</summary>
	public SubBuildingMasterItem GetSubBuildingMaster(int subbuildingId)
	{
		subBuildingMasterById.TryGetValue(subbuildingId, out var item);
		return item;
	}

	/// <summary>
	/// Return the landmark definition, or null if undefined.
	/// </summary>
	public LandmarkMasterItem GetLandmarkMaster(int landmarkId)
	{
		landmarkMasterById.TryGetValue(landmarkId, out var item);
		return item;
	}

	public IReadOnlyDictionary<int, LandmarkMasterItem> GetLandmarkMasterDictionary()
	{
		return landmarkMasterById;
	}

	/// <summary>
	/// Return model_prefix used for the landmark's part folder, or null if undefined.
	/// BuildingMeshUtils.DeterminePrefix uses this to construct landmark-{model_prefix}.
	/// </summary>
	public string GetLandmarkModelPrefix(int landmarkId)
	{
		var item = GetLandmarkMaster(landmarkId);
		return string.IsNullOrEmpty(item?.model_prefix) ? null : item.model_prefix;
	}

	public PathMasterItem GetPathMaster(int pathVariant)
	{
		if (pathMasterById.TryGetValue(pathVariant, out var item))
		{
			return item;
		}
		Debug.LogError($"MasterDataStore: PathVariant {pathVariant} not found in pathMasterById. Count: {pathMasterById.Count}");
		// Return defaults if needed, or require callers to check for null.
		throw new KeyNotFoundException($"PathVariant {pathVariant} was not found in MasterDataStore.");
	}

	public IReadOnlyDictionary<int, PathMasterItem> GetPathMasterDictionary()
	{
		return pathMasterById;
	}

	public IReadOnlyDictionary<int, TransitMasterItem> GetTransitMasterDictionary()
	{
		return transitMasterById;
	}

	public PathTypeItem GetPathType(int typeId)
	{
		pathTypesById.TryGetValue(typeId, out var item);
		return item;
	}

	public ProductItem GetProductMaster(int productId)
	{
		return productMasterByKey[productId.ToString()];
	}

	/// <summary>
	/// Return the product-master dictionary keyed by product ID strings. Used to enumerate all infrastructure goods
	/// for deterministic infrastructure-node bit assignment (InfraNetworkUtil.GetInfraNodeBit).
	/// </summary>
	public IReadOnlyDictionary<string, ProductItem> GetProductMasterDictionary()
	{
		return productMasterByKey;
	}

	public ProductItem GetProductMaster(string productId)
	{
		return productMasterByKey[productId];
	}
}


