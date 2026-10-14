using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

/// <summary>
/// MessagePackバイナリのデコーダと、GeneratedModels系POCOへのマッパー。
///
/// バックエンドのBBOX一括取得エンドポイント(buildings/paths/parcels/zones/nodes)は
/// Accept: application/x-msgpack を送ると MessagePack バイナリでレスポンスを返す。
/// JSONテキストのパース(Newtonsoft)より高速・低アロケーションでデシリアライズする
/// ための受け側実装で、ApiClient.GetBulk がレスポンスの Content-Type を見て
/// JSON(Newtonsoft)とこのデコーダを自動で切り替える。
///
/// 設計方針:
/// - 外部ライブラリ(MessagePack-CSharp等)には依存しない。標準MessagePack仕様の
///   サブセット(このゲームのAPIレスポンスに現れる型)を自前で読む。
/// - デコード結果の中間表現は「Dictionary&lt;string, object&gt; / List&lt;object&gt; /
///   string / long / double / bool / null」。これはNewtonsoftで List&lt;object&gt; に
///   落とした場合の非JToken表現と互換であり、GeneratedModelUtils の既存の変換
///   (ToFloatJaggedArray / ToWaypointList 等)がそのまま機能する。
/// - マップのキーは常に文字列化する。JSONではdictのintキーが暗黙に文字列へ
///   変換されるが、msgpackはintキーを保持するため、ここで揃える
///   (例: path_traffic の {1: 0.5} → キー "1")。
/// - POCOへのマッピングはpublicフィールドの名前一致(Newtonsoftと同じ規約)。
///   数値の相互変換(long→int/float等)もNewtonsoftと同等に寛容にする。
/// - 静的キャッシュは ConcurrentDictionary を使い、BackgroundWork のバックグラウンド
///   スレッドから並行に呼ばれても安全にする。
/// </summary>
public static class MsgPackDecoder
{
    /// <summary>
    /// MessagePackバイナリをデコードし、型 T のオブジェクトへマッピングして返す。
    /// ApiClient.GetBulk からバックグラウンドスレッドで呼ばれる。
    /// </summary>
    public static T Deserialize<T>(byte[] data) where T : class
    {
        int pos = 0;
        object graph = ReadValue(data, ref pos);
        return (T)MapValue(graph, typeof(T));
    }

    // ================================================================
    // 1. バイナリ → 中間オブジェクトグラフ
    // ================================================================

    private static object ReadValue(byte[] buf, ref int pos)
    {
        byte b = buf[pos++];

        // positive fixint (0x00 - 0x7f)
        if (b <= 0x7f) return (long)b;
        // fixmap (0x80 - 0x8f)
        if (b <= 0x8f) return ReadMap(buf, ref pos, b & 0x0f);
        // fixarray (0x90 - 0x9f)
        if (b <= 0x9f) return ReadArray(buf, ref pos, b & 0x0f);
        // fixstr (0xa0 - 0xbf)
        if (b <= 0xbf) return ReadString(buf, ref pos, b & 0x1f);
        // negative fixint (0xe0 - 0xff)
        if (b >= 0xe0) return (long)unchecked((sbyte)b);

        switch (b)
        {
            case 0xc0: return null;                       // nil
            case 0xc2: return false;                      // false
            case 0xc3: return true;                       // true
            case 0xc4: return ReadBinary(buf, ref pos, ReadUInt8(buf, ref pos));   // bin 8
            case 0xc5: return ReadBinary(buf, ref pos, ReadUInt16(buf, ref pos));  // bin 16
            case 0xc6: return ReadBinary(buf, ref pos, checked((int)ReadUInt32(buf, ref pos))); // bin 32
            case 0xca: return (double)ReadFloat32(buf, ref pos);  // float 32
            case 0xcb: return ReadFloat64(buf, ref pos);          // float 64
            case 0xcc: return (long)ReadUInt8(buf, ref pos);      // uint 8
            case 0xcd: return (long)ReadUInt16(buf, ref pos);     // uint 16
            case 0xce: return (long)ReadUInt32(buf, ref pos);     // uint 32
            case 0xcf: return unchecked((long)ReadUInt64(buf, ref pos)); // uint 64
            case 0xd0: return (long)unchecked((sbyte)buf[pos++]); // int 8
            case 0xd1: return (long)unchecked((short)ReadUInt16(buf, ref pos)); // int 16
            case 0xd2: return (long)unchecked((int)ReadUInt32(buf, ref pos));   // int 32
            case 0xd3: return unchecked((long)ReadUInt64(buf, ref pos));        // int 64
            case 0xd9: return ReadString(buf, ref pos, ReadUInt8(buf, ref pos));   // str 8
            case 0xda: return ReadString(buf, ref pos, ReadUInt16(buf, ref pos));  // str 16
            case 0xdb: return ReadString(buf, ref pos, checked((int)ReadUInt32(buf, ref pos))); // str 32
            case 0xdc: return ReadArray(buf, ref pos, ReadUInt16(buf, ref pos));   // array 16
            case 0xdd: return ReadArray(buf, ref pos, checked((int)ReadUInt32(buf, ref pos))); // array 32
            case 0xde: return ReadMap(buf, ref pos, ReadUInt16(buf, ref pos));     // map 16
            case 0xdf: return ReadMap(buf, ref pos, checked((int)ReadUInt32(buf, ref pos)));   // map 32
            default:
                // ext系(0xc7-0xc9, 0xd4-0xd8)はバックエンドのレスポンスに現れない想定
                throw new NotSupportedException($"MsgPackDecoder: 未対応のフォーマットバイト 0x{b:x2} (offset {pos - 1})");
        }
    }

