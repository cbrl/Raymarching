using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class RaymarchFeature : ScriptableRendererFeature
{
    public enum ColorMode
    {
        BRDF = 0,
        Heatmap
    };

    public struct SDFMaterial
    {
        public Vector3 color;
        public float   roughness;
        public float   metalness;
        public Vector3 pad0;

        public SDFMaterial(SDF.Material mat)
        {
            color     = new Vector3(mat.color.r, mat.color.g, mat.color.b);
            roughness = mat.roughness;
            metalness = mat.metalness;
            pad0      = Vector3.zero;
        }

        public static int GetSize()
        {
            return sizeof(float) * 8;
        }
    }

    public struct SDFData
    {
        public Matrix4x4   worldToModel;
        public Vector3     size;
        public float       pad0;
        public uint        type;
        public uint        operation;
        public float       blendStrength;
        public float       pad1;
        public Vector4     data;
        public SDFMaterial material;

        public SDFData(SDF sdf)
        {
            // The worldToModel matrix is calculated without the scale because the scale parameter is used
            // in a way that's dependent on the type of SDF. It can't be used as it normally would.
            worldToModel  = (Matrix4x4.Translate(sdf.transform.position) * Matrix4x4.Rotate(sdf.transform.rotation)).inverse;
            size          = sdf.transform.localScale;
            material      = new SDFMaterial(sdf.material);
            type          = (uint)sdf.type;
            operation     = (uint)sdf.operation;
            blendStrength = sdf.blendStrength;
            data          = sdf.data;
            pad0          = 0;
            pad1          = 0;
        }
        public static int GetSize()
        {
            return (sizeof(float) * 26) + (sizeof(uint) * 2) + SDFMaterial.GetSize();
        }
    }

    [System.Serializable]
    public class RaymarchFeatureSettings
    {
        public RenderPassEvent whenToInsert = RenderPassEvent.AfterRendering;

        public ComputeShader raymarchShader;
        public RenderTargetIdentifier? blitTarget = null;

        public int       maxIterations = 128;
        public float     maxDistance   = 100f;
        public float     minDistance   = 0.001f;
        public float     shadowBias    = 0.05f;
        public ColorMode colorMode     = ColorMode.BRDF;
        public Color     heatmapColor  = Color.red;
    }

    public class RaymarchPass : ScriptableRenderPass
    {
        static readonly string renderTag = "Raymarch"; // Add tag for Frame Debugger
        static readonly int tempTargetId = Shader.PropertyToID("_RaymarchTarget");

        public RaymarchFeatureSettings settings;
        public RenderTargetIdentifier raymarchTarget;
        public List<ComputeBuffer> buffersInUse = new();

        public RaymarchPass(RaymarchFeatureSettings settings)
        {
            this.renderPassEvent = settings.whenToInsert;
            this.settings = settings;

            if (settings.raymarchShader == null)
            {
                Debug.LogError("Raymarch pass has no shader set");
                return;
            }

            //raymarchMaterial = CoreUtils.CreateEngineMaterial(settings.raymarchShader);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            //settings.blitTarget ??= renderingData.cameraData.renderer.cameraColorTarget;
            settings.blitTarget ??= RenderTargetHandle.CameraTarget.Identifier();

            // Grab the camera target descriptor. We will use this when creating a temporary render texture.
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.enableRandomWrite = true;

            // Create a temporary render texture using the descriptor from above.
            cmd.GetTemporaryRT(tempTargetId, descriptor);
            raymarchTarget = new RenderTargetIdentifier(tempTargetId);

            // Release any allocate SDF buffers from the last frame
            foreach (var buffer in buffersInUse)
            {
                buffer.Dispose();
            }
            buffersInUse.Clear();
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            if (cmd == null)
            {
                throw new System.ArgumentNullException("cmd");
            }

            // Since we created a temporary render texture in OnCameraSetup, we need to release the memory here to avoid a leak.
            cmd.ReleaseTemporaryRT(tempTargetId);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (settings.raymarchShader == null)
            {
                Debug.LogError("Shader not set");
                return;
            }
            if (settings.blitTarget == null)
            {
                Debug.LogError("Blit target not defined");
                return;
            }

            UploadData(ref renderingData);

            int threadGroupsX = Mathf.CeilToInt(renderingData.cameraData.camera.pixelWidth / 8.0f);
            int threadGroupsY = Mathf.CeilToInt(renderingData.cameraData.camera.pixelHeight / 8.0f);

            CommandBuffer cmd = CommandBufferPool.Get(renderTag);
            cmd.SetComputeTextureParam(settings.raymarchShader, 0, "Source", settings.blitTarget.Value);
            cmd.SetComputeTextureParam(settings.raymarchShader, 0, "Destination", raymarchTarget);
            cmd.SetComputeTextureParam(settings.raymarchShader, 0, "_DepthTexture", Shader.GetGlobalTexture("_CameraDepthTexture"));
            cmd.DispatchCompute(settings.raymarchShader, 0, threadGroupsX, threadGroupsY, 1);
            cmd.Blit(raymarchTarget, settings.blitTarget.Value);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void UploadData(ref RenderingData renderingData)
        {
            // Set the shader uniforms
            var camera = renderingData.cameraData.camera;
            settings.raymarchShader.SetInt("_ColorMode", (int)settings.colorMode);
            settings.raymarchShader.SetVector("_HeatmapColor", settings.heatmapColor);
            settings.raymarchShader.SetMatrix("_CameraToWorldMatrix", camera.cameraToWorldMatrix);
            settings.raymarchShader.SetMatrix("_ProjectionToCameraMatrix", camera.projectionMatrix.inverse);
            settings.raymarchShader.SetInt("_MaxIterations", settings.maxIterations);
            settings.raymarchShader.SetFloat("_MinDistance", settings.minDistance);
            settings.raymarchShader.SetFloat("_MaxDistance", settings.maxDistance);
            settings.raymarchShader.SetFloat("_ShadowBias", settings.shadowBias);
            settings.raymarchShader.SetVector("_ZBufferParams", Shader.GetGlobalVector("_ZBufferParams"));

            // Update the SDF data
            var sdfObjects = FindObjectsOfType<SDF>();
            if (sdfObjects.Length == 0) return;

            var volumes = new SDFData[sdfObjects.Length];
            for (int i = 0; i < sdfObjects.Length; i++)
            {
                volumes[i] = new SDFData(sdfObjects[i]);
            }

            var volumeBuffer = new ComputeBuffer(volumes.Length, SDFData.GetSize());
            volumeBuffer.SetData(volumes);
            settings.raymarchShader.SetBuffer(0, "_Volumes", volumeBuffer);

            buffersInUse.Add(volumeBuffer);
        }
    }

    // Must be named "settings" (lowercase) to be shown in the Render Features inspector
    public RaymarchFeatureSettings settings = new();
    RaymarchPass raymarchPass;

    public override void Create()
    {
        raymarchPass = new RaymarchPass(settings);
    }

    public void OnDisable()
    {
        foreach (var buffer in raymarchPass.buffersInUse)
        {
            buffer.Dispose();
        }
        raymarchPass.buffersInUse.Clear();
    }

    // Called every frame once per camera
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // Ask the renderer to add our pass.
        // Could queue up multiple passes and/or pick passes to use
        renderer.EnqueuePass(raymarchPass);
    }
}
