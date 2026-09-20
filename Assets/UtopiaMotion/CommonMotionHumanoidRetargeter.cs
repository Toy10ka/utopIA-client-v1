using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Utopia.Motion
{
    /// <summary>
    /// Play-Mode preview: the existing CommonMotionPlayer remains the only clock and
    /// object player. This component transfers the source pose to a Humanoid Animator.
    /// It uses mesh bind poses (fallback: Avatar's reference skeleton), not frame 0 of
    /// the motion and not an arbitrary currently animated pose, for calibration.
    /// No IK, finger synthesis, collision handling, or environment-contact correction.
    /// Use on a duplicate preview avatar, not on the live XR interaction character.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    public sealed class CommonMotionHumanoidRetargeter : MonoBehaviour
    {
        [Header("References")]
        public CommonMotionPlayer source;
        [Tooltip("Drag the scene avatar's Animator, NOT its controller or Avatar asset.")]
        public Animator targetAnimator;
        [Tooltip("Optional body renderer used first for reference bind poses. Normally leave empty.")]
        public SkinnedMeshRenderer preferredBodyRenderer;

        [Header("Preview")]
        public bool bindOnStart = true;
        public bool driveMotion = true;
        [Tooltip("Correct reference bone directions, e.g. target A-pose versus source T-pose.")]
        public bool alignReferenceBoneDirections = true;
        [Tooltip("Offset in the source player's coordinates. Human only; objects do NOT move with this offset. Leave zero for InfBaGel comparisons.")]
        public Vector3 humanOffsetInSource = Vector3.zero;

        [Header("Temporarily pause competing controllers on the PREVIEW copy")]
        [Tooltip("Stops the Animator Controller from overwriting the target's bones. Restored when unbound/disabled.")]
        public bool pauseAnimator = true;
        [Tooltip("Optional gaze/IK/spring/character-motion behaviours that write to these bones. Do not include Source or this component.")]
        public Behaviour[] additionallyPause = Array.Empty<Behaviour>();
        [Tooltip("Prevents source-driven motion outside old skin bounds from disappearing. Preview setting; restored afterward.")]
        public bool updateSkinWhenOffscreen = true;

        public bool IsBound { get { return bound; } }
        public string Status { get { return status; } }
        public int MappedBoneCount { get { return mappedCount; } }
        public string LastValidation { get; private set; }

        private bool bound;
        private bool attemptedAutoBind;
        private string status = "Not bound. Enter Play Mode to preview.";
        private int mappedCount;
        private CommonMotionPlayer activeSource;
        private Animator activeAnimator;
        private Avatar activeAvatar;
        private CommonMotionClip activeClip;
        private Transform[] mapped;
        private Quaternion[] corrections;
        private Dictionary<Transform, Matrix4x4> referenceMatrices;
        private readonly List<BoneState> boneStates = new List<BoneState>();
        private readonly List<BehaviourState> pausedBehaviours = new List<BehaviourState>();
        private readonly List<SkinState> skinStates = new List<SkinState>();
        private Vector3 originalRootPosition;
        private Quaternion originalRootRotation;
        private bool hasRootSnapshot;

        private sealed class BoneState
        {
            public Transform transform;
            public Vector3 originalPosition, referencePosition;
            public Quaternion originalRotation, referenceRotation;
            public Vector3 originalScale, referenceScale;
        }
        private struct BehaviourState { public Behaviour component; public bool enabled; }
        private struct SkinState { public SkinnedMeshRenderer skin; public bool offscreen; }

        private static readonly HumanBodyBones[] HumanoidMap =
        {
            HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
            HumanBodyBones.Spine, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.Chest, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
            HumanBodyBones.UpperChest, HumanBodyBones.LeftToes, HumanBodyBones.RightToes,
            HumanBodyBones.Neck, HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder,
            HumanBodyBones.Head, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftHand, HumanBodyBones.RightHand
        };
        private static readonly int[] Required = { 0, 1, 2, 3, 4, 5, 7, 8, 15, 16, 17, 18, 19, 20, 21 };

        private void Start() { TryAutoBind(); }
        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            TryAutoBind();
            if (!bound) return;
            if (source != activeSource || targetAnimator != activeAnimator || activeAnimator.avatar != activeAvatar)
            {
                Unbind();
                attemptedAutoBind = false;
                return;
            }
            if (!source.IsLoaded || !source.isActiveAndEnabled || !driveMotion) return;
            try
            {
                if (!ReferenceEquals(activeClip, source.Clip)) ConfigureSource();
                ApplyPose();
            }
            catch (Exception e) { Fail(e); }
        }
        private void OnDisable() { Unbind(); attemptedAutoBind = false; }
        private void OnDestroy() { Unbind(); }
        private void TryAutoBind()
        {
            if (bound || attemptedAutoBind || !bindOnStart || source == null || !source.IsLoaded) return;
            attemptedAutoBind = true;
            Bind();
        }

        public static void CheckAnimator(Animator animator)
        {
            if (animator == null) throw new ArgumentException("Assign Target Animator from the Hierarchy.");
            if (!animator.gameObject.scene.IsValid()) throw new ArgumentException("Target must be a scene instance, not a prefab asset.");
            if (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                throw new ArgumentException("Target Animator requires a valid Humanoid Avatar (model Rig settings).");
        }

        [ContextMenu("Bind / Rebind in Play Mode")]
        public void Bind()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[CommonMotionRetarget] Bind is Play-Mode only. No target transforms were changed.", this);
                return;
            }
            Unbind();
            attemptedAutoBind = true;
            try
            {
                CheckAnimator(targetAnimator);
                if (source == null || !source.IsLoaded) throw new ArgumentException("Assign Source and Load / Reload JSON first.");
                if (source.transform == targetAnimator.transform || source.transform.IsChildOf(targetAnimator.transform))
                    throw new ArgumentException("Source player must not be inside the target avatar hierarchy (transform feedback).");
                CommonMotionRetargetMath.DecomposeRigidTRS(source.transform.localToWorldMatrix, out _, out _, out _);
                CommonMotionRetargetMath.DecomposeRigidTRS(targetAnimator.transform.localToWorldMatrix, out _, out _, out _);
                activeSource = source;
                activeAnimator = targetAnimator;
                activeAvatar = targetAnimator.avatar;
                mapped = MapBones(activeAnimator);
                referenceMatrices = ReadReferenceSkeleton(activeAnimator, mapped, out string referenceSummary);
                BuildBoneStates();
                ConfigureSource();

                Transform root = activeAnimator.transform;
                originalRootPosition = root.position;
                originalRootRotation = root.rotation;
                hasRootSnapshot = true;
                if (pauseAnimator) PauseBehaviour(activeAnimator);
                foreach (Behaviour component in additionallyPause ?? Array.Empty<Behaviour>())
                {
                    if (component == null) continue;
                    if (component == this || component == source || !component.transform.IsChildOf(root))
                        throw new ArgumentException("Additionally Pause must contain only target-avatar behaviours, not Source/Retargeter.");
                    PauseBehaviour(component);
                }
                if (updateSkinWhenOffscreen)
                    foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        skinStates.Add(new SkinState { skin = skin, offscreen = skin.updateWhenOffscreen });
                        skin.updateWhenOffscreen = true;
                    }
                bound = true;
                activeSource.PoseApplied += OnSourcePoseApplied;
                driveMotion = true;
                ApplyPose();
                status = "BOUND: " + mappedCount + "/22 source joints -> Humanoid. " + referenceSummary;
                Debug.Log("[CommonMotionRetarget] " + status + " | full-body preview; no contact/foot IK", this);
                ValidateAppliedPose();
                if (!pauseAnimator)
                    Debug.LogWarning("[CommonMotionRetarget] Animator is not paused. This is NOT a blend/mixer; other bone writers can conflict.", this);
            }
            catch (Exception e) { Fail(e); }
        }
        private void Fail(Exception e)
        {
            Unbind();
            status = "ERROR: " + e.Message;
            Debug.LogError("[CommonMotionRetarget] " + status, this);
        }
        private void PauseBehaviour(Behaviour component)
        {
            foreach (BehaviourState state in pausedBehaviours) if (state.component == component) return;
            pausedBehaviours.Add(new BehaviourState { component = component, enabled = component.enabled });
            component.enabled = false;
        }

        // Optional spine levels are collapsed into the highest available torso bone,
        // using source GLOBAL rotation so rotations on omitted levels are not lost.
        private static Transform[] MapBones(Animator animator)
        {
            Transform[] result = new Transform[22];
            for (int i = 0; i < result.Length; i++) result[i] = animator.GetBoneTransform(HumanoidMap[i]);
            foreach (int i in Required)
                if (result[i] == null) throw new ArgumentException("Missing required Humanoid bone: " + HumanoidMap[i]);
            if (result[9] == null && result[6] != null) { result[9] = result[6]; result[6] = null; }
            else if (result[9] == null && result[6] == null) { result[9] = result[3]; result[3] = null; }
            HashSet<Transform> unique = new HashSet<Transform>();
            for (int i = 0; i < result.Length; i++)
            {
                if (result[i] == null) continue;
                if (!result[i].IsChildOf(animator.transform) || result[i] == animator.transform)
                    throw new ArgumentException("Invalid/optimized target bone hierarchy: " + result[i].name);
                if (!unique.Add(result[i])) throw new ArgumentException("The Humanoid maps multiple joints to the same Transform: " + result[i].name);
            }
            int[] parents = { -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19 };
            for (int i = 1; i < 22; i++)
            {
                if (result[i] == null) continue;
                int parent = parents[i];
                while (parent >= 0 && result[parent] == null) parent = parents[parent];
                if (parent >= 0 && !result[i].IsChildOf(result[parent]))
                    throw new ArgumentException("Unexpected Humanoid hierarchy: " + result[i].name + " is not below " + result[parent].name);
            }
            return result;
        }

        private Dictionary<Transform, Matrix4x4> ReadReferenceSkeleton(Animator animator, Transform[] mapping,
                                                                      out string summary)
        {
            Transform root = animator.transform;
            Dictionary<Transform, Matrix4x4> meshReferences = new Dictionary<Transform, Matrix4x4>();
            List<SkinnedMeshRenderer> skins = new List<SkinnedMeshRenderer>(root.GetComponentsInChildren<SkinnedMeshRenderer>(true));
            if (preferredBodyRenderer != null && !preferredBodyRenderer.transform.IsChildOf(root))
                throw new ArgumentException("Preferred Body Renderer must belong to the Target Animator.");
            skins.Sort((a, b) => ScoreSkin(b, mapping).CompareTo(ScoreSkin(a, mapping)));
            foreach (SkinnedMeshRenderer skin in skins)
            {
                // A renderer placed under a currently animated hip/head cannot supply
                // a reliable static mesh->root transform here; use Avatar reference data.
                if (skin.sharedMesh == null || skin.transform.IsChildOf(mapping[0])) continue;
                Transform[] skinBones = skin.bones;
                Matrix4x4[] bindPoses = skin.sharedMesh.bindposes;
                if (skinBones.Length != bindPoses.Length) continue;
                Matrix4x4 meshToRoot = root.worldToLocalMatrix * skin.transform.localToWorldMatrix;
                for (int i = 0; i < skinBones.Length; i++)
                {
                    Transform bone = skinBones[i];
                    if (bone == null || bone == root || !bone.IsChildOf(root) || meshReferences.ContainsKey(bone)) continue;
                    meshReferences.Add(bone, meshToRoot * bindPoses[i].inverse);
                }
            }
            Dictionary<string, SkeletonBone> avatarReference = new Dictionary<string, SkeletonBone>();
            HashSet<string> duplicateNames = new HashSet<string>();
            SkeletonBone[] skeleton = animator.avatar.humanDescription.skeleton;
            if (skeleton != null)
                foreach (SkeletonBone bone in skeleton)
                {
                    if (avatarReference.ContainsKey(bone.name)) duplicateNames.Add(bone.name);
                    else avatarReference.Add(bone.name, bone);
                }
            foreach (string name in duplicateNames) avatarReference.Remove(name);
            List<Transform> chain = NeededTransforms(root, mapping);
            HashSet<Transform> mappedSet = new HashSet<Transform>(mapping);
            Dictionary<Transform, Matrix4x4> result = new Dictionary<Transform, Matrix4x4>();
            result[root] = Matrix4x4.identity;
            int fromMesh = 0, fromAvatar = 0, helperCount = 0;
            foreach (Transform bone in chain)
            {
                Matrix4x4 pose;
                if (meshReferences.TryGetValue(bone, out pose)) { result[bone] = pose; fromMesh++; }
                else if (avatarReference.TryGetValue(bone.name, out SkeletonBone refBone))
                {
                    result[bone] = result[bone.parent] * Matrix4x4.TRS(refBone.position, refBone.rotation, refBone.scale);
                    fromAvatar++;
                }
                else
                {
                    if (mappedSet.Contains(bone))
                        throw new ArgumentException("No bind/reference pose for " + bone.name + ". Assign a body renderer or expose the model skeleton; current animated pose is NOT used as calibration.");
                    // Unmapped structural helper such as an added model container.
                    result[bone] = result[bone.parent] * Matrix4x4.TRS(bone.localPosition, bone.localRotation, bone.localScale);
                    helperCount++;
                }
                CommonMotionRetargetMath.DecomposeRigidTRS(result[bone], out _, out _, out _);
            }
            summary = "Reference: mesh=" + fromMesh + ", Avatar=" + fromAvatar + ", static helpers=" + helperCount;
            if (helperCount > 0)
                Debug.LogWarning("[CommonMotionRetarget] " + helperCount + " unmapped helper transforms use their current local transform. These helpers must not be animated.", this);
            return result;
        }
        private int ScoreSkin(SkinnedMeshRenderer skin, Transform[] mapping)
        {
            if (skin == preferredBodyRenderer) return int.MaxValue;
            if (skin.sharedMesh == null) return -1;
            HashSet<Transform> bones = new HashSet<Transform>(skin.bones);
            int count = 0;
            foreach (Transform bone in mapping) if (bone != null && bones.Contains(bone)) count++;
            return count * 100000 + Mathf.Min(skin.sharedMesh.vertexCount, 99999);
        }
        private static int Depth(Transform node) { int result = 0; for (; node != null; node = node.parent) result++; return result; }
        private static List<Transform> NeededTransforms(Transform root, Transform[] mapping)
        {
            HashSet<Transform> set = new HashSet<Transform>();
            foreach (Transform bone in mapping)
                for (Transform node = bone; node != null && node != root; node = node.parent) set.Add(node);
            List<Transform> result = new List<Transform>(set);
            result.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
            return result;
        }
        private void BuildBoneStates()
        {
            foreach (Transform bone in NeededTransforms(activeAnimator.transform, mapped))
            {
                Matrix4x4 local = referenceMatrices[bone.parent].inverse * referenceMatrices[bone];
                CommonMotionRetargetMath.DecomposeRigidTRS(local, out Vector3 pos, out Quaternion rot, out Vector3 scale);
                boneStates.Add(new BoneState
                {
                    transform = bone, originalPosition = bone.localPosition, originalRotation = bone.localRotation,
                    originalScale = bone.localScale, referencePosition = pos, referenceRotation = rot, referenceScale = scale
                });
            }
        }
        private Vector3 TargetRestPosition(int i) { return referenceMatrices[mapped[i]].GetColumn(3); }
        private Quaternion TargetRestRotation(int i)
        {
            CommonMotionRetargetMath.DecomposeRigidTRS(referenceMatrices[mapped[i]], out _, out Quaternion q, out _);
            return q;
        }
        private int DirectionChild(int i)
        {
            // Main anatomical chain, not arbitrary first Transform child (which might be a twist helper).
            switch (i)
            {
                case 0: return mapped[3] != null ? 3 : (mapped[6] != null ? 6 : 9);
                case 1: return 4; case 2: return 5;
                case 3: return mapped[6] != null ? 6 : (mapped[9] != null ? 9 : 15);
                case 4: return 7; case 5: return 8;
                case 6: return mapped[9] != null ? 9 : (mapped[12] != null ? 12 : 15);
                case 7: return mapped[10] != null ? 10 : -1;
                case 8: return mapped[11] != null ? 11 : -1;
                case 9: return mapped[12] != null ? 12 : 15;
                case 12: return 15; case 13: return 16; case 14: return 17;
                case 16: return 18; case 17: return 19; case 18: return 20; case 19: return 21;
                default: return -1;
            }
        }
        private void ConfigureSource()
        {
            CommonMotionClip clip = source.Clip;
            CommonMotionValidation.Validate(clip);
            Vector3[] rest = CommonMotionRetargetMath.RestPositions(clip.skeleton);
            Quaternion sourceBasis = CommonMotionRetargetMath.BodyBasis(rest[1], rest[2], rest[0], rest[15]);
            Quaternion targetBasis = CommonMotionRetargetMath.BodyBasis(TargetRestPosition(1), TargetRestPosition(2), TargetRestPosition(0), TargetRestPosition(15));
            Quaternion bodyAlignment = sourceBasis * Quaternion.Inverse(targetBasis);
            corrections = new Quaternion[22];
            mappedCount = 0;
            for (int i = 0; i < 22; i++)
            {
                if (mapped[i] == null) continue;
                int child = DirectionChild(i);
                Vector3 targetDirection = Vector3.zero, sourceDirection = Vector3.zero;
                Quaternion alignment = bodyAlignment;
                if (child >= 0 && mapped[child] != null)
                {
                    targetDirection = TargetRestPosition(child) - TargetRestPosition(i);
                    sourceDirection = rest[child] - rest[i];
                }
                else
                {
                    // Terminal bones inherit the nearest driven parent's reference correction;
                    // fingers themselves are not generated by these CommonMotion tracks.
                    int parent = clip.skeleton.parent_indices[i];
                    while (parent >= 0 && mapped[parent] == null) parent = clip.skeleton.parent_indices[parent];
                    if (alignReferenceBoneDirections && parent >= 0)
                        alignment = corrections[parent] * Quaternion.Inverse(TargetRestRotation(parent));
                }
                corrections[i] = CommonMotionRetargetMath.BoneCorrection(alignment, TargetRestRotation(i), targetDirection, sourceDirection, alignReferenceBoneDirections);
                mappedCount++;
            }
            activeClip = clip;
        }

        private void OnSourcePoseApplied()
        {
            // Also follows Inspector scrubbing immediately while Unity's Play Mode is paused.
            if (!Application.isPlaying || !bound || !driveMotion || source != activeSource ||
                targetAnimator != activeAnimator || !source.IsLoaded) return;
            try
            {
                if (!ReferenceEquals(activeClip, source.Clip)) ConfigureSource();
                ApplyPose();
            }
            catch (Exception e) { Fail(e); }
        }

        private void ApplyPose()
        {
            if (!bound || !source.IsLoaded) return;
            if (!source.TryGetJointWorldPose(0, out Vector3 pelvis, out _)) return;
            Transform root = activeAnimator.transform;
            Vector3 sourceLocalPelvis = source.transform.InverseTransformPoint(pelvis);
            // Keep target root near the character, but do NOT move the source player or object tracks.
            // Root motion/contact quality on a new body shape is deliberately not 'fixed' here.
            root.position = source.transform.TransformPoint(new Vector3(sourceLocalPelvis.x, 0f, sourceLocalPelvis.z) + humanOffsetInSource);
            root.rotation = source.transform.rotation;
            foreach (BoneState state in boneStates)
            {
                state.transform.localPosition = state.referencePosition;
                state.transform.localRotation = state.referenceRotation;
                state.transform.localScale = state.referenceScale;
            }
            // Source order is parent before child, even with collapsed optional torso levels.
            for (int i = 0; i < 22; i++)
            {
                if (mapped[i] == null) continue;
                if (!source.TryGetJointWorldPose(i, out _, out Quaternion sourceRotation))
                    throw new InvalidOperationException("Source joint unavailable: " + i);
                mapped[i].rotation = CommonMotionRetargetMath.RetargetRotation(sourceRotation, corrections[i]);
            }
            mapped[0].position = pelvis + source.transform.TransformVector(humanOffsetInSource);
        }

        [ContextMenu("Validate applied pose (Play Mode)")]
        public void ValidateAppliedPose()
        {
            if (!bound || !source.IsLoaded)
            {
                LastValidation = "Not bound; validation was not run.";
                Debug.LogWarning("[CommonMotionRetarget] " + LastValidation, this);
                return;
            }
            float maxAngle = 0;
            for (int i = 0; i < 22; i++)
            {
                if (mapped[i] == null) continue;
                source.TryGetJointWorldPose(i, out _, out Quaternion q);
                maxAngle = Mathf.Max(maxAngle, Quaternion.Angle(mapped[i].rotation, CommonMotionRetargetMath.RetargetRotation(q, corrections[i])));
            }
            source.TryGetJointWorldPose(0, out Vector3 p, out _);
            float pelvisError = Vector3.Distance(mapped[0].position, p + source.transform.TransformVector(humanOffsetInSource));
            source.TryGetJointWorldPose(20, out Vector3 left, out _);
            source.TryGetJointWorldPose(21, out Vector3 right, out _);
            Vector3 offset = source.transform.TransformVector(humanOffsetInSource);
            LastValidation = "Applied rotation max error=" + maxAngle.ToString("F3") + " deg; pelvis error=" + pelvisError.ToString("G4") + " m.\n" +
                "Wrist deviation from source: L=" + Vector3.Distance(mapped[20].position, left + offset).ToString("F3") +
                " m, R=" + Vector3.Distance(mapped[21].position, right + offset).ToString("F3") + " m (body-proportion difference; NOT contact error).";
            if (maxAngle > 0.2f || pelvisError > 0.001f) Debug.LogWarning("[CommonMotionRetarget] " + LastValidation, this);
            else Debug.Log("[CommonMotionRetarget] " + LastValidation, this);
        }

        public string DescribeMapping()
        {
            if (mapped == null) return "Mapping is created at runtime.";
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < 22; i++)
                text.Append(i).Append(" ").Append(activeClip.skeleton.joint_names[i]).Append(" -> ")
                    .Append(mapped[i] == null ? "(optional / collapsed)" : mapped[i].name).Append('\n');
            return text.ToString();
        }

        [ContextMenu("Unbind and restore target")]
        public void Unbind()
        {
            bound = false;
            if (activeSource != null) activeSource.PoseApplied -= OnSourcePoseApplied;
            if (activeAnimator != null && hasRootSnapshot)
            {
                activeAnimator.transform.position = originalRootPosition;
                activeAnimator.transform.rotation = originalRootRotation;
            }
            foreach (BoneState state in boneStates)
                if (state.transform != null)
                {
                    state.transform.localPosition = state.originalPosition;
                    state.transform.localRotation = state.originalRotation;
                    state.transform.localScale = state.originalScale;
                }
            foreach (SkinState state in skinStates) if (state.skin != null) state.skin.updateWhenOffscreen = state.offscreen;
            foreach (BehaviourState state in pausedBehaviours) if (state.component != null) state.component.enabled = state.enabled;
            boneStates.Clear(); pausedBehaviours.Clear(); skinStates.Clear();
            activeClip = null; activeSource = null; activeAnimator = null; activeAvatar = null;
            mapped = null; corrections = null; referenceMatrices = null;
            mappedCount = 0; hasRootSnapshot = false;
            status = "Not bound. Target restored.";
        }
    }
}
