using System.Collections.Generic;
using UnityEngine;

/// <summary>Share towers among visible connected paths. Transfer ownership to remaining candidates when owning paths retire/destroy.</summary>
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
        // Do not transfer shared-tower display ownership to paths awaiting replacement or individually hidden.
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
