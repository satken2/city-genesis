using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 一時停止中の自家用車・歩行者の選択。押下の瞬間に画面内の個体の向き付きの箱を集めてレイを当て
/// (常時コライダーを持たせないため)、手前を建物などが遮っていれば選ばない。5px以上の移動はカメラ操作へ譲る。
/// 離したときに <see cref="CitizenFollow.RequestPick"/> で市民を抽選して市民パネルを開く。物理レイキャストより先に呼ぶ。
/// </summary>
public static class CitizenPickSelection
{
    private static object pending;
    private static Vector2 pressed;
    private static bool cancelled;
    private static VehicleManager cars;
    private static PedestrianManager people;
    private static readonly List<Vehicle> carTargets = new();
    private static readonly List<Pedestrian> peopleTargets = new();

    public static void Cancel() { pending = null; cancelled = false; }

    public static bool ProcessPointer(Camera cam)
    {
        var mouse = Mouse.current;
        if (mouse == null || !TrafficClock.Paused) { pending = null; return false; }
        Vector2 point = mouse.position.ReadValue();
        if (mouse.leftButton.wasPressedThisFrame) { cancelled = false; pressed = point; pending = Pick(cam, cam.ScreenPointToRay(point)); }
        if (pending == null) return false;
        if ((point - pressed).sqrMagnitude >= 25) cancelled = true;
        if (mouse.leftButton.wasReleasedThisFrame)
        {
            var target = pending; pending = null;
            if (!cancelled) CitizenFollow.Ensure().RequestPick(target);
        }
        else if (!mouse.leftButton.isPressed) pending = null;
        return true;
    }

    private static object Pick(Camera cam, Ray ray)
    {
        if (cars == null) cars = Object.FindAnyObjectByType<VehicleManager>();
        if (people == null) people = Object.FindAnyObjectByType<PedestrianManager>();
        var frustum = GeometryUtility.CalculateFrustumPlanes(cam);
        object best = null;
        float nearest = float.PositiveInfinity;
        carTargets.Clear(); peopleTargets.Clear();
        if (cars != null)
        {
            cars.CollectPickTargets(carTargets, frustum);
            foreach (var v in carTargets)
                if (cars.RayHit(v, ray, out float d) && d < nearest) { nearest = d; best = v; }
        }
        if (people != null)
        {
            people.CollectPickTargets(peopleTargets, frustum);
            foreach (var p in peopleTargets)
                if (people.RayHit(p, ray, out float d) && d < nearest) { nearest = d; best = p; }
        }
        if (best == null) return null;
        if (Physics.Raycast(ray, out _, Mathf.Max(0, nearest - .05f), ~0, QueryTriggerInteraction.Ignore)) return null;
        return best;
    }
}
