using System;
using UnityEngine;

namespace Utopia.Motion
{
    [Serializable] public sealed class InfBaGelEnvironmentRef
    {
        public string environment_id, revision;
        public string FrameId { get { return "environment:" + environment_id + ":" + revision.Substring(0,16); } }
    }
    [Serializable] public sealed class InfBaGelSceneBox
    {
        public string name;
        public Vector3 center_m;
        public Quaternion rotation_xyzw;
        public Vector3 size_m;
    }
    [Serializable] public sealed class InfBaGelSceneRegistration
    {
        public string environment_id;
        public string coordinate_frame = "infbagel_scene_y_up_m";
        public Vector3 bounds_min_m, bounds_max_m;
        public float voxel_size_m = .02f;
        public string floor_policy = "flat_y0_halfspace_no_above_floor_voxels_v1";
        public InfBaGelSceneBox[] boxes;
    }
    [Serializable] public sealed class InfBaGelSceneReceipt
    {
        public InfBaGelEnvironmentRef environment;
        public string frame_id, kind, occupancy_sha256, floor_policy;
        public int[] shape;
        public int occupied_cells;
        public InfBaGelSceneRegistration registration;
    }
    [Serializable] public sealed class InfBaGelSceneQuery
    { public InfBaGelEnvironmentRef environment; public Vector3[] points_m; }
    [Serializable] public sealed class InfBaGelSceneQueryReply
    { public InfBaGelEnvironmentRef environment; public string frame_id; public bool[] occupied, in_bounds; }
    [Serializable] public sealed class InfBaGelSceneMetadata
    { public InfBaGelEnvironmentRef environment; public string frame_id,kind,occupancy_sha256; }
    [Serializable] internal sealed class SceneMotionSource
    { public SceneMotionGeneration api_generation; }
    [Serializable] internal sealed class SceneMotionGeneration
    { public InfBaGelSceneMetadata environment_input; }
    [Serializable] internal sealed class SceneMotionEnvelope
    { public SceneMotionSource source; }

    [Serializable] public sealed class InfBaGelScenePathRequest
    { public InfBaGelEnvironmentRef environment; public Vector3 start_m,goal_m; }
    [Serializable] public sealed class InfBaGelScenePathReply
    { public InfBaGelEnvironmentRef environment; public Vector3[] points_m; public float length_m; }

    [Serializable] public sealed class InfBaGelInitialStatePlacement
    {
        public string coordinate_frame = "infbagel_scene_y_up_m";
        public string source_frame_id;
        public Vector3 translation_m;
        public float yaw_degrees;
    }
    [Serializable] public sealed class InfBaGelInitialStateReport
    {
        public string source_kind;
        public int play_from_frame;
        public InfBaGelHistorySample[] expected_output_samples;
        public InfBaGelHistoryOutputCheck output_check;
    }
    [Serializable] public sealed class InfBaGelStartPreview
    { public Vector3[] joints_m; public Vector3 object_position_m; public Quaternion object_rotation_xyzw; }
    [Serializable] public sealed class InfBaGelEnvironmentValidation
    {
        public string status;
        public bool no_inference;
        public InfBaGelSceneMetadata environment;
        public InfBaGelInitialStateReport report;
        public InfBaGelStartPreview preview;
    }

    public static class InfBaGelSceneProtocol
    {
        public static Vector3 Reflect(Vector3 p) { return new Vector3(p.x,p.y,-p.z); }
        public static Quaternion Reflect(Quaternion q) { return new Quaternion(-q.x,-q.y,q.z,q.w); }
        public static bool Same(InfBaGelEnvironmentRef a, InfBaGelEnvironmentRef b)
        { return a!=null && b!=null && a.environment_id==b.environment_id && a.revision==b.revision; }
        public static InfBaGelInitialStatePlacement Placement(Matrix4x4 sourceWorld, Transform room, string sourceFrame)
        {
            InfBaGelGoalCoordinates.ValidatePlacement(room);
            Matrix4x4 relative = room.worldToLocalMatrix * sourceWorld;
            Quaternion q = relative.rotation;
            float yaw = Mathf.DeltaAngle(0f, q.eulerAngles.y);
            if (Quaternion.Angle(q, Quaternion.AngleAxis(yaw, Vector3.up)) > .01f)
                throw new ArgumentException("Initial placement must preserve up direction; yaw only.");
            return new InfBaGelInitialStatePlacement { source_frame_id = sourceFrame,
                translation_m = Reflect(relative.MultiplyPoint3x4(Vector3.zero)), yaw_degrees = -yaw };
        }
        public static InfBaGelHumanHistory TransformExpected(InfBaGelHumanHistory source, InfBaGelInitialStatePlacement placement)
        {
            var result = JsonUtility.FromJson<InfBaGelHumanHistory>(JsonUtility.ToJson(source));
            Quaternion q = Quaternion.AngleAxis(-placement.yaw_degrees, Vector3.up);
            Vector3 t = Reflect(placement.translation_m);
            foreach (var s in result.samples)
            {
                s.human.pelvis_position_m = q * s.human.pelvis_position_m + t;
                s.human.root_rotation_xyzw = q * s.human.root_rotation_xyzw;
                s.@object.position_m = q * s.@object.position_m + t;
                s.@object.rotation_xyzw = q * s.@object.rotation_xyzw;
            }
            return result;
        }
        public static string AddPlacement(string json, InfBaGelInitialStatePlacement placement)
        {
            if (placement == null) return json;
            string result = json.Substring(0,json.Length-1)+",\"initial_state_placement\":"+JsonUtility.ToJson(placement)+"}";
            if (System.Text.Encoding.UTF8.GetByteCount(result) > 131072) throw new ArgumentException("Request exceeds 128 KiB.");
            return result;
        }
        public static string AddEnvironment(string json,InfBaGelEnvironmentRef env)
        {
            if(env==null)return json;
            string result=json.Substring(0,json.Length-1)+",\"environment\":"+JsonUtility.ToJson(env)+"}";
            if(System.Text.Encoding.UTF8.GetByteCount(result)>131072)throw new ArgumentException("Request exceeds 128 KiB.");
            return result;
        }
        public static void CheckEcho(string json,InfBaGelEnvironmentRef expected)
        {
            SceneMotionEnvelope e=JsonUtility.FromJson<SceneMotionEnvelope>(json);
            InfBaGelSceneMetadata m=e!=null && e.source!=null && e.source.api_generation!=null ? e.source.api_generation.environment_input : null;
            if(m==null || !Same(m.environment,expected) || m.frame_id!=expected.FrameId)
                throw new ArgumentException("Server did not use the requested environment id/revision. Motion not applied.");
        }
    }
}
