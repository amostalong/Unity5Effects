﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif // UNITY_EDITOR

namespace Ist
{
    [AddComponentMenu("Ist/Screen Space Reflections")]
    [RequireComponent(typeof(Camera))]
    [RequireComponent(typeof(GBufferUtils))]
    [ExecuteInEditMode]
    public class ScreenSpaceReflections : MonoBehaviour
    {
        public enum SampleCount
        {
            Low,
            Medium,
            High,
            VeryHigh,
        }

        public SampleCount m_sample_count = SampleCount.Medium;
        [Range(1,8)]
        public int m_downsampling = 2;
        [Range(0.0f, 2.0f)]
        public float m_intensity = 1.0f;
        [Range(0.0f, 1.0f)]
        public float m_ray_diffusion = 0.01f;
        [Range(0.0f, 8.0f)]
        public float m_blur_size = 1.0f;

        public float m_raymarch_distance = 2.5f;
        public float m_falloff_distance = 2.5f;
        public float m_ray_hit_radius = 0.15f;
        public float m_max_accumulation = 25.0f;
        public float m_step_boost = 0.0f;

        public Shader m_shader;

        Material m_material;
        Mesh m_quad;
        public RenderTexture[] m_reflection_buffers = new RenderTexture[2];
        public RenderTexture[] m_accumulation_buffers = new RenderTexture[2];
        RenderBuffer[] m_rb = new RenderBuffer[2];


        public static RenderTexture CreateRenderTexture(int w, int h, int d, RenderTextureFormat f)
        {
            RenderTexture r = new RenderTexture(w, h, d, f);
            r.filterMode = FilterMode.Point;
            r.useMipMap = false;
            r.generateMips = false;
            r.Create();
            return r;
        }

#if UNITY_EDITOR
        void Reset()
        {
            m_shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Ist/ScreenSpaceReflections/Shaders/ScreenSpaceReflections.shader");
            GetComponent<GBufferUtils>().m_enable_inv_matrices = true;
            GetComponent<GBufferUtils>().m_enable_prev_depth = true;
        }
#endif // UNITY_EDITOR

        void Awake()
        {
#if UNITY_EDITOR
            var cam = GetComponent<Camera>();
            if (cam.renderingPath != RenderingPath.DeferredShading &&
                (cam.renderingPath == RenderingPath.UsePlayerSettings && PlayerSettings.renderingPath != RenderingPath.DeferredShading))
            {
                Debug.Log("ScreenSpaceReflections: Rendering path must be deferred.");
            }
#endif // UNITY_EDITOR
        }

        void OnDestroy()
        {
            Object.DestroyImmediate(m_material);
        }

        void OnDisable()
        {
            ReleaseRenderTargets();
        }

        void ReleaseRenderTargets()
        {
            for (int i = 0; i < m_reflection_buffers.Length; ++i)
            {
                if (m_reflection_buffers[i] != null)
                {
                    m_reflection_buffers[i].Release();
                    m_reflection_buffers[i] = null;
                }
                if (m_accumulation_buffers[i] != null)
                {
                    m_accumulation_buffers[i].Release();
                    m_accumulation_buffers[i] = null;
                }
            }
        }

        void UpdateRenderTargets()
        {
            Camera cam = GetComponent<Camera>();

            Vector2 reso = new Vector2(cam.pixelWidth, cam.pixelHeight) / m_downsampling;
            if (m_reflection_buffers[0] != null && m_reflection_buffers[0].width != (int)reso.x)
            {
                ReleaseRenderTargets();
            }
            if (m_reflection_buffers[0] == null || !m_reflection_buffers[0].IsCreated())
            {
                for (int i = 0; i < m_reflection_buffers.Length; ++i)
                {
                    m_reflection_buffers[i] = CreateRenderTexture((int)reso.x, (int)reso.y, 0, RenderTextureFormat.ARGB32);
                    m_reflection_buffers[i].filterMode = FilterMode.Bilinear;
                    Graphics.SetRenderTarget(m_reflection_buffers[i]);
                    GL.Clear(false, true, Color.black);

                    m_accumulation_buffers[i] = CreateRenderTexture((int)reso.x, (int)reso.y, 0, RenderTextureFormat.R8);
                    m_accumulation_buffers[i].filterMode = FilterMode.Bilinear;
                    Graphics.SetRenderTarget(m_accumulation_buffers[i]);
                    GL.Clear(false, true, Color.black);
                }
            }
        }

