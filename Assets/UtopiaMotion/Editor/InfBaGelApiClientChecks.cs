using System;
using UnityEditor;
using UnityEngine;

namespace Utopia.Motion.Editor
{
    /// <summary>Optional Editor-only validation checks; no scene edits, HTTP requests or GPU generation.</summary>
    public static class InfBaGelApiClientChecks
    {
        private const string Id = "infbagel_0123456789abcdef0123456789abcdef";
        [MenuItem("Tools/utopIA/Run InfBaGel API Client Checks (no server)")]
        public static void Run()
        {
            int passed = 0;
            Check(() => Equal(InfBaGelApiProtocol.Endpoint("http://127.0.0.1:8002", "health"), "http://127.0.0.1:8002/health"), ref passed);
            Check(() => Equal(InfBaGelApiProtocol.Endpoint("http://localhost:8002/", "generate_motion"), "http://localhost:8002/generate_motion"), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Endpoint("file:///tmp/data", "health")), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Endpoint("http://u:p@localhost:8002", "health")), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Endpoint("http://localhost:8002?token=x", "health")), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Endpoint("http://localhost:8002#fragment", "health")), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Endpoint("http://localhost:8002", "anything")), ref passed);
            Check(() => Equal(Request().scene_id, InfBaGelApiProtocol.DefaultSceneId), ref passed);
            Check(() => { InfBaGelApiProtocol.Request("A_b-0", 100000, int.MaxValue); }, ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Request("../scene", 0, 0)), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Request("scene.json", 0, 0)), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Request("scene\n", 0, 0)), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Request(" scene", 0, 0)), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Request(new string('a', 129), 0, 0)), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Request("scene", -1, 0)), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Request("scene", 100001, 0)), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.Request("scene", 0, -1)), ref passed);
            Check(() =>
            {
                string json = JsonUtility.ToJson(Request());
                if (!json.Contains("\"scene_id\":") || !json.Contains("\"test_item_index\":") || !json.Contains("\"seed\":")) throw new Exception("Request fields differ from API.");
                InfBaGelGenerateMotionRequest req = JsonUtility.FromJson<InfBaGelGenerateMotionRequest>(json);
                if (req.test_item_index != 0 || req.seed != 0) throw new Exception("Request round trip failed.");
                Equal(req.scene_id, InfBaGelApiProtocol.DefaultSceneId);
            }, ref passed);
            Check(() =>
            {
                InfBaGelApiHealth h = InfBaGelApiProtocol.ReadHealth("{\"status\":\"ready\",\"busy\":true,\"api_version\":\"0.1.0\",\"runtime\":{\"backend\":\"infbagel\",\"mode\":\"official_hosi_case\"}}");
                if (!h.busy) throw new Exception("Busy status not parsed.");
            }, ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.ReadHealth("{\"status\":\"ready\",\"runtime\":{\"backend\":\"dart\"}}")), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.ReadHealth("{\"detail\":\"not health\"}")), ref passed);
            Check(() =>
            {
                if (!InfBaGelApiProtocol.IsJsonContentType("application/json; charset=utf-8") || InfBaGelApiProtocol.IsJsonContentType("text/html")) throw new Exception("Content-Type check failed.");
            }, ref passed);
            Check(() =>
            {
                if (Math.Abs(InfBaGelApiProtocol.ReadMilliseconds("1234.125") - 1234.125) > 1e-8 || InfBaGelApiProtocol.ReadMilliseconds("NaN") != -1) throw new Exception("Header number parsing failed.");
            }, ref passed);
            Check(() =>
            {
                CommonMotionClip clip = InfBaGelApiProtocol.ReadMotion(ResponseJson(Fixture()), Id, Request());
                if (clip.time.frame_count != 2 || clip.objects.Length != 1 || clip.objects[0].mesh.triangles.Length != 3) throw new Exception("Human/object response failed.");
            }, ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.ReadMotion(ResponseJson(Fixture()), "different_id", Request())), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.ReadMotion(ResponseJson(Fixture(), "other-scene"), Id, Request())), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.ReadMotion(ResponseJson(Fixture(), InfBaGelApiProtocol.DefaultSceneId, 1), Id, Request())), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.ReadMotion(ResponseJson(Fixture(), InfBaGelApiProtocol.DefaultSceneId, 0, 42), Id, Request())), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.ReadMotion("{\"detail\":\"busy\"}", null, Request())), ref passed);
            Check(() => Reject(() => InfBaGelApiProtocol.ReadMotion(JsonUtility.ToJson(Fixture()), Id, Request())), ref passed);
            Check(() =>
            {
                CommonMotionClip c = Fixture(); c.objects = new ObjectTrack[0];
                Reject(() => InfBaGelApiProtocol.ReadMotion(ResponseJson(c), Id, Request()));
            }, ref passed);
            Check(() =>
            {
                CommonMotionClip c = Fixture(); c.objects[0].mesh = null;
                Reject(() => InfBaGelApiProtocol.ReadMotion(ResponseJson(c), Id, Request()));
            }, ref passed);
            Check(() =>
            {
                CommonMotionClip c = Fixture(); c.human.frames[0].root_rotation_xyzw = new Quaternion(0, 0, 0, 0);
                Reject(() => InfBaGelApiProtocol.ReadMotion(ResponseJson(c), Id, Request()));
            }, ref passed);
            Check(() =>
            {
                CommonMotionClip c = Fixture(); c.objects[0].mesh.triangles[2] = 3;
                Reject(() => InfBaGelApiProtocol.ReadMotion(ResponseJson(c), Id, Request()));
            }, ref passed);
            Check(() =>
            {
                if (!InfBaGelApiProtocol.HttpError(429, "", "").Contains("busy") || !InfBaGelApiProtocol.HttpError(409, "", "").Contains("case")) throw new Exception("Error hint missing.");
            }, ref passed);
            Debug.Log("[INFBAGEL_CLIENT_CHECKS] PASS " + passed + " Editor checks. No GPU inference, HTTP transfer, mesh rendering or avatar playback was tested.");
        }
        private static void Check(Action action, ref int count) { action(); ++count; }
        private static void Equal(string a, string b) { if (a != b) throw new Exception("Expected " + b + ", got " + a); }
        private static void Reject(Action action)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("Expected validation to reject input.");
        }
        private static InfBaGelGenerateMotionRequest Request() { return InfBaGelApiProtocol.Request(InfBaGelApiProtocol.DefaultSceneId, 0, 0); }
        private static string ResponseJson(CommonMotionClip clip, string scene = InfBaGelApiProtocol.DefaultSceneId, int index = 0, int seed = 0)
        {
            InfBaGelMotionEnvelope env = new InfBaGelMotionEnvelope { source = new InfBaGelMotionSource
            {
                backend = "infbagel", api_generation = new InfBaGelGenerationMetadata { mode = "official_hosi_case", scene_id = scene, test_item_index = index, seed = seed }
            }};
            string motion = JsonUtility.ToJson(clip);
            return motion.Substring(0, motion.Length - 1) + "," + JsonUtility.ToJson(env).Substring(1);
        }
        private static CommonMotionClip Fixture()
        {
            string[] names = { "pelvis", "left_hip", "right_hip", "spine1", "left_knee", "right_knee", "spine2", "left_ankle", "right_ankle", "spine3", "left_foot", "right_foot", "neck", "left_collar", "right_collar", "head", "left_shoulder", "right_shoulder", "left_elbow", "right_elbow", "left_wrist", "right_wrist" };
            int[] parents = { -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19 };
            HumanFrame[] human = new HumanFrame[2]; ObjectFrame[] objects = new ObjectFrame[2];
            for (int f = 0; f < 2; ++f)
            {
                Quaternion[] q = new Quaternion[21];
                for (int j = 0; j < 21; ++j) q[j] = Quaternion.identity;
                human[f] = new HumanFrame { pelvis_position_m = new Vector3(0, 1, 0), root_rotation_xyzw = Quaternion.identity, body_local_rotation_xyzw = q };
                objects[f] = new ObjectFrame { position_m = new Vector3(1, 0, 0), rotation_xyzw = Quaternion.identity };
            }
            return new CommonMotionClip
            {
                schema_version = CommonMotionValidation.Version, motion_id = Id,
                time = new MotionTime { fps = 30, frame_count = 2, time_origin_s = 0 },
                space = new MotionSpace { basis = "unity_lh_y_up_z_forward", length_unit = "meter", frame_id = "synthetic_test" },
                skeleton = new MotionSkeleton { profile_id = "synthetic_test", joint_names = names, parent_indices = parents, rest_offsets_m = new Vector3[22] },
                human = new HumanTrack { frames = human },
                objects = new[] { new ObjectTrack { track_id = "synthetic_0", asset_id = "synthetic_mesh_not_clothesstand", frames = objects,
                    mesh = new CommonObjectMesh { vertices_m = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) }, triangles = new[] { 0, 1, 2 } } } }
            };
        }
    }
}
