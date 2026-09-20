using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Utopia.Motion
{
    /// <summary>
    /// Source-skeleton debug playback. This is NOT a Unity Humanoid retargeter.
    /// Both backends use the same JSON reader and playback path.
    /// Attach to an empty GameObject; its transform places human AND object together.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CommonMotionPlayer : MonoBehaviour
    {
        [Header("Input (generated .common_motion.json)")]
        public TextAsset motionJson;
        [Header("Playback")]
        public bool playOnStart = true;
        public bool loop = false;
        [Min(0f)] public float playbackSpeed = 1f;
        [Header("Source skeleton / object appearance")]
        [Min(0.002f)] public float jointDiameter = 0.035f;
        [Min(0.001f)] public float boneDiameter = 0.018f;
        [Tooltip("Optional. Use a material compatible with your render pipeline.")]
        public Material humanMaterial;
        public Material objectMaterial;

        [Header("Preview visibility (does not stop playback)")]
        public bool showSourceSkeleton = true;
        public bool showObjects = true;

        private readonly List<Renderer> humanRenderers = new List<Renderer>();
        private readonly List<Renderer> objectRenderers = new List<Renderer>();
        private CommonMotionClip clip;
        private Transform generatedRoot;
        private Transform[] joints;
        private Transform[] bones;
        private Transform[] objectRoots;
        private readonly List<Mesh> ownedMeshes = new List<Mesh>();
        private Material fallbackMaterial;
        private double timeSeconds;
        private bool playing;

        // Read-only access by the retarget preview. Callers must not mutate the clip.
        public event Action PoseApplied;
        public CommonMotionClip Clip { get { return clip; } }
        public string LastValidationMessage { get; private set; }
        public bool LastValidationPassed { get; private set; }

        public bool TryGetJointWorldPose(int index, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!IsLoaded || joints == null || index < 0 || index >= joints.Length) return false;
            position = joints[index].position;
            rotation = joints[index].rotation;
            return true;
        }
        public void RefreshVisibility()
        {
            foreach (Renderer r in humanRenderers) if (r != null) r.enabled = showSourceSkeleton;
            foreach (Renderer r in objectRenderers) if (r != null) r.enabled = showObjects;
        }

        public bool IsLoaded { get { return clip != null && generatedRoot != null; } }
        public bool IsPlaying { get { return playing; } }
        public int FrameCount { get { return clip == null ? 0 : clip.time.frame_count; } }
        public float Fps { get { return clip == null ? 0f : clip.time.fps; } }
        public float DurationSeconds { get { return clip == null ? 0f : clip.time.frame_count / clip.time.fps; } }
        public float CurrentFrame { get { return clip == null ? 0f : Mathf.Min((float)timeSeconds * Fps, FrameCount - 1); } }
        public string MotionId { get { return clip == null ? "" : clip.motion_id; } }
        public string SpaceId { get { return clip == null ? "" : clip.space.frame_id; } }

        private void Start()
        {
            if (motionJson != null)
            {
                Reload();
                if (playOnStart && IsLoaded) Play();
            }
        }
        private void Update()
        {
            if (!playing || !IsLoaded) return;
            timeSeconds += Time.unscaledDeltaTime * Math.Max(0f, playbackSpeed);
            double duration = DurationSeconds;
            if (timeSeconds >= duration)
            {
                if (loop) timeSeconds %= duration;
                else { timeSeconds = duration; playing = false; }
            }
            ApplyTime();
        }
        private void OnDisable() { playing = false; }
        private void OnDestroy() { ClearGenerated(); }

        [ContextMenu("Load / Reload CommonMotion")]
        public void Reload()
        {
            if (motionJson == null)
            {
                Debug.LogError("[CommonMotion] Assign the generated JSON to Motion Json.", this);
                return;
            }
            try { LoadJson(motionJson.text); }
            catch (Exception e)
            {
                playing = false;
                Debug.LogError("[CommonMotion] Load failed: " + e.Message, this);
            }
        }
        // Can later be called by a network receiver; it accepts only shared-format JSON.
        public void LoadJson(string json)
        {
            CommonMotionClip next = JsonUtility.FromJson<CommonMotionClip>(json);
            CommonMotionValidation.Validate(next);
            ClearGenerated();
            LastValidationMessage = "";
            clip = next;
            timeSeconds = 0;
            playing = false;
            try
            {
                Build();
                ApplyTime();
                Debug.Log("[CommonMotion] Loaded " + MotionId + " | " + FrameCount + " frames @ " + Fps +
                          " fps | " + clip.objects.Length + " objects | space=" + SpaceId, this);
                ValidateFrame(0);
            }
            catch
            {
                ClearGenerated();
                throw;
            }
        }
        public void Play()
        {
            if (!IsLoaded) Reload();
            if (IsLoaded)
            {
                if (timeSeconds >= DurationSeconds) timeSeconds = 0;
                playing = true;
            }
        }
        public void Pause() { playing = false; }
        public void Restart()
        {
            if (!IsLoaded) Reload();
            if (!IsLoaded) return;
            timeSeconds = 0;
            ApplyTime();
            playing = true;
        }
        public void SetFrame(float frame)
        {
            if (!IsLoaded) return;
            playing = false;
            timeSeconds = Mathf.Clamp(frame, 0f, FrameCount - 1) / Fps;
            ApplyTime();
        }

        private Material MaterialFor(bool isObject)
        {
            Material selected = isObject ? objectMaterial : humanMaterial;
            if (selected != null) return selected;
            if (fallbackMaterial != null) return fallbackMaterial;
            Shader shader = null;
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("HDRP/Lit");
            }
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) throw new InvalidOperationException("Assign Human/Object Material in the Inspector");
            fallbackMaterial = new Material(shader) { name = "CommonMotion Debug Material", hideFlags = HideFlags.DontSave };
            return fallbackMaterial;
        }
        private static void RemoveObject(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj);
        }
        private Transform NewNode(string nodeName, Transform parent)
        {
            GameObject go = new GameObject(nodeName);
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(parent, false);
            return go.transform;
        }
        private Transform Primitive(string nodeName, PrimitiveType type, Transform parent, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = nodeName;
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(parent, false);
            Collider col = go.GetComponent<Collider>();
            if (col != null) { col.enabled = false; RemoveObject(col); }
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go.transform;
        }
        private void Build()
        {
            generatedRoot = NewNode("CommonMotion Generated Preview", transform);
            joints = new Transform[22];
            bones = new Transform[22];
            for (int j = 0; j < 22; ++j)
            {
                int p = clip.skeleton.parent_indices[j];
                joints[j] = NewNode(clip.skeleton.joint_names[j], p < 0 ? generatedRoot : joints[p]);
                joints[j].localPosition = clip.skeleton.rest_offsets_m[j];
                Transform dot = Primitive("Joint", PrimitiveType.Sphere, joints[j], MaterialFor(false));
                dot.localScale = Vector3.one * jointDiameter;
                humanRenderers.Add(dot.GetComponent<Renderer>());
                if (p >= 0)
                {
                    bones[j] = Primitive("Bone " + j, PrimitiveType.Cylinder, generatedRoot, MaterialFor(false));
                    humanRenderers.Add(bones[j].GetComponent<Renderer>());
                }
            }
            objectRoots = new Transform[clip.objects.Length];
            for (int k = 0; k < objectRoots.Length; ++k)
            {
                ObjectTrack track = clip.objects[k];
                Transform node = NewNode(track.track_id, generatedRoot);
                objectRoots[k] = node;
                if (track.mesh == null)
                {
                    Transform marker = Primitive("Object origin (mesh not provided)", PrimitiveType.Cube, node, MaterialFor(true));
                    marker.localScale = Vector3.one * 0.10f;
                    objectRenderers.Add(marker.GetComponent<Renderer>());
                    Debug.LogWarning("[CommonMotion] " + track.asset_id + ": origin marker only, not the object shape.", this);
                }
                else
                {
                    Mesh mesh = new Mesh { name = track.asset_id + " CommonMesh", hideFlags = HideFlags.DontSave };
                    ownedMeshes.Add(mesh);
                    mesh.indexFormat = track.mesh.vertices_m.Length > 65534 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                    mesh.vertices = track.mesh.vertices_m;
                    mesh.triangles = track.mesh.triangles;
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    node.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer renderer = node.gameObject.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = MaterialFor(true);
                    objectRenderers.Add(renderer);
                }
            }
        }
        private void ApplyTime()
        {
            if (!IsLoaded) return;
            RefreshVisibility();
            float f = Mathf.Clamp((float)timeSeconds * Fps, 0f, FrameCount - 1);
            int i = Mathf.FloorToInt(f);
            int j = Mathf.Min(i + 1, FrameCount - 1);
            float t = f - i;
            HumanFrame a = clip.human.frames[i], b = clip.human.frames[j];
            joints[0].localPosition = Vector3.Lerp(a.pelvis_position_m, b.pelvis_position_m, t);
            joints[0].localRotation = Quaternion.Slerp(a.root_rotation_xyzw, b.root_rotation_xyzw, t);
            for (int k = 1; k < 22; ++k)
                joints[k].localRotation = Quaternion.Slerp(a.body_local_rotation_xyzw[k-1], b.body_local_rotation_xyzw[k-1], t);
            for (int k = 1; k < 22; ++k)
            {
                Vector3 p0 = generatedRoot.InverseTransformPoint(joints[clip.skeleton.parent_indices[k]].position);
                Vector3 p1 = generatedRoot.InverseTransformPoint(joints[k].position);
                Vector3 d = p1 - p0;
                bones[k].localPosition = (p0 + p1) * 0.5f;
                bones[k].localRotation = d.sqrMagnitude < 1e-12f ? Quaternion.identity : Quaternion.FromToRotation(Vector3.up, d);
                bones[k].localScale = new Vector3(boneDiameter, d.magnitude * 0.5f, boneDiameter);
            }
            for (int k = 0; k < objectRoots.Length; ++k)
            {
                ObjectFrame oa = clip.objects[k].frames[i], ob = clip.objects[k].frames[j];
                objectRoots[k].localPosition = Vector3.Lerp(oa.position_m, ob.position_m, t);
                objectRoots[k].localRotation = Quaternion.Slerp(oa.rotation_xyzw, ob.rotation_xyzw, t);
            }
            PoseApplied?.Invoke();
        }
        [ContextMenu("Validate current integer frame FK")]
        public void ValidateCurrentFrame() { ValidateFrame(Mathf.RoundToInt(CurrentFrame)); }
        private void ValidateFrame(int frame)
        {
            if (!IsLoaded)
            {
                LastValidationPassed = false;
                LastValidationMessage = "Load JSON first; FK validation was not run.";
                Debug.LogWarning("[CommonMotion] " + LastValidationMessage, this);
                return;
            }
            frame = Mathf.Clamp(frame, 0, FrameCount - 1);
            Vector3[] reference = clip.human.frames[frame].reference_joint_positions_m;
            if (reference == null || reference.Length != 22)
            {
                LastValidationPassed = false;
                LastValidationMessage = "Reference joints are missing; FK validation was not run.";
                Debug.LogWarning("[CommonMotion] " + LastValidationMessage, this);
                return;
            }
            double previousTime = timeSeconds;
            timeSeconds = frame / (double)Fps;
            ApplyTime();
            float maxError = 0;
            for (int k = 0; k < 22; ++k)
                maxError = Mathf.Max(maxError, Vector3.Distance(generatedRoot.InverseTransformPoint(joints[k].position), reference[k]));
            timeSeconds = previousTime;
            ApplyTime();
            string message = "[CommonMotion] frame=" + frame + " FK/reference max error=" + maxError.ToString("G5") + " m";
            LastValidationPassed = maxError <= 0.001f;
            LastValidationMessage = (LastValidationPassed ? "PASS: " : "FAIL: ") + message;
            if (maxError > 0.001f) Debug.LogWarning(message, this); else Debug.Log(message, this);
        }
        private void ClearGenerated()
        {
            playing = false;
            clip = null;
            if (generatedRoot != null) generatedRoot.gameObject.SetActive(false);
            if (generatedRoot != null) RemoveObject(generatedRoot.gameObject);
            generatedRoot = null;
            foreach (Mesh mesh in ownedMeshes) RemoveObject(mesh);
            ownedMeshes.Clear();
            humanRenderers.Clear(); objectRenderers.Clear();
            RemoveObject(fallbackMaterial);
            fallbackMaterial = null;
            joints = null; bones = null; objectRoots = null;
        }
    }
}
