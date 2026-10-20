using System;
using System.Collections.Generic;

/// <summary>Capped set selecting lowest fixed ranks. Retained points are independent of scan order or display history.</summary>
public sealed class StableSample<T>
{
    private readonly int capacity;
    private readonly List<(ulong rank, T value)> heap = new List<(ulong, T)>();
    public StableSample(int capacity) { this.capacity = Math.Max(0, capacity); }
    public void Add(ulong rank, T value)
    {
        if (capacity == 0) return;
        if (heap.Count < capacity)
        {
            int i = heap.Count;
            heap.Add((rank, value));
            while (i > 0)
            {
                int p = (i-1)/2;
                if (heap[p].rank >= heap[i].rank) break;
                (heap[i], heap[p]) = (heap[p], heap[i]); i = p;
            }
            return;
        }
        if (rank >= heap[0].rank) return;
        heap[0] = (rank, value);
        int at = 0;
        while (at*2+1 < heap.Count)
        {
            int next = at*2+1;
            if (next+1 < heap.Count && heap[next+1].rank > heap[next].rank) next++;
            if (heap[at].rank >= heap[next].rank) break;
            (heap[at], heap[next]) = (heap[next], heap[at]); at = next;
        }
    }
    public T[] ToArray()
    {
        heap.Sort((a,b) => a.rank.CompareTo(b.rank));
        return heap.ConvertAll(item => item.value).ToArray();
    }

    public static ulong Rank(float x, float z, int seed)
    {
        unchecked
        {
            ulong h = (uint)BitConverter.SingleToInt32Bits(x);
            h = (h << 32) | (uint)BitConverter.SingleToInt32Bits(z);
            h ^= (ulong)(uint)seed * 0x9e3779b97f4a7c15UL;
            h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
            h = (h ^ (h >> 27)) * 0x94d049bb133111ebUL;
            return h ^ (h >> 31);
        }
    }
}