        [ImageEffectOpaque]
        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (m_material == null)
            {
                m_material = new Material(m_shader);
                m_material.hideFlags = HideFlags.DontSave;

                m_quad = MeshUtils.GenerateQuad();
            }
            UpdateRenderTargets();

            switch (m_sample_count)
            {
                case SampleCount.Low:
                    m_material.EnableKeyword ("QUALITY_FAST");
                    break;
                case SampleCount.Medium:
                    m_material.EnableKeyword ("QUALITY_MEDIUM");
                    break;
                case SampleCount.High:
                    m_material.EnableKeyword ("QUALITY_HIGH");
                    break;
                case SampleCount.VeryHigh:
                    m_material.EnableKeyword ("QUALITY_ULTRA");
                    break;
            }

            m_material.SetVector("_Params0", new Vector4(m_intensity, m_raymarch_distance, m_ray_diffusion, m_falloff_distance));
            m_material.SetVector("_Params1", new Vector4(m_max_accumulation, m_ray_hit_radius, m_step_boost, 0.0f));
            m_material.SetTexture("_ReflectionBuffer", m_reflection_buffers[1]);
            m_material.SetTexture("_AccumulationBuffer", m_accumulation_buffers[1]);
            m_material.SetTexture("_MainTex", src);

            // accumulate reflection
            m_rb[0] = m_reflection_buffers[0].colorBuffer;
            m_rb[1] = m_accumulation_buffers[0].colorBuffer;
            Graphics.SetRenderTarget(m_rb, m_reflection_buffers[0].depthBuffer);
            m_material.SetPass(0);
            Graphics.DrawMeshNow(m_quad, Matrix4x4.identity);


            var tmp1 = RenderTexture.GetTemporary(m_reflection_buffers[0].width, m_reflection_buffers[0].height, 0, RenderTextureFormat.ARGB32);
            var tmp2 = RenderTexture.GetTemporary(m_reflection_buffers[0].width, m_reflection_buffers[0].height, 0, RenderTextureFormat.ARGB32);
            tmp1.filterMode = FilterMode.Bilinear;
            tmp2.filterMode = FilterMode.Bilinear;

            if(m_blur_size > 0.0f)
            {
                // horizontal blur
                Graphics.SetRenderTarget(tmp1);
                m_material.SetTexture("_ReflectionBuffer", m_reflection_buffers[0]);
                m_material.SetVector("_BlurOffset", new Vector4(m_blur_size / src.width, 0.0f, 0.0f, 0.0f));
                m_material.SetPass(1);
                Graphics.DrawMeshNow(m_quad, Matrix4x4.identity);

                // vertical blur
                Graphics.SetRenderTarget(tmp2);
                m_material.SetTexture("_ReflectionBuffer", tmp1);
                m_material.SetVector("_BlurOffset", new Vector4(0.0f, m_blur_size / src.height, 0.0f, 0.0f));
                m_material.SetPass(1);
                Graphics.DrawMeshNow(m_quad, Matrix4x4.identity);
            }

            // combine
            Graphics.SetRenderTarget(dst);
            m_material.SetTexture("_ReflectionBuffer", m_blur_size > 0.0f ? tmp2 : m_reflection_buffers[0]);
            m_material.SetTexture("_AccumulationBuffer", m_accumulation_buffers[0]);
            Graphics.Blit(src, dst, m_material, 2);

            RenderTexture.ReleaseTemporary(tmp2);
            RenderTexture.ReleaseTemporary(tmp1);


            Swap(ref m_reflection_buffers[0], ref m_reflection_buffers[1]);
            Swap(ref m_accumulation_buffers[0], ref m_accumulation_buffers[1]);
        }

        public static void Swap<T>(ref T lhs, ref T rhs)
        {
            T tmp;
            tmp = lhs;
            lhs = rhs;
            rhs = tmp;
        }
    }
}