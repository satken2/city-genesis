using UnityEngine;

/// <summary>装飾の種類ごとの描画距離。保存データやモデルの実寸とは独立した描画設定。</summary>
public static class PropRenderPolicy
{
    public const int Grass = 4;
    public const float DetailResidentDistance = 125f;   // 表示距離より広く候補を先に作り、小区画の境界で生成待ちを見せない
    public const int DetailCandidateBudget = 65536;

    public static bool NearOnly(int type) => type == Grass;
    public static float MaxDistance(int type) => NearOnly(type) ? 100f : DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE;
    public static bool Hidden(int type, float distance, DrawLod previous) => NearOnly(type)
        ? distance >= MaxDistance(type) * (previous == DrawLod.Hidden ? .9f : 1f)
        : DrawLodEvaluator.PropHiddenByDistance(distance, previous);

    // 円と交差する小区画数の上界で割り、移動による割当て変更で草が入れ替わるのを防ぐ。
    public static int DetailQuota(float chunkSize)
    {
        int side = Mathf.CeilToInt(2f * DetailResidentDistance / chunkSize) + 2;
        return DetailCandidateBudget / (side * side);
    }
}
