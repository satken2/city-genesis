using System.Collections.Generic;
using UnityEngine;

/// <summary>表示中の接続パス間で鉄塔を共有する。所有パスの退避・破棄時は残る候補へ引き継ぐ。</summary>
public sealed class PowerLineSupport : MonoBehaviour
{
    static readonly Dictionary<int,List<PowerLineSupport>> nodes = new();
    int node, path;
    GameObject visual;
    DrawLod lod;
    Renderer sampleRenderer;
    bool eligible = true;
    public void Initialize(int nodeId,int pathId,GameObject target,DrawLod detail)
    {
        node=nodeId; path=pathId; visual=target; lod=detail;
        sampleRenderer=target.GetComponentInChildren<Renderer>(true);
        Register();
    }
    void OnEnable() { if (visual != null) Register(); }
    void Register()
    {
        if (!nodes.TryGetValue(node,out var list)) nodes[node]=list=new List<PowerLineSupport>();
        if (!list.Contains(this)) list.Add(this);
        Refresh(list);
    }
    void OnDisable()
    {
        if (!nodes.TryGetValue(node,out var list)) return;
        list.Remove(this);
        if (list.Count==0) nodes.Remove(node); else Refresh(list);
    }
    void LateUpdate()
    {
        // 表示差し替えの待機中や個別非表示のパスへ、共有鉄塔の表示所有権を渡さない。
        bool visible = !ObjectVisibilityFilter.IsHidden(InputObjectType.PATH,path)
            && (sampleRenderer == null || !sampleRenderer.forceRenderingOff);
        if (visible == eligible) return;
        eligible=visible;
        if(nodes.TryGetValue(node,out var list)) Refresh(list);
    }
    static void Refresh(List<PowerLineSupport> list)
    {
        PowerLineSupport owner=null;
        foreach(var candidate in list)
            if (candidate != null && candidate.isActiveAndEnabled && candidate.eligible && (owner==null ||
                (int)candidate.lod>(int)owner.lod || candidate.lod==owner.lod && candidate.path<owner.path)) owner=candidate;
        foreach(var candidate in list)
            if (candidate != null && candidate.visual != null) candidate.visual.SetActive(candidate==owner);
    }
}
