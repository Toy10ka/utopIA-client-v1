using System;
using UnityEditor;
using UnityEngine;

namespace Utopia.Motion.Editor
{
    /// <summary>Optional Editor-only contract checks. No server, GPU, or scene changes.</summary>
    public static class DartApiClientChecks
    {
        [MenuItem("Tools/utopIA/Run DART API Client Checks (no server)")]
        public static void Run()
        {
            int passed = 0;
            Check(() => Equal(DartApiProtocol.Endpoint("http://127.0.0.1:8001", "health"), "http://127.0.0.1:8001/health"), ref passed);
            Check(() => Equal(DartApiProtocol.Endpoint("http://localhost:8001/", "generate_motion"), "http://localhost:8001/generate_motion"), ref passed);
            Check(() => Reject(() => DartApiProtocol.Endpoint("file:///tmp/foo", "health")), ref passed);
            Check(() => Reject(() => DartApiProtocol.Endpoint("http://u:p@localhost:8001", "health")), ref passed);
            Check(() => Reject(() => DartApiProtocol.Endpoint("http://localhost:8001?x=1", "health")), ref passed);
            Check(() => Equal(DartApiProtocol.Request("  wave   right hand ", 4f, 0, 5f).text, "wave right hand"), ref passed);
            Check(() => Reject(() => DartApiProtocol.Request("walk*20", 4f, 0, 5f)), ref passed);
            Check(() => Reject(() => DartApiProtocol.Request("walk\njump", 4f, 0, 5f)), ref passed);
            Check(() => Reject(() => DartApiProtocol.Request("   ", 4f, 0, 5f)), ref passed);
            Check(() => Reject(() => DartApiProtocol.Request("walk", float.NaN, 0, 5f)), ref passed);
            Check(() => Reject(() => DartApiProtocol.Request("walk", 21f, 0, 5f)), ref passed);
            Check(() => Reject(() => DartApiProtocol.Request("walk", 4f, -1, 5f)), ref passed);
            Check(() => Reject(() => DartApiProtocol.Request("walk", 4f, 0, 11f)), ref passed);
            Check(() =>
            {
                string json = JsonUtility.ToJson(DartApiProtocol.Request("wave right hand", 4f, 0, 5f));
                if (!json.Contains("\"duration_s\":") || !json.Contains("\"guidance\":")) throw new Exception("Wrong request wire field names.");
                DartGenerateMotionRequest roundtrip = JsonUtility.FromJson<DartGenerateMotionRequest>(json);
                Equal(roundtrip.text, "wave right hand");
                if (roundtrip.duration_s != 4f || roundtrip.seed != 0 || roundtrip.guidance != 5f) throw new Exception("Request round trip failed.");
            }, ref passed);
            Check(() =>
            {
                DartApiHealth health = DartApiProtocol.ReadHealth("{\"status\":\"ready\",\"busy\":true,\"api_version\":\"0.1.0\"}");
                if (!health.busy) throw new Exception("Busy status not parsed.");
            }, ref passed);
            Check(() => Reject(() => DartApiProtocol.ReadHealth("{\"detail\":\"not health\"}")), ref passed);
            Check(() =>
            {
                if (!DartApiProtocol.IsJsonContentType("application/json; charset=utf-8") || DartApiProtocol.IsJsonContentType("text/html"))
                    throw new Exception("Content-Type check failed.");
            }, ref passed);
            Check(() =>
            {
                if (Math.Abs(DartApiProtocol.ReadMilliseconds("1234.125") - 1234.125) > 1e-8 || DartApiProtocol.ReadMilliseconds("NaN") != -1)
                    throw new Exception("Invariant header parsing failed.");
            }, ref passed);
            Check(() =>
            {
                string json = JsonUtility.ToJson(Fixture());
                CommonMotionClip clip = DartApiProtocol.ReadMotion(json, "synthetic_client_check");
                if (clip.time.frame_count != 2 || clip.objects.Length != 0) throw new Exception("Motion round trip failed.");
            }, ref passed);
            Check(() => Reject(() => DartApiProtocol.ReadMotion(JsonUtility.ToJson(Fixture()), "different_id")), ref passed);
            Check(() => Reject(() => DartApiProtocol.ReadMotion("{\"detail\":\"busy\"}", null)), ref passed);
            Check(() =>
            {
                CommonMotionClip invalid = Fixture();
                invalid.human.frames[0].root_rotation_xyzw = new Quaternion(0, 0, 0, 0);
                Reject(() => DartApiProtocol.ReadMotion(JsonUtility.ToJson(invalid), null));
            }, ref passed);
            Check(() =>
            {
                if (!DartApiProtocol.HttpError(429, "", "").Contains("busy")) throw new Exception("429 hint missing.");
            }, ref passed);
            Debug.Log("[DART_CLIENT_CHECKS] PASS " + passed + " Editor checks. No GPU inference, HTTP transfer, or avatar playback was tested.");
        }
        private static void Check(Action action, ref int passed) { action(); ++passed; }
        private static void Equal(string a, string b) { if (a != b) throw new Exception("Expected " + b + ", got " + a); }
        private static void Reject(Action action)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("Expected validation to reject input.");
        }
        private static CommonMotionClip Fixture()
        {
            string[] names = { "pelvis", "left_hip", "right_hip", "spine1", "left_knee", "right_knee", "spine2", "left_ankle", "right_ankle", "spine3", "left_foot", "right_foot", "neck", "left_collar", "right_collar", "head", "left_shoulder", "right_shoulder", "left_elbow", "right_elbow", "left_wrist", "right_wrist" };
            int[] parents = { -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19 };
            HumanFrame[] frames = new HumanFrame[2];
            for (int f = 0; f < frames.Length; ++f)
            {
                Quaternion[] q = new Quaternion[21];
                for (int j = 0; j < q.Length; ++j) q[j] = Quaternion.identity;
                frames[f] = new HumanFrame { pelvis_position_m = new Vector3(0, 1, 0), root_rotation_xyzw = Quaternion.identity, body_local_rotation_xyzw = q };
            }
            return new CommonMotionClip
            {
                schema_version = CommonMotionValidation.Version, motion_id = "synthetic_client_check",
                time = new MotionTime { fps = 30, frame_count = 2, time_origin_s = 0 },
                space = new MotionSpace { basis = "unity_lh_y_up_z_forward", length_unit = "meter", frame_id = "synthetic_test" },
                skeleton = new MotionSkeleton { profile_id = "synthetic_test", joint_names = names, parent_indices = parents, rest_offsets_m = new Vector3[22] },
                human = new HumanTrack { frames = frames }, objects = new ObjectTrack[0]
            };
        }
    }
}
