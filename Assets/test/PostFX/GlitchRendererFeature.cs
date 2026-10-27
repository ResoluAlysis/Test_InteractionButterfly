using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 赛博故障后处理的 RendererFeature。
/// 挂在 URP Asset 引用的那个 Renderer 资产上即可。
/// </summary>
public class GlitchRendererFeature : ScriptableRendererFeature
{
    [Tooltip("留空自动用 Hidden/Custom/Glitch")]
    public Shader shader;

    [Tooltip("在 Bloom 之后执行（故障不被柔化）")]
    public bool afterPostProcessing = true;

    private Material _material;
    private GlitchPass _pass;

    public override void Create()
    {
        if (shader == null) shader = Shader.Find("Hidden/Custom/Glitch");

        if (shader == null)
        {
            Debug.LogError("[Glitch] 找不到 shader Hidden/Custom/Glitch");
            return;
        }

        _material = CoreUtils.CreateEngineMaterial(shader);

        _pass = new GlitchPass(_material)
        {
            renderPassEvent = afterPostProcessing
                ? RenderPassEvent.AfterRenderingPostProcessing
                : RenderPassEvent.BeforeRenderingPostProcessing,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_material == null) return;

        var camType = renderingData.cameraData.cameraType;
        if (camType != CameraType.Game && camType != CameraType.SceneView) return;
        if (renderingData.cameraData.renderType == CameraRenderType.Overlay) return;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        _pass?.Dispose();
    }

    // ====================================================================
    private class GlitchPass : ScriptableRenderPass
    {
        private const string k_ProfilerTag = "Glitch";

        private readonly Material _material;
        private RTHandle _tempRT;

        // 参数 ID 缓存
        private static readonly int IdIntensity   = Shader.PropertyToID("_Intensity");
        private static readonly int IdPulse       = Shader.PropertyToID("_Pulse");
        private static readonly int IdBlockAmount = Shader.PropertyToID("_BlockAmount");
        private static readonly int IdBlockCount  = Shader.PropertyToID("_BlockCount");
        private static readonly int IdBlockShift  = Shader.PropertyToID("_BlockShift");
        private static readonly int IdBlockSpeed  = Shader.PropertyToID("_BlockSpeed");
        private static readonly int IdRgbSplit    = Shader.PropertyToID("_RgbSplit");
        private static readonly int IdScanInt     = Shader.PropertyToID("_ScanlineIntensity");
        private static readonly int IdScanCount   = Shader.PropertyToID("_ScanlineCount");
        private static readonly int IdScanSpeed   = Shader.PropertyToID("_ScanlineSpeed");
        private static readonly int IdNoise       = Shader.PropertyToID("_NoiseAmount");
        private static readonly int IdVignette    = Shader.PropertyToID("_Vignette");
        private static readonly int IdSaturation  = Shader.PropertyToID("_Saturation");
        private static readonly int IdTint        = Shader.PropertyToID("_Tint");
        private static readonly int IdTintAmount  = Shader.PropertyToID("_TintAmount");
        private static readonly int IdBurst       = Shader.PropertyToID("_Burst");

        public GlitchPass(Material material)
        {
            _material = material;
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            var desc = cameraTextureDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples     = 1;

            RenderingUtils.ReAllocateIfNeeded(
                ref _tempRT, desc, FilterMode.Bilinear, TextureWrapMode.Clamp,
                name: "_GlitchTemp");
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_material == null) return;

            var camType = renderingData.cameraData.cameraType;
            if (camType != CameraType.Game && camType != CameraType.SceneView) return;

            var stack = VolumeManager.instance.stack;
            var vol = stack.GetComponent<GlitchVolume>();
            if (vol == null || !vol.IsActive()) return;

            var cmd = CommandBufferPool.Get(k_ProfilerTag);

            UpdateMaterial(vol);

            var renderer = renderingData.cameraData.renderer;
            var src = renderer.cameraColorTargetHandle;

            if (src == null || _tempRT == null)
            {
                CommandBufferPool.Release(cmd);
                return;
            }

            // 画面 → 临时 → 回写
            Blitter.BlitCameraTexture(cmd, src, _tempRT, _material, 0);
            Blitter.BlitCameraTexture(cmd, _tempRT, src);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void UpdateMaterial(GlitchVolume v)
        {
            _material.SetFloat(IdIntensity, v.intensity.value);
            _material.SetFloat(IdPulse,     ComputePulse(v));

            // ★ 爆发强度：每帧推进
            _material.SetFloat(IdBurst,
                GlitchBurst.Tick(Time.deltaTime, v.burstStrength.value, v.burstDuration.value));
            
            _material.SetFloat(IdBlockAmount, v.blockAmount.value);
            _material.SetFloat(IdBlockCount,  v.blockCount.value);
            _material.SetFloat(IdBlockShift,  v.blockShift.value);
            _material.SetFloat(IdBlockSpeed,  v.blockSpeed.value);

            _material.SetFloat(IdRgbSplit,   v.rgbSplit.value);

            _material.SetFloat(IdScanInt,    v.scanlineIntensity.value);
            _material.SetFloat(IdScanCount,  v.scanlineCount.value);
            _material.SetFloat(IdScanSpeed,  v.scanlineSpeed.value);

            _material.SetFloat(IdNoise,      v.noiseAmount.value);
            _material.SetFloat(IdVignette,   v.vignette.value);
            _material.SetFloat(IdSaturation, v.saturation.value);

            _material.SetColor(IdTint,       v.tint.value);
            _material.SetFloat(IdTintAmount, v.tintAmount.value);
        }

        /// <summary>脉冲：用 Perlin 噪声的尖峰模拟"偶发故障"</summary>
        private static float ComputePulse(GlitchVolume v)
        {
            if (!v.pulseEnabled.value) return 1f;

            float t = Time.time * v.pulseSpeed.value;

            // 两路不同频率的噪声叠加，避免单一路径的规律性
            float n = Mathf.PerlinNoise(t, 0.37f) * 0.65f
                    + Mathf.PerlinNoise(t * 2.3f, 5.71f) * 0.35f;

            // 幂次把它推向两端：大部分时间接近 0，偶尔接近 1
            float p = Mathf.Pow(Mathf.Clamp01(n), v.pulseSharpness.value);

            return Mathf.Lerp(v.pulseFloor.value, 1f, p);
        }

        public void Dispose()
        {
            _tempRT?.Release();
            _tempRT = null;
        }
    }
}