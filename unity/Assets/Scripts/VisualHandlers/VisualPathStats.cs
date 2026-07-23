using GeneratedModels;
using UnityEngine;
using System.Reflection;

/// <summary>
/// 道路の可視化用統計値を保持するシンプルなホルダー。
/// 所属地区色分けに必要な zone_id と path_type も保持する。
/// </summary>
public class VisualPathStats : MonoBehaviour
{
    public int zone_id;           // 所属地区ID (0は未設定)
    public float utilization;
    public float durability;
    public float effective_speed;
    public float pedestrian_traffic;

    public void InitFrom(PathSummary data)
    {
        durability = data.durability;

        // PathSummary のフィールドをリフレクションで安全に取得する。
        // 自動生成スキーマ（GeneratedModels）の更新タイミングに依存しないようにするため。
        utilization = GetFloatField(data, "utilization");
        effective_speed = GetFloatField(data, "effective_speed");
        pedestrian_traffic = GetFloatField(data, "pedestrian_traffic");

        // zone_id もリフレクションで取得
        FieldInfo zoneField = data.GetType().GetField("zone_id");
        zone_id = zoneField != null ? (int)zoneField.GetValue(data) : 0;
    }

    /// <summary>
    /// リフレクションで PathSummary オブジェクトから float フィールドの値を取得する。
    /// フィールドが存在しない、または取得に失敗した場合は 0 を返す。
    /// </summary>
    private float GetFloatField(PathSummary data, string fieldName)
    {
        FieldInfo field = data.GetType().GetField(fieldName);
        if (field != null)
        {
            object value = field.GetValue(data);
            if (value is float f) return f;
            if (value is double d) return (float)d;
            if (value is int i) return (float)i;
        }
        return 0f;
    }
}


