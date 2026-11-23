using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Capture the camera view as an encoded image for save-data and asset manager previews.
/// Render the camera directly to a texture, rather than capturing the screen, so overlay UI (Canvas / OnGUI) is excluded.
/// </summary>
public static class CameraCaptureUtil
{
    /// <summary>
    /// Return a width x height PNG, retaining the camera's vertical field of view even if the aspect ratio differs from the screen.
    /// Render at twice the dimensions and downsample to avoid detail aliasing from a single reduction.
    /// </summary>
    public static byte[] CapturePng(Camera camera, int width, int height)
    {
        return Capture(camera, width, height, texture => texture.EncodeToPNG());
    }

    /// <summary>Return a width x height JPEG using the same supersampled capture pipeline as <see cref="CapturePng"/>.</summary>
    public static byte[] CaptureJpg(Camera camera, int width, int height, int quality = 90)
    {
        return Capture(camera, width, height, texture => texture.EncodeToJPG(quality));
    }

    private static byte[] Capture(Camera camera, int width, int height, Func<Texture2D, byte[]> encode)
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
            return encode(texture);
        }
        finally
        {
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(large);
            RenderTexture.ReleaseTemporary(small);
            UnityEngine.Object.Destroy(texture);
        }
    }
}