    private static List<object> ReadArray(byte[] buf, ref int pos, int count)
    {
        var list = new List<object>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(ReadValue(buf, ref pos));
        }
        return list;
    }

    private static Dictionary<string, object> ReadMap(byte[] buf, ref int pos, int count)
    {
        var map = new Dictionary<string, object>(count);
        for (int i = 0; i < count; i++)
        {
            object key = ReadValue(buf, ref pos);
            object value = ReadValue(buf, ref pos);
            // キーは常に文字列化する(JSONのdictキー暗黙文字列化と挙動を揃える)
            string keyStr = key as string ?? Convert.ToString(key, CultureInfo.InvariantCulture);
            if (keyStr != null)
            {
                map[keyStr] = value;
            }
        }
        return map;
    }

    private static string ReadString(byte[] buf, ref int pos, int length)
    {
        string s = Encoding.UTF8.GetString(buf, pos, length);
        pos += length;
        return s;
    }

    private static byte[] ReadBinary(byte[] buf, ref int pos, int length)
    {
        var bytes = new byte[length];
        Buffer.BlockCopy(buf, pos, bytes, 0, length);
        pos += length;
        return bytes;
    }

    // MessagePackの数値はビッグエンディアン
    private static byte ReadUInt8(byte[] buf, ref int pos) => buf[pos++];

    private static ushort ReadUInt16(byte[] buf, ref int pos)
    {
        ushort v = (ushort)((buf[pos] << 8) | buf[pos + 1]);
        pos += 2;
        return v;
    }

    private static uint ReadUInt32(byte[] buf, ref int pos)
    {
        uint v = ((uint)buf[pos] << 24) | ((uint)buf[pos + 1] << 16) | ((uint)buf[pos + 2] << 8) | buf[pos + 3];
        pos += 4;
        return v;
    }

    private static ulong ReadUInt64(byte[] buf, ref int pos)
    {
        ulong v = ((ulong)ReadUInt32(buf, ref pos) << 32);
        v |= ReadUInt32(buf, ref pos);
        return v;
    }

    private static float ReadFloat32(byte[] buf, ref int pos)
    {
        // BitConverter.Int32BitsToSingle は .NET Framework 4.x 互換レベルに存在しないため、
        // バイト列の並べ替え(ビッグエンディアン→実行環境のリトルエンディアン)で変換する。
        // なお Python msgpack は float を既定で float64 にパックするため、この経路は稀。
        byte[] tmp = { buf[pos + 3], buf[pos + 2], buf[pos + 1], buf[pos] };
        pos += 4;
        return BitConverter.ToSingle(tmp, 0);
    }

    private static double ReadFloat64(byte[] buf, ref int pos)
    {
        ulong bits = ReadUInt64(buf, ref pos);
        return BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }

    // ================================================================
    // 2. 中間オブジェクトグラフ → POCO(GeneratedModels等)
    // ================================================================

    /// <summary>
    /// 型ごとの「フィールド名 → FieldInfo」の解決キャッシュ。
    /// バックグラウンドスレッドから並行アクセスされるため ConcurrentDictionary を使う。
    /// </summary>
    private static readonly ConcurrentDictionary<Type, Dictionary<string, FieldInfo>> FieldMapCache =
        new ConcurrentDictionary<Type, Dictionary<string, FieldInfo>>();

    private static Dictionary<string, FieldInfo> GetFieldMap(Type type)
    {
        return FieldMapCache.GetOrAdd(type, t =>
        {
            var map = new Dictionary<string, FieldInfo>();
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (map.TryGetValue(f.Name, out FieldInfo existing))
                {
                    // 基底クラスのフィールドを派生クラスでシャドーイングしている場合
                    // (例: BuildingSummary.space_types が AgentData.space_types を隠す)は、
                    // Newtonsoftと同様に派生クラス側を優先する。
                    if (existing.DeclaringType != null && f.DeclaringType != null
                        && existing.DeclaringType.IsAssignableFrom(f.DeclaringType))
                    {
                        map[f.Name] = f;
                    }
                }
                else
                {
                    map[f.Name] = f;
                }
            }
            return map;
        });
    }

    /// <summary>
    /// 中間表現の値を、指定された型へ変換する。
    /// 数値の相互変換(long→int/float等)はNewtonsoftと同等に寛容に行う。
    /// 変換できない場合は InvalidCastException を投げる(呼び出し側の
    /// ApiClient.GetBulk がエラーとしてログ・通知する)。
    /// </summary>
    private static object MapValue(object raw, Type targetType)
    {
        // objectフィールド(スキーマの「型は動的」フィールドやList<object>の要素)には
        // 中間表現をそのまま渡す。GeneratedModelUtils側がDictionary/List/数値を解釈する。
        if (targetType == typeof(object)) return raw;

        if (raw == null) return null;

        // Nullable<T> は中身の型として扱う
        Type underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying != null) targetType = underlying;

        if (targetType == typeof(string)) return raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture);
        if (targetType == typeof(int)) return (int)ToLong(raw);
        if (targetType == typeof(long)) return ToLong(raw);
        if (targetType == typeof(short)) return (short)ToLong(raw);
        if (targetType == typeof(byte)) return (byte)ToLong(raw);
        if (targetType == typeof(float)) return (float)ToDouble(raw);
        if (targetType == typeof(double)) return ToDouble(raw);
        if (targetType == typeof(bool))
        {
            if (raw is bool bl) return bl;
            // バックエンドがboolを0/1のintで返すフィールド(platform_side等)に対応する
            return ToLong(raw) != 0;
        }

        // bin(列をまとめたバイト列)は byte[] フィールドへそのまま渡す。
        if (raw is byte[] bytes && targetType == typeof(byte[])) return bytes;

        if (raw is List<object> rawList)
        {
            // List<object> フィールドはそのまま(要素も中間表現のまま)
            if (targetType == typeof(List<object>)) return rawList;

            // T[] 配列(例: NodeData.position の float[])
            if (targetType.IsArray)
            {
                Type elem = targetType.GetElementType();
                var array = Array.CreateInstance(elem, rawList.Count);
                for (int i = 0; i < rawList.Count; i++)
                {
                    array.SetValue(MapValue(rawList[i], elem), i);
                }
                return array;
            }

            // List<T>
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type elem = targetType.GetGenericArguments()[0];
                var list = (System.Collections.IList)Activator.CreateInstance(targetType, rawList.Count);
                for (int i = 0; i < rawList.Count; i++)
                {
                    list.Add(MapValue(rawList[i], elem));
                }
                return list;
            }
        }

        if (raw is Dictionary<string, object> rawMap)
        {
            // Dictionary<string, V>(例: path_traffic の Dictionary<string, float>)
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                && targetType.GetGenericArguments()[0] == typeof(string))
            {
                Type valueType = targetType.GetGenericArguments()[1];
                var dict = (System.Collections.IDictionary)Activator.CreateInstance(targetType, rawMap.Count);
                foreach (var kv in rawMap)
                {
                    dict[kv.Key] = MapValue(kv.Value, valueType);
                }
                return dict;
            }

            // その他のクラス(GeneratedModelsのPOCO等): publicフィールドへ名前一致で詰める
            if (targetType.IsClass)
            {
                object instance = Activator.CreateInstance(targetType);
                Dictionary<string, FieldInfo> fieldMap = GetFieldMap(targetType);
                foreach (var kv in rawMap)
                {
                    if (!fieldMap.TryGetValue(kv.Key, out FieldInfo field)) continue; // 未知のキーは無視(Newtonsoftと同じ)
                    object converted = MapValue(kv.Value, field.FieldType);
                    if (converted == null && field.FieldType.IsValueType && Nullable.GetUnderlyingType(field.FieldType) == null)
                    {
                        continue; // null を非nullableな値型へは代入しない(デフォルト値のまま)
                    }
                    field.SetValue(instance, converted);
                }
                return instance;
            }
        }

        throw new InvalidCastException(
            $"MsgPackDecoder: {raw.GetType().Name} を {targetType.Name} へ変換できません");
    }

    private static long ToLong(object raw)
    {
        if (raw is long l) return l;
        if (raw is double d) return (long)d;
        if (raw is bool b) return b ? 1L : 0L;
        if (raw is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) return parsed;
        throw new InvalidCastException($"MsgPackDecoder: {raw?.GetType().Name} を整数へ変換できません");
    }

    private static double ToDouble(object raw)
    {
        if (raw is double d) return d;
        if (raw is long l) return l;
        if (raw is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed;
        throw new InvalidCastException($"MsgPackDecoder: {raw?.GetType().Name} を実数へ変換できません");
    }
}
