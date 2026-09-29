using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// カメラの見ている景色をPNGにする(セーブデータの画像)。
/// 画面ではなくカメラを直接レンダーテクスチャへ描くので、画面に重ねたUI(Canvas・OnGUI)は写らない。
/// </summary>
public static class CameraCaptureUtil
{
    /// <summary>
    /// width×height のPNGを返す。縦横比が画面と違っても、カメラの縦の画角のまま描く。
    /// 1回の縮小で細部がちらつかないよう、2倍の大きさで描いてから縮める。
    /// </summary>
    public static byte[] CapturePng(Camera camera, int width, int height)
    {
        RenderTexture large = RenderTexture.GetTemporary(width * 2, height * 2, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture small = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture previousActive = RenderTexture.active;
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
        try
        {
            var request = new RenderPipeline.StandardRequest { destination = large };
            if (RenderPipeline.SupportsRenderRequest(camera, request))
            {
                RenderPipeline.SubmitRenderRequest(camera, request);
            }
            else
            {
                RenderTexture previousTarget = camera.targetTexture;
                camera.targetTexture = large;
                camera.Render();
                camera.targetTexture = previousTarget;
            }
            Graphics.Blit(large, small);
            RenderTexture.active = small;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply(false);
            return texture.EncodeToPNG();
        }
        finally
        {
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(large);
            RenderTexture.ReleaseTemporary(small);
            Object.Destroy(texture);
        }
    }
}
