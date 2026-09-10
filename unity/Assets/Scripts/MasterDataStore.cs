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
		public int? building_shape;
		public int? model_type;
		public int? max_floors;
		public int? min_floors;
		public float? height_per_floor;
		public bool? shared_building;
		// 地面パネル(地面全体を覆う水平スラブ)を描画するか。未設定(null)は描画しない(オプトイン)。
		// 描画時のマテリアルは floor パーツを a{variant}→b{bd_type}→common で解決する。
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
		// 選択できる構造種別(structure_master のID一覧、昇順)。空は構造の区別を持たないバリアント。
		public List<int> structures = new();
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
		// space, labour は必要になったら追加
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
	/// 公共交通マスター(transit_master)の1種別分。
	/// 公共交通の路線種別(バス路線・メトロ路線など)ごとに、区間パスとして使うパスバリアント
	/// (transit_path_id)と、停留所を設置できる下地パスバリアント(stop_path_variants)・
	/// 車両が走行できる下地パスバリアント(route_path_variants)を定義する。
	/// 停留所配置モードで「どのパス上に停留所を置けるか」のハイライトや入力制限に使う。
	/// </summary>
	[Serializable]
	public class TransitMasterItem
	{
		public string display_name;
		public string description;
		public int transit_path_id;
		public List<int> stop_path_variants = new();
		public List<int> route_path_variants = new();
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
	/// サブ建物マスター(subbuilding_master)の1棟分。
	/// マッシュアップ建物(MODEL_MASHUP)の施設バリアントを構成する付属棟の定義。
	/// 実ゲームでは配置計画も model_prefix の解決もバックエンドが行うため本編コードは
	/// 参照しないが、メッシュテストシーンが配置計画を自前生成するために使う。
	/// </summary>
	[Serializable]
	public class SubBuildingMasterItem
	{
		public int agent_variant;
		public string display_name;
		public string model_prefix;
		public float? width;     // 底面の幅(セル数単位。1単位=MASHUP_CELL_SIZE m。未定義は1セル)
		public float? height;    // 底面の奥行き(同上)
		public int? max_floors;
		public int? min_floors;
		public float? height_per_floor;
		public float? weight;    // 配置生成の棟数比(未定義は1.0、0で比率充填の対象外)
		public int? min_count;   // 最低配置数(未定義は0)
		public int? max_count;   // 最大配置数(未定義は無制限)。施設の顔(前面配置)は先頭(ID最小)の棟
	}

	[Serializable]
	public class SubBuildingsResponse
	{
		public Dictionary<int, SubBuildingMasterItem> subbuildings;
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

	// Path のマスター型は PathManager.cs の公開型を利用

	public bool IsInitialized { get; private set; } = false;

	private Dictionary<string, ProductItem> productMasterByKey = new Dictionary<string, ProductItem>();
	private Dictionary<int, AgentMasterItem> agentMasterById = new Dictionary<int, AgentMasterItem>();
	private Dictionary<int, SubBuildingMasterItem> subBuildingMasterById = new Dictionary<int, SubBuildingMasterItem>();
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

	public IEnumerator Initialize(bool forceReload = false)
	{
		if (IsInitialized && !forceReload) yield break;

		IsInitialized = false;

		// 全マスターの取得に成功するまでリトライする。
		// バックエンド起動直後やUnityクラッシュ後の自動再起動時は、マスターAPIが
		// まだ応答しない場合がある。以前は失敗しても IsInitialized = true に
		// してしまうバグがあり、空のマスターで進行してしまっていた。
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
		OnInitialized?.Invoke();
	}

	/// <summary>
	/// 全マスターの取得を1回試行する。全リクエストが成功した場合のみ
	/// onComplete(true) を呼ぶ(パース失敗はスキーマ不一致でありリトライで
	/// 回復しないため、成功判定は通信レベルで行う)。
	/// </summary>
	private IEnumerator InitializeAttempt(Action<bool> onComplete)
	{
		// 並列でリクエストを開始
		var productsReq = UnityWebRequest.Get($"{ApiClient.BASE_URL}/master/products");
		var pathsReq = UnityWebRequest.Get($"{ApiClient.BASE_URL}/master/paths");
		var transitReq = UnityWebRequest.Get($"{ApiClient.BASE_URL}/master/transit");
		var pathTypesReq = UnityWebRequest.Get($"{ApiClient.BASE_URL}/master/path_types");
		var agentsReq = UnityWebRequest.Get($"{ApiClient.BASE_URL}/master/agents");
		var subBuildingsReq = UnityWebRequest.Get($"{ApiClient.BASE_URL}/master/subbuildings");
		var buildingTypesReq = UnityWebRequest.Get($"{ApiClient.BASE_URL}/master/bd_types");
		var parcelTypesReq = UnityWebRequest.Get($"{ApiClient.BASE_URL}/master/parcel_types");
		var agentTypesReq = UnityWebRequest.Get($"{ApiClient.BASE_URL}/master/agent_types");

		var opProducts = productsReq.SendWebRequest();
		var opPaths = pathsReq.SendWebRequest();
		var opTransit = transitReq.SendWebRequest();
		var opPathTypes = pathTypesReq.SendWebRequest();
		var opAgents = agentsReq.SendWebRequest();
		var opSubBuildings = subBuildingsReq.SendWebRequest();
		var opBuildingTypes = buildingTypesReq.SendWebRequest();
		var opParcelTypes = parcelTypesReq.SendWebRequest();
		var opAgentTypes = agentTypesReq.SendWebRequest();

		// すべて完了を待つ
		yield return opProducts;
		yield return opPaths;
		yield return opTransit;
		yield return opPathTypes;
		yield return opAgents;
		yield return opSubBuildings;
		yield return opBuildingTypes;
		yield return opParcelTypes;
		yield return opAgentTypes;

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

		// Transit (公共交通マスター)
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

		// Sub buildings (サブ建物マスター)
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
			buildingTypesReq.result == UnityWebRequest.Result.Success &&
			parcelTypesReq.result == UnityWebRequest.Result.Success &&
			agentTypesReq.result == UnityWebRequest.Result.Success;

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
	/// 指定バリアントを構成するサブ建物の (subbuilding_id, 定義) 一覧を返す（未定義なら空リスト）。
	/// バックエンド(MasterData.get_subbuilding_ids)のシート行順に合わせるため、ID昇順で返す。
	/// 空リストのバリアントは「棟が分かれていない=1棟で建つ施設」を意味する。
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

	public PathMasterItem GetPathMaster(int pathVariant)
	{
		if (pathMasterById.TryGetValue(pathVariant, out var item))
		{
			return item;
		}
		Debug.LogError($"MasterDataStore: PathVariant {pathVariant} not found in pathMasterById. Count: {pathMasterById.Count}");
		// 必要であればデフォルト値を返す、あるいは呼び出し元でnullチェックが必要
		throw new KeyNotFoundException($"PathVariant {pathVariant} was not found in MasterDataStore.");
	}

	public IReadOnlyDictionary<int, PathMasterItem> GetPathMasterDictionary()
	{
		return pathMasterById;
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
	/// 製品マスターの辞書（キーは製品IDの文字列）を返す。インフラ財ノードビットの
	/// 決定的割り当て（InfraNetworkUtil.GetInfraNodeBit）で全インフラ財を列挙するために使う。
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


