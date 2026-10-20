using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

/// <summary>
/// MessagePack binary decoder and mapper to GeneratedModels POCOs.
///
/// The backend's bulk BBOX endpoints (buildings/paths/parcels/zones/nodes)
/// return MessagePack binary responses when sent Accept: application/x-msgpack.
/// This receiver deserializes faster and with fewer allocations than JSON text parsing (Newtonsoft).
/// ApiClient.GetBulk examines the response Content-Type
/// and automatically selects JSON (Newtonsoft) or this decoder.
///
/// Design:
/// - No dependency on external libraries such as MessagePack-CSharp. Read the subset of the standard
///   MessagePack format used by this game's API responses directly.
/// - Intermediate decoded values are Dictionary&lt;string, object&gt; / List&lt;object&gt; /
///   string / long / double / bool / null. This matches the non-JToken representation produced
///   by Newtonsoft when deserializing to List&lt;object&gt;, so existing GeneratedModelUtils conversions
///   such as ToFloatJaggedArray / ToWaypointList work unchanged.
/// - Always convert map keys to strings. JSON implicitly converts integer dictionary keys to strings,
///   whereas msgpack preserves integer keys; normalize them here
///   (e.g. path_traffic {1: 0.5} -> key "1").
/// - Map to POCOs by public field name, using the same convention as Newtonsoft.
///   Numeric conversions (long -> int/float, etc.) are as permissive as Newtonsoft's.
/// - Use ConcurrentDictionary for static caches so concurrent calls from BackgroundWork
///   background threads remain safe.
/// </summary>
public static class MsgPackDecoder
{
    /// <summary>
    /// Decode MessagePack binary data and map it to an object of type T.
    /// Called by ApiClient.GetBulk on a background thread.
    /// </summary>
    public static T Deserialize<T>(byte[] data) where T : class
    {
        int pos = 0;
        object graph = ReadValue(data, ref pos);
        return (T)MapValue(graph, typeof(T));
    }

    // ================================================================
    // 1. Binary data -> intermediate object graph.
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
                // Extension types (0xc7-0xc9, 0xd4-0xd8) are not expected in backend responses.
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
            // Always stringify keys to match JSON's implicit dictionary-key conversion.
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

    // MessagePack numbers are big-endian.
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
        // BitConverter.Int32BitsToSingle is unavailable at the .NET Framework 4.x compatibility level,
        // so convert by reordering bytes from big-endian to the runtime's little-endian order.
        // Python msgpack packs floats as float64 by default, so this path is rarely used.
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
    // 2. Intermediate object graph -> POCOs such as GeneratedModels.
    // ================================================================

    /// <summary>
    /// Per-type cache mapping field names to FieldInfo.
    /// Use ConcurrentDictionary because background threads access it concurrently.
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
                    // If a derived class shadows a base-class field
                    // (e.g. BuildingSummary.space_types hides AgentData.space_types),
                    // prefer the derived-class field, as Newtonsoft does.
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
    /// Convert an intermediate value to the specified type.
    /// Numeric conversions (long -> int/float, etc.) are as permissive as Newtonsoft's.
    /// Throw InvalidCastException if conversion fails; the caller,
    /// ApiClient.GetBulk, logs and reports it as an error.
    /// </summary>
    private static object MapValue(object raw, Type targetType)
    {
        // For object fields (dynamically typed schema fields or List<object> elements),
        // pass the intermediate value through. GeneratedModelUtils interprets dictionaries, lists, and numbers.
        if (targetType == typeof(object)) return raw;

        if (raw == null) return null;

        // Treat Nullable<T> as its underlying type.
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
            // Support fields the backend returns as 0/1 integers instead of bools, such as platform_side.
            return ToLong(raw) != 0;
        }

        // Pass bin values (packed column byte arrays) directly to byte[] fields.
        if (raw is byte[] bytes && targetType == typeof(byte[])) return bytes;

        if (raw is List<object> rawList)
        {
            // Leave List<object> fields and their intermediate-value elements unchanged.
            if (targetType == typeof(List<object>)) return rawList;

            // T[] arrays (e.g. float[] for NodeData.position).
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
            // Dictionary<string, V> (e.g. Dictionary<string, float> for path_traffic).
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

            // Other classes (such as GeneratedModels POCOs): assign public fields by name.
            if (targetType.IsClass)
            {
                object instance = Activator.CreateInstance(targetType);
                Dictionary<string, FieldInfo> fieldMap = GetFieldMap(targetType);
                foreach (var kv in rawMap)
                {
                    if (!fieldMap.TryGetValue(kv.Key, out FieldInfo field)) continue; // Ignore unknown keys, as Newtonsoft does.
                    object converted = MapValue(kv.Value, field.FieldType);
                    if (converted == null && field.FieldType.IsValueType && Nullable.GetUnderlyingType(field.FieldType) == null)
                    {
                        continue; // Do not assign null to non-nullable value types; retain the default value.
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
