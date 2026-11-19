using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace PhotoSplat
{
    /// <summary>
    /// "Pinch a photo into particles, throw it, and it unfolds into a 3D scene."
    ///
    /// Idle       : the photo floats in front of you (hold your real phone behind it if you like).
    /// Gathering  : pinch on the photo: it breaks into particles that stream into your fingertips.
    /// Holding    : the particles swirl round your pinch and follow your hand.
    /// Expanding  : open your fingers: the particles fly out and become the SHARP Gaussian-splat scene.
    /// Shown      : the scene stays in the room. Pinch and hold for a moment to call it back to your hand.
    /// Reset      : both hands pinching for 1.5 s, or R on the keyboard.
    ///
    /// Every splat is drawn by one mesh (4 vertices each) and PhotoSplat.shader does all the animation.
    /// </summary>
    public class PhotoSplatEffect : MonoBehaviour
    {
        public enum State { Idle, Gathering, Holding, Expanding, Shown, Collapsing }

        [Header("Content (from tools/sharp_to_unity.py)")]
        public TextAsset splatData;
        public Texture2D photo;

        [Header("Shaders (referenced so they are included in the build)")]
        public Shader splatShader;
        public Shader photoShader;

        [Header("Photo panel")]
        [Tooltip("Height of the floating photo, metres (a phone screen is ~0.14).")] public float photoHeight = 0.14f;
        [Tooltip("Where the photo appears: forward / down from your head, metres.")] public Vector2 photoOffset = new Vector2(0.4f, 0.15f);
        [Tooltip("How close (m) a pinch must be to the photo to grab it.")] public float grabMargin = 0.08f;

        [Header("Scene placement")]
        [Tooltip("Distance from your head to the middle of the scene after the throw, metres.")] public float placeDistance = 1.6f;
        [Tooltip("Extra scale on top of the automatic one (1 = the scene fills the same angle as the photo did when taken).")] public float sceneScale = 1f;
        [Tooltip("Moves the scene's viewpoint below your eyes, metres.")] public float sceneDrop = 0.1f;

        [Header("Look")]
        public float holdRadius = 0.035f;
        public float dotSigma = 0.0015f;
        [Range(0, 1.5f)] public float vignetteInner = 0.55f;
        [Range(0, 1.5f)] public float vignetteOuter = 1.0f;
        [Tooltip("Particles get brighter while flying.")] [Range(0, 3)] public float glow = 0.6f;
        [Tooltip("Toggle if the finished scene looks like diagonal streaks instead of a sharp picture.")] public bool flipEllipseY = false;

        [Header("Timing (s)")]
        public float gatherTime = 1.4f;
        public float expandTime = 2.8f;
        public float collapseTime = 1.2f;
        public float collapseHold = 0.4f;
        public float resetHold = 1.5f;

        public State Current { get; private set; } = State.Idle;

        PinchInput m_Input;
        PhotoSplatData m_Data;
        Mesh m_Mesh;
        Material m_Material;
        MaterialPropertyBlock m_Props;
        Transform m_Photo;
        Transform m_SceneRoot;

        float m_Gather, m_Expand, m_PinchTime, m_BothTime;
        Vector3 m_HandPos, m_ThrowDir = Vector3.forward;
        Vector2 m_GrabUV = new Vector2(0.5f, 0.5f);
        bool m_PhotoPlaced;

        // back-to-front sorting on a worker thread
        Task<int[]> m_Sort;
        Vector3 m_LastSortCam = new Vector3(1e9f, 0, 0);
        bool m_SceneMoved;

        static readonly int ID_SceneToWorld = Shader.PropertyToID("_PS_SceneToWorld");
        static readonly int ID_PhotoToWorld = Shader.PropertyToID("_PS_PhotoToWorld");
        static readonly int ID_HandPos = Shader.PropertyToID("_PS_HandPos");
        static readonly int ID_ThrowDir = Shader.PropertyToID("_PS_ThrowDir");
        static readonly int ID_GrabUV = Shader.PropertyToID("_PS_GrabUV");
        static readonly int ID_Gather = Shader.PropertyToID("_PS_Gather");
        static readonly int ID_Expand = Shader.PropertyToID("_PS_Expand");
        static readonly int ID_SrcSigma = Shader.PropertyToID("_PS_SrcSigma");
        static readonly int ID_DotSigma = Shader.PropertyToID("_PS_DotSigma");
        static readonly int ID_HoldRadius = Shader.PropertyToID("_PS_HoldRadius");
        static readonly int ID_VigInner = Shader.PropertyToID("_PS_VigInner");
        static readonly int ID_VigOuter = Shader.PropertyToID("_PS_VigOuter");
        static readonly int ID_Glow = Shader.PropertyToID("_PS_Glow");
        static readonly int ID_FlipAxisY = Shader.PropertyToID("_PS_FlipAxisY");

        void Reset()
        {
            splatShader = Shader.Find("PhotoSplat/Splat");
            photoShader = Shader.Find("Universal Render Pipeline/Unlit");
        }

        void Awake()
        {
            m_Input = GetComponent<PinchInput>();
            if (m_Input == null) m_Input = gameObject.AddComponent<PinchInput>();
        }

        void Start()
        {
            if (splatData == null) { Debug.LogError("[PhotoSplat] Assign a .bytes file made by sharp_to_unity.py", this); enabled = false; return; }
            if (splatShader == null) splatShader = Shader.Find("PhotoSplat/Splat");
            if (photoShader == null) photoShader = Shader.Find("Universal Render Pipeline/Unlit");

            m_Data = PhotoSplatData.Load(splatData.bytes);
            BuildMesh();
            m_Material = new Material(splatShader) { enableInstancing = true };
            m_Props = new MaterialPropertyBlock();

            m_SceneRoot = new GameObject("PhotoSplat Scene Root").transform;
            m_SceneRoot.SetParent(transform, false);

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "PhotoSplat Photo";
            Destroy(quad.GetComponent<Collider>());
            var pm = new Material(photoShader);
            if (photo != null) { pm.SetTexture("_BaseMap", photo); pm.mainTexture = photo; }
            quad.GetComponent<MeshRenderer>().sharedMaterial = pm;
            m_Photo = quad.transform;
            m_Photo.SetParent(transform, false);
            m_Photo.localScale = new Vector3(photoHeight * m_Data.ImageAspect, photoHeight, 1f);
            m_Photo.gameObject.SetActive(false);

            Debug.Log($"[PhotoSplat] {m_Data.Count:N0} splats, aspect {m_Data.ImageAspect:F2}, median depth {m_Data.MedianDepth:F2}");
        }

        void BuildMesh()
        {
            int n = m_Data.Count;
            var verts = new SplatVertex[n * 4];
            var rng = new System.Random(1234);
            for (int i = 0; i < n; i++)
            {
                var c = m_Data.Cov;
                int k = i * 6;
                var c0 = new Vector4(c[k], c[k + 1], c[k + 2], c[k + 3]);
                var c1 = new Vector4(c[k + 4], c[k + 5], m_Data.UV[i].x, m_Data.UV[i].y);
                float r0 = (float)rng.NextDouble(), r1 = (float)rng.NextDouble(), r2 = (float)rng.NextDouble();
                for (int corner = 0; corner < 4; corner++)
                {
                    verts[i * 4 + corner] = new SplatVertex
                    {
                        pos = m_Data.Positions[i],
                        col = m_Data.Colors[i],
                        c0 = c0,
                        c1 = c1,
                        r = new Vector4(r0, r1, r2, corner),
                    };
                }
            }

            const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;
            m_Mesh = new Mesh { name = "PhotoSplat", indexFormat = IndexFormat.UInt32 };
            m_Mesh.SetVertexBufferParams(n * 4,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4));
            m_Mesh.SetVertexBufferData(verts, 0, 0, verts.Length, 0, flags);
            m_Mesh.SetIndexBufferParams(n * 6, IndexFormat.UInt32);
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            UploadIndices(QuadIndices(order));
            m_Mesh.subMeshCount = 1;
            m_Mesh.SetSubMesh(0, new SubMeshDescriptor(0, n * 6), flags);
            m_Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
        }

        static int[] QuadIndices(int[] order)
        {
            var idx = new int[order.Length * 6];
            for (int i = 0; i < order.Length; i++)
            {
                int v = order[i] * 4, o = i * 6;
                idx[o] = v; idx[o + 1] = v + 2; idx[o + 2] = v + 1;
                idx[o + 3] = v + 1; idx[o + 4] = v + 2; idx[o + 5] = v + 3;
            }
            return idx;
        }

        void UploadIndices(int[] idx)
        {
            m_Mesh.SetIndexBufferData(idx, 0, 0, idx.Length,
                MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers);
        }

        void Update()
        {
            var cam = Camera.main;
            if (cam == null || m_Data == null) return;
            if (!m_PhotoPlaced && Time.timeSinceLevelLoad > 0.5f) PlacePhoto(cam);

            bool resetKey = Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
            m_BothTime = m_Input.BothPinching ? m_BothTime + Time.deltaTime : 0f;
            if (resetKey || m_BothTime > resetHold) { ResetEffect(cam); m_BothTime = -999f; return; }
            if (!m_Input.BothPinching && m_BothTime < 0f) m_BothTime = 0f;

            float dt = Time.deltaTime;
            Vector3 p = m_Input.PinchPoint;
            m_PinchTime = m_Input.IsPinching ? m_PinchTime + dt : 0f;

            switch (Current)
            {
                case State.Idle:
                    if (m_Input.PinchStarted && m_PhotoPlaced && TryGrab(p))
                    {
                        m_Photo.gameObject.SetActive(false);
                        m_Gather = 0f;
                        Current = State.Gathering;
                    }
                    break;

                case State.Gathering:
                case State.Holding:
                    m_HandPos = p;
                    if (!m_Input.IsPinching) { Throw(cam); break; }
                    m_Gather = Mathf.MoveTowards(m_Gather, 1f, dt / gatherTime);
                    if (m_Gather >= 1f) Current = State.Holding;
                    break;

                case State.Expanding:
                    m_Expand = Mathf.MoveTowards(m_Expand, 1f, dt / expandTime);
                    if (m_Expand >= 1f) Current = State.Shown;
                    break;

                case State.Shown:
                    if (m_Input.IsPinching && m_PinchTime > collapseHold)
                    {
                        m_Gather = 1f;   // the scene comes back to the hand, never to the photo
                        Current = State.Collapsing;
                    }
                    break;

                case State.Collapsing:
                    m_HandPos = p;
                    if (!m_Input.IsPinching) { Current = State.Expanding; break; }   // let go early: it goes back
                    m_Expand = Mathf.MoveTowards(m_Expand, 0f, dt / collapseTime);
                    if (m_Expand <= 0f) Current = State.Holding;
                    break;
            }

            UpdateSort(cam.transform.position);
            Draw();
        }

        bool TryGrab(Vector3 p)
        {
            Vector3 local = m_Photo.InverseTransformPoint(p);   // unit quad space in x/y, metres in z
            float mx = 0.5f + grabMargin / m_Photo.lossyScale.x, my = 0.5f + grabMargin / m_Photo.lossyScale.y;
            if (Mathf.Abs(local.x) > mx || Mathf.Abs(local.y) > my || Mathf.Abs(local.z) > grabMargin * 1.5f) return false;
            m_GrabUV = new Vector2(Mathf.Clamp01(local.x + 0.5f), Mathf.Clamp01(local.y + 0.5f));
            return true;
        }

        /// <summary>Put the scene's camera viewpoint at your eyes, looking where you threw, scaled so its
        /// middle lands <see cref="placeDistance"/> away: you see it from the angle the photo was taken.</summary>
        void Throw(Camera cam)
        {
            Vector3 head = cam.transform.position;
            Vector3 dir = m_HandPos - head;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) { dir = cam.transform.forward; dir.y = 0f; }
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            dir.Normalize();
            m_ThrowDir = dir;

            float s = placeDistance / Mathf.Max(m_Data.MedianDepth, 0.01f) * sceneScale;
            m_SceneRoot.SetPositionAndRotation(head + Vector3.down * sceneDrop, Quaternion.LookRotation(dir, Vector3.up));
            m_SceneRoot.localScale = Vector3.one * s;
            m_SceneMoved = true;
            m_Expand = 0f;
            Current = State.Expanding;
        }

        void PlacePhoto(Camera cam)
        {
            Vector3 head = cam.transform.position;
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 pos = head + fwd * photoOffset.x + Vector3.down * photoOffset.y;
            m_Photo.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head, Vector3.up));
            m_Photo.gameObject.SetActive(Current == State.Idle);
            m_PhotoPlaced = true;
        }

        void ResetEffect(Camera cam)
        {
            Current = State.Idle;
            m_Gather = m_Expand = 0f;
            PlacePhoto(cam);
        }

        void UpdateSort(Vector3 camPos)
        {
            if (m_Sort != null && m_Sort.IsCompleted)
            {
                if (m_Sort.Status == TaskStatus.RanToCompletion) UploadIndices(m_Sort.Result);
                else Debug.LogException(m_Sort.Exception);
                m_Sort = null;
            }
            if (m_Sort != null) return;
            bool sceneVisible = Current == State.Expanding || Current == State.Shown || Current == State.Collapsing;
            if (!sceneVisible) return;
            if (!m_SceneMoved && (camPos - m_LastSortCam).sqrMagnitude < 0.05f * 0.05f) return;

            m_SceneMoved = false;
            m_LastSortCam = camPos;
            Matrix4x4 toWorld = m_SceneRoot.localToWorldMatrix;
            Vector3[] local = m_Data.Positions;
            m_Sort = Task.Run(() =>
            {
                int n = local.Length;
                var keys = new float[n];
                var order = new int[n];
                for (int i = 0; i < n; i++)
                {
                    keys[i] = -(toWorld.MultiplyPoint3x4(local[i]) - camPos).sqrMagnitude;   // far first
                    order[i] = i;
                }
                Array.Sort(keys, order);
                return QuadIndices(order);
            });
        }

        void Draw()
        {
            if (Current == State.Idle) return;
            int n = m_Data.Count;
            float photoW = m_Photo.lossyScale.x, photoH = m_Photo.lossyScale.y;
            float pixel = Mathf.Sqrt(photoW * photoH / Mathf.Max(n, 1));

            m_Props.SetMatrix(ID_SceneToWorld, m_SceneRoot.localToWorldMatrix);
            m_Props.SetMatrix(ID_PhotoToWorld, m_Photo.localToWorldMatrix);
            m_Props.SetVector(ID_HandPos, m_HandPos);
            m_Props.SetVector(ID_ThrowDir, m_ThrowDir);
            m_Props.SetVector(ID_GrabUV, m_GrabUV);
            m_Props.SetFloat(ID_Gather, m_Gather);
            m_Props.SetFloat(ID_Expand, m_Expand);
            m_Props.SetFloat(ID_SrcSigma, pixel * 0.6f);
            m_Props.SetFloat(ID_DotSigma, dotSigma);
            m_Props.SetFloat(ID_HoldRadius, holdRadius);
            m_Props.SetFloat(ID_VigInner, vignetteInner);
            m_Props.SetFloat(ID_VigOuter, vignetteOuter);
            m_Props.SetFloat(ID_Glow, glow);
            m_Props.SetFloat(ID_FlipAxisY, flipEllipseY ? 1f : 0f);

            var rp = new RenderParams(m_Material)
            {
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 1000f),
                matProps = m_Props,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                layer = gameObject.layer,
            };
            Graphics.RenderMesh(rp, m_Mesh, 0, Matrix4x4.identity);
        }

        void OnDestroy()
        {
            if (m_Mesh != null) Destroy(m_Mesh);
            if (m_Material != null) Destroy(m_Material);
        }

        struct SplatVertex
        {
            public Vector3 pos;
            public Color32 col;
            public Vector4 c0, c1, r;
        }
    }
}
