using System;
using UnityEngine;

namespace Utopia.Motion
{
    // Wire format uses serializable objects, not multi-dimensional/jagged JSON arrays.
    [Serializable] public sealed class CommonMotionClip
    {
        public string schema_version;
        public string motion_id;
        public MotionTime time;
        public MotionSpace space;
        public MotionSkeleton skeleton;
        public HumanTrack human;
        public ObjectTrack[] objects;
    }
    [Serializable] public sealed class MotionTime
    {
        public float fps;
        public int frame_count;
        public float time_origin_s;
    }
    [Serializable] public sealed class MotionSpace
    {
        public string frame_id;
        public string basis;
        public string length_unit;
    }
    [Serializable] public sealed class MotionSkeleton
    {
        public string profile_id;
        public string[] joint_names;
        public int[] parent_indices;
        public Vector3[] rest_offsets_m;
    }
    [Serializable] public sealed class HumanTrack { public HumanFrame[] frames; }
    [Serializable] public sealed class HumanFrame
    {
        public Vector3 pelvis_position_m;
        public Quaternion root_rotation_xyzw;
        public Quaternion[] body_local_rotation_xyzw;
        // Independent SMPL-X core-joint output for integer-frame FK checks.
        public Vector3[] reference_joint_positions_m;
    }
    [Serializable] public sealed class ObjectTrack
    {
        public string track_id;
        public string asset_id;
        public ObjectFrame[] frames;
        public CommonObjectMesh mesh; // Optional. If absent, the player shows an origin marker.
    }
    [Serializable] public sealed class ObjectFrame
    {
        public Vector3 position_m;
        public Quaternion rotation_xyzw;
    }
    [Serializable] public sealed class CommonObjectMesh
    {
        public Vector3[] vertices_m;
        public int[] triangles;
    }

    public static class CommonMotionValidation
    {
        public const string Version = "utopia.common_motion/0.1";
        private static readonly int[] Parents =
            { -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19 };
        private static readonly string[] Names =
        {
            "pelvis", "left_hip", "right_hip", "spine1", "left_knee", "right_knee",
            "spine2", "left_ankle", "right_ankle", "spine3", "left_foot", "right_foot",
            "neck", "left_collar", "right_collar", "head", "left_shoulder", "right_shoulder",
            "left_elbow", "right_elbow", "left_wrist", "right_wrist"
        };
        private static bool Finite(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }
        private static void Vector(Vector3 v, string field)
        {
            if (!Finite(v.x) || !Finite(v.y) || !Finite(v.z))
                throw new ArgumentException(field + ": non-finite position");
        }
        private static void Rotation(Quaternion q, string field)
        {
            float s = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (!Finite(s) || Mathf.Abs(s - 1f) > 0.002f)
                throw new ArgumentException(field + ": missing/non-unit quaternion");
        }
        public static void Validate(CommonMotionClip clip)
        {
            if (clip == null || clip.schema_version != Version)
                throw new ArgumentException("Unsupported/missing CommonMotion schema_version");
            if (clip.space == null || clip.space.basis != "unity_lh_y_up_z_forward" ||
                clip.space.length_unit != "meter")
                throw new ArgumentException("CommonMotion must already be converted to the Unity basis, in metres");
            if (clip.time == null || clip.time.frame_count < 1 || !Finite(clip.time.fps) || clip.time.fps <= 0)
                throw new ArgumentException("Invalid frame count or playback FPS");
            if (!Finite(clip.time.time_origin_s))
                throw new ArgumentException("Invalid time_origin_s");
            MotionSkeleton sk = clip.skeleton;
            if (sk == null || sk.joint_names == null || sk.joint_names.Length != 22 ||
                sk.parent_indices == null || sk.parent_indices.Length != 22 ||
                sk.rest_offsets_m == null || sk.rest_offsets_m.Length != 22)
                throw new ArgumentException("Expected a complete 22-joint source skeleton");
            for (int j = 0; j < 22; ++j)
            {
                if (sk.parent_indices[j] != Parents[j] || sk.joint_names[j] != Names[j])
                    throw new ArgumentException("Unexpected joint order/hierarchy at " + j);
                Vector(sk.rest_offsets_m[j], "rest offset " + j);
            }
            if (sk.rest_offsets_m[0].sqrMagnitude > 1e-8f)
                throw new ArgumentException("Root rest offset must be zero");
            if (clip.human == null || clip.human.frames == null || clip.human.frames.Length != clip.time.frame_count)
                throw new ArgumentException("Human frame count mismatch");
            for (int i = 0; i < clip.human.frames.Length; ++i)
            {
                HumanFrame f = clip.human.frames[i];
                if (f == null || f.body_local_rotation_xyzw == null || f.body_local_rotation_xyzw.Length != 21)
                    throw new ArgumentException("Missing human rotations at frame " + i);
                Vector(f.pelvis_position_m, "human frame " + i);
                Rotation(f.root_rotation_xyzw, "root frame " + i);
                for (int j = 0; j < 21; ++j) Rotation(f.body_local_rotation_xyzw[j], "body frame " + i);
                if (f.reference_joint_positions_m != null && f.reference_joint_positions_m.Length > 0)
                {
                    if (f.reference_joint_positions_m.Length != 22)
                        throw new ArgumentException("Reference joints must have 22 entries");
                    for (int j = 0; j < 22; ++j) Vector(f.reference_joint_positions_m[j], "reference joint");
                }
            }
            if (clip.objects == null) clip.objects = Array.Empty<ObjectTrack>();
            foreach (ObjectTrack o in clip.objects)
            {
                if (o == null || o.frames == null || o.frames.Length != clip.time.frame_count)
                    throw new ArgumentException("Object frame count mismatch");
                foreach (ObjectFrame f in o.frames)
                {
                    if (f == null) throw new ArgumentException("Missing object frame");
                    Vector(f.position_m, "object position");
                    Rotation(f.rotation_xyzw, "object rotation");
                }
                if (o.mesh != null)
                {
                    if (o.mesh.vertices_m == null || o.mesh.vertices_m.Length == 0 ||
                        o.mesh.triangles == null || o.mesh.triangles.Length == 0 || o.mesh.triangles.Length % 3 != 0)
                        throw new ArgumentException("Invalid embedded object mesh");
                    foreach (Vector3 v in o.mesh.vertices_m) Vector(v, "mesh vertex");
                    foreach (int index in o.mesh.triangles)
                        if (index < 0 || index >= o.mesh.vertices_m.Length)
                            throw new ArgumentException("Object triangle index out of range");
                }
            }
        }
    }
}
