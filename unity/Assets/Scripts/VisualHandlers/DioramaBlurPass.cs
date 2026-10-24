using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Depth-of-field pass for diorama mode, making the city look like a miniature.
/// The blur follows the ratio of each pixel's depth to the focus distance (Resources/Shaders/DioramaBlur.shader).
/// Runs before URP post-processing, so bloom and tone mapping apply to the blurred image.
/// </summary>
public sealed class DioramaBlurPass : ScriptableRenderPass
{
    private const int PrefilterPass = 0;
    private const int GatherPass = 1;
    private const int CompositePass = 2;

    private static readonly int ParamsId = Shader.PropertyToID("_DioramaParams");
    private static readonly int HalfTexelId = Shader.PropertyToID("_DioramaHalfTexel");
    private static readonly int BlurTextureId = Shader.PropertyToID("_DioramaBlurTexture");
    private static readonly int CameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");

    private readonly Material material;
    private float focusDistance;
    private float strength;
    private float maxRadiusRatio;

    private class PassData
    {
        public Material material;
        public Vector4 parameters;
        public Vector4 halfTexel;
        public TextureHandle source;
        public TextureHandle depth;
        public TextureHandle prefiltered;
        public TextureHandle blurred;
        public TextureHandle destination;
    }

    public DioramaBlurPass()
    {
        renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        requiresIntermediateTexture = true;
        ConfigureInput(ScriptableRenderPassInput.Depth);
        Shader shader = Resources.Load<Shader>("Shaders/DioramaBlur");
        if (shader != null) material = CoreUtils.CreateEngineMaterial(shader);
    }

    /// <param name="focusDistance">Eye depth (m) of the in-focus plane.</param>
    /// <param name="strength">Blur amount per unit of (1 - focus / depth); 1 reaches the maximum radius only at infinity.</param>
    /// <param name="maxRadiusRatio">Maximum blur radius as a fraction of the screen height.</param>
    public void Setup(float focusDistance, float strength, float maxRadiusRatio)
    {
        this.focusDistance = focusDistance;
        this.strength = strength;
        this.maxRadiusRatio = maxRadiusRatio;
    }

    public void Dispose()
    {
        CoreUtils.Destroy(material);
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        if (material == null) return;
        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

        TextureHandle source = resourceData.cameraColor;
        TextureDesc desc = renderGraph.GetTextureDesc(source);
        desc.name = "_DioramaColor";
        desc.msaaSamples = MSAASamples.None;
        desc.useMipMap = false;
        desc.clearBuffer = false;
        desc.filterMode = FilterMode.Bilinear;
        TextureHandle destination = renderGraph.CreateTexture(desc);

        // Signed CoC goes in alpha, so half resolution always uses a floating-point format with alpha.
        TextureDesc halfDesc = desc;
        halfDesc.sizeMode = TextureSizeMode.Explicit;
        halfDesc.width = Mathf.Max(1, desc.width / 2);
        halfDesc.height = Mathf.Max(1, desc.height / 2);
        halfDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
        halfDesc.wrapMode = TextureWrapMode.Clamp;
        halfDesc.name = "_DioramaPrefiltered";
        TextureHandle prefiltered = renderGraph.CreateTexture(halfDesc);
        halfDesc.name = "_DioramaBlurred";
        TextureHandle blurred = renderGraph.CreateTexture(halfDesc);

        using (var builder = renderGraph.AddUnsafePass<PassData>("Diorama Blur", out var passData))
        {
            passData.material = material;
            passData.parameters = new Vector4(focusDistance, strength, maxRadiusRatio * desc.height * 0.5f, 0f);
            passData.halfTexel = new Vector4(1f / halfDesc.width, 1f / halfDesc.height, 0f, 0f);
            passData.source = source;
            passData.depth = resourceData.cameraDepthTexture;
            passData.prefiltered = prefiltered;
            passData.blurred = blurred;
            passData.destination = destination;

            builder.UseTexture(source, AccessFlags.Read);
            builder.UseTexture(passData.depth, AccessFlags.Read);
            builder.UseTexture(prefiltered, AccessFlags.ReadWrite);
            builder.UseTexture(blurred, AccessFlags.ReadWrite);
            builder.UseTexture(destination, AccessFlags.Write);

            builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
            {
                CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                RTHandle sourceHandle = data.source;
                RTHandle prefilteredHandle = data.prefiltered;
                RTHandle blurredHandle = data.blurred;
                RTHandle destinationHandle = data.destination;

                data.material.SetVector(ParamsId, data.parameters);
                data.material.SetVector(HalfTexelId, data.halfTexel);
                data.material.SetTexture(CameraDepthTextureId, data.depth);
                data.material.SetTexture(BlurTextureId, data.blurred);

                Blitter.BlitCameraTexture(cmd, sourceHandle, prefilteredHandle, data.material, PrefilterPass);
                Blitter.BlitCameraTexture(cmd, prefilteredHandle, blurredHandle, data.material, GatherPass);
                Blitter.BlitCameraTexture(cmd, sourceHandle, destinationHandle, data.material, CompositePass);
            });
        }

        resourceData.cameraColor = destination;
    }
}
