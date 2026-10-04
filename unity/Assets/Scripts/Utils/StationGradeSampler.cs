using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

/// <summary>保存設計標高の線分をXZの包囲矩形で絞り込み、最近接位置の絶対標高を返す。</summary>
internal sealed class StationGradeSampler
{
    private const int LEAF_SIZE = 8;
    private static readonly ConditionalWeakTable<List<List<float>>, StationGradeSampler> samplers = new();

    private struct Segment
    {
        public Vector2 Start, End;
        public float StartHeight, EndHeight;
    }

    private struct Node
    {
        public Vector2 Min, Max;
        public int First, Count, Left, Right;
    }

    private readonly Segment[] segments;
    private readonly Node[] nodes;
    private int nodeCount;

    // 受信済み点列は編集せず、更新時に別のリストへ置き換える。地形の常駐変更では索引を作り直さない。
    public static StationGradeSampler Get(List<List<float>> profile)
    {
        return samplers.GetValue(profile, source => new StationGradeSampler(source));
    }

    private StationGradeSampler(List<List<float>> profile)
    {
        segments = new Segment[Mathf.Max(0, profile.Count - 1)];
        for (int i = 0; i < segments.Length; i++)
        {
            var a = profile[i];
            var b = profile[i + 1];
            segments[i] = new Segment
            {
                Start = new Vector2(a[0], a[2]), End = new Vector2(b[0], b[2]),
                StartHeight = a[1], EndHeight = b[1]
            };
        }
        int leaves = 1;
        while (segments.Length > LEAF_SIZE * leaves) leaves *= 2;
        nodes = new Node[segments.Length == 0 ? 0 : 2 * leaves - 1];
        if (segments.Length > 0) Build(0, segments.Length);
    }

    private int Build(int first, int count)
    {
        int index = nodeCount++;
        var node = new Node { First = first, Count = count, Left = -1, Right = -1 };
        if (count > LEAF_SIZE)
        {
            int half = count / 2;
            node.Left = Build(first, half);
            node.Right = Build(first + half, count - half);
            node.Min = Vector2.Min(nodes[node.Left].Min, nodes[node.Right].Min);
            node.Max = Vector2.Max(nodes[node.Left].Max, nodes[node.Right].Max);
        }
        else
        {
            node.Min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            node.Max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = first; i < first + count; i++)
            {
                node.Min = Vector2.Min(node.Min, Vector2.Min(segments[i].Start, segments[i].End));
                node.Max = Vector2.Max(node.Max, Vector2.Max(segments[i].Start, segments[i].End));
            }
            // floatの補間誤差で最近接点が端点の矩形をわずかに越えても、候補から除外しない。
            float padding = 1e-6f * Mathf.Max(1f, Mathf.Abs(node.Min.x), Mathf.Abs(node.Min.y),
                Mathf.Abs(node.Max.x), Mathf.Abs(node.Max.y));
            node.Min -= Vector2.one * padding;
            node.Max += Vector2.one * padding;
        }
        nodes[index] = node;
        return index;
    }

    public float Evaluate(Vector3 point)
    {
        if (segments.Length == 0) return 0f;
        float bestDistance = float.PositiveInfinity, height = 0f;
        int bestSegment = int.MaxValue;
        Search(0, new Vector2(point.x, point.z), ref bestDistance, ref bestSegment, ref height);
        return height;
    }

    private void Search(int index, Vector2 point, ref float bestDistance, ref int bestSegment, ref float height)
    {
        Node node = nodes[index];
        if (node.Left < 0)
        {
            for (int i = node.First; i < node.First + node.Count; i++)
            {
                Segment segment = segments[i];
                float t = PolylineUtils.ProjectOnSegment(point, segment.Start, segment.End);
                float distance = (point - Vector2.Lerp(segment.Start, segment.End, t)).sqrMagnitude;
                // 同距離なら元の点列で先の線分を選ぶ。自己交差や折り返しでも探索順で高さを変えない。
                if (distance < bestDistance || (distance == bestDistance && bestSegment != int.MaxValue && i < bestSegment))
                {
                    bestDistance = distance;
                    bestSegment = i;
                    height = Mathf.Lerp(segment.StartHeight, segment.EndHeight, t);
                }
            }
            return;
        }

        int near = node.Left, far = node.Right;
        float nearDistance = BoundsDistanceSquared(nodes[near], point);
        float farDistance = BoundsDistanceSquared(nodes[far], point);
        if (farDistance < nearDistance)
        {
            (near, far) = (far, near);
            (nearDistance, farDistance) = (farDistance, nearDistance);
        }
        if (nearDistance <= bestDistance) Search(near, point, ref bestDistance, ref bestSegment, ref height);
        if (farDistance <= bestDistance) Search(far, point, ref bestDistance, ref bestSegment, ref height);
    }

    private static float BoundsDistanceSquared(Node node, Vector2 point)
    {
        float x = point.x < node.Min.x ? node.Min.x - point.x : point.x > node.Max.x ? point.x - node.Max.x : 0f;
        float z = point.y < node.Min.y ? node.Min.y - point.y : point.y > node.Max.y ? point.y - node.Max.y : 0f;
        return x * x + z * z;
    }
}
