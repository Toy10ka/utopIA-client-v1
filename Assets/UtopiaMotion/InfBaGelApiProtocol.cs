using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Utopia.Motion
{
    // InfBaGel_API_v0_1/infbagel_api/contracts.py. Selects one official case;
    // v0.2 optionally sends explicit goal points; it still does NOT send arbitrary text,
    // current avatar history, initial object transforms or scene geometry.
    [Serializable]
    public sealed class InfBaGelGenerateMotionRequest
    {
        public string scene_id;
        public int test_item_index;
        public int seed;
    }

    [Serializable]
    public sealed class InfBaGelGoalOverrides
    {
        public string coordinate_frame = InfBaGelGoalCoordinates.NativeFrame;
        public float[] pelvis_goal;
        public float[] object_goal;
    }
    [Serializable]
    public sealed class InfBaGelGenerateWithGoalsRequest
    {
        public string scene_id;
        public int test_item_index;
        public int seed;
        public InfBaGelGoalOverrides goal_overrides;
    }
    [Serializable]
    public sealed class InfBaGelCaseGoals
    {
        public string api_version;
        public string scene_id;
        public int test_item_index;
        public string frame_id;
        public string coordinate_frame;
        public string object_name;
        public int data_idx;
        public float[] start_location;
        public float[] pelvis_goal;
        public float[] object_goal;
        public string initial_state;
    }
    [Serializable] public sealed class InfBaGelEffectiveGoals { public float[] pelvis_goal; public float[] object_goal; }
    [Serializable]
    public sealed class InfBaGelGoalInputMetadata
    {
        public string coordinate_frame;
        public bool overrides_applied;
        public InfBaGelEffectiveGoals original;
        public InfBaGelEffectiveGoals effective;
        public string initial_state;
        public float[] initial_alignment_pelvis_goal;
    }
    [Serializable] public sealed class InfBaGelApiCapabilities { public bool goal_overrides; public bool case_goals; }

    [Serializable]
    public sealed class InfBaGelApiHealth
    {
        public string status;
        public bool busy;
        public string api_version;
        public InfBaGelApiRuntime runtime;
        public InfBaGelApiCapabilities capabilities;
    }
    [Serializable]
    public sealed class InfBaGelApiRuntime
    {
        public string backend;
        public string mode;
    }

    // Read only the identifying metadata added by the existing server. This does not
    // change CommonMotionClip or make the shared player depend on InfBaGel.
    [Serializable] public sealed class InfBaGelMotionEnvelope { public InfBaGelMotionSource source; }
    [Serializable]
    public sealed class InfBaGelMotionSource
    {
        public string backend;
        public InfBaGelGenerationMetadata api_generation;
    }
    [Serializable]
    public sealed class InfBaGelGenerationMetadata
    {
        public InfBaGelGoalInputMetadata goal_input;
        public string mode;
        public string scene_id;
        public int test_item_index;
        public int seed;
    }

    public static class InfBaGelApiProtocol
    {
        public const string DefaultSceneId = "00add26c-7a26-4a61-b192-b97aa493b3f3";
        public const int MaxResponseBytes = 64 * 1024 * 1024;
        public const int MaxResponseFrames = 6000;
        public const int MaxHealthBytes = 64 * 1024;
        private static readonly Regex SceneIdPattern = new Regex(@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,127}\z", RegexOptions.CultureInvariant);
        private static readonly Regex MotionIdPattern = new Regex(@"\Ainfbagel_[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

        public static string Endpoint(string baseUrl, string relativePath)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(baseUrl) ||
                !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("API Base URL must be an http(s) base URL without credentials, query or fragment.");
            if (relativePath != "health" && relativePath != "generate_motion")
                throw new ArgumentException("Unsupported API endpoint.");
            return uri.AbsoluteUri.TrimEnd('/') + "/" + relativePath;
        }

        public static InfBaGelGenerateMotionRequest Request(string sceneId, int itemIndex, int seed)
        {
            // No paths or .json extension. Fail visibly rather than normalize another case.
            if (sceneId == null || !SceneIdPattern.IsMatch(sceneId))
                throw new ArgumentException("Scene ID must be 1-128 ASCII letters/digits/_/-, start with a letter/digit, and contain no .json extension or spaces.");
            if (itemIndex < 0 || itemIndex > 100000)
                throw new ArgumentException("Test Item Index must be between 0 and 100000 (zero-based). Actual case availability is checked by the server.");
            if (seed < 0)
                throw new ArgumentException("Seed must be between 0 and 2147483647.");
            return new InfBaGelGenerateMotionRequest { scene_id = sceneId, test_item_index = itemIndex, seed = seed };
        }

        public static string CaseGoalsEndpoint(string baseUrl, string scene, int itemIndex)
        {
            Request(scene, itemIndex, 0);
            // Endpoint() validates the base URL; replace only the final fixed route.
            string health = Endpoint(baseUrl, "health");
            return health.Substring(0, health.Length - "health".Length) + "cases/" +
                Uri.EscapeDataString(scene) + "/items/" + itemIndex.ToString(CultureInfo.InvariantCulture) + "/goals";
        }

        public static InfBaGelGoalOverrides ValidateGoals(InfBaGelGoalOverrides goals)
        {
            if (goals == null || goals.coordinate_frame != InfBaGelGoalCoordinates.NativeFrame)
                throw new ArgumentException("Goals must use infbagel_scene_y_up_m.");
            Vector3 pelvis = InfBaGelGoalCoordinates.FromArray(goals.pelvis_goal);
            Vector3 obj = InfBaGelGoalCoordinates.FromArray(goals.object_goal);
            if (pelvis.y != 0f) throw new ArgumentException("Pelvis Goal marker represents a ground-plane destination (native y=0), not hip height.");
            foreach (float x in goals.pelvis_goal) if (Mathf.Abs(x) > 1000f) throw new ArgumentException("Goal exceeds the API coordinate limit.");
            foreach (float x in goals.object_goal) if (Mathf.Abs(x) > 1000f) throw new ArgumentException("Goal exceeds the API coordinate limit.");
            // Snapshot values so later marker movement cannot mutate an in-flight request.
            return new InfBaGelGoalOverrides
            {
                coordinate_frame = goals.coordinate_frame,
                pelvis_goal = InfBaGelGoalCoordinates.ToArray(pelvis),
                object_goal = InfBaGelGoalCoordinates.ToArray(obj)
            };
        }

        public static string SerializeRequest(InfBaGelGenerateMotionRequest request, InfBaGelGoalOverrides goals)
        {
            if (goals == null) return JsonUtility.ToJson(request); // v0.1 wire format stays byte-for-byte structurally compatible.
            return JsonUtility.ToJson(new InfBaGelGenerateWithGoalsRequest
            {
                scene_id = request.scene_id,
                test_item_index = request.test_item_index,
                seed = request.seed,
                goal_overrides = ValidateGoals(goals)
            });
        }

        public static InfBaGelCaseGoals ReadCaseGoals(string json, string scene, int itemIndex)
        {
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxHealthBytes)
                throw new ArgumentException("Invalid case-goals response.");
            InfBaGelCaseGoals result = JsonUtility.FromJson<InfBaGelCaseGoals>(json);
            if (result == null || result.scene_id != scene || result.test_item_index != itemIndex ||
                result.frame_id != "infbagel_scene:" + scene || result.coordinate_frame != InfBaGelGoalCoordinates.NativeFrame ||
                result.initial_state != "original_official_case_including_original_heading" || string.IsNullOrWhiteSpace(result.object_name))
                throw new ArgumentException("Case/frame metadata differs from the request. Check that the server is API v0.2.");
            InfBaGelGoalCoordinates.FromArray(result.start_location);
            ValidateGoals(new InfBaGelGoalOverrides { pelvis_goal = result.pelvis_goal, object_goal = result.object_goal });
            return result;
        }

        public static void CheckGoalEcho(string json, InfBaGelGoalOverrides expected)
        {
            if (expected == null) return;
            InfBaGelMotionEnvelope envelope = JsonUtility.FromJson<InfBaGelMotionEnvelope>(json);
            InfBaGelGoalInputMetadata goals = envelope != null && envelope.source != null && envelope.source.api_generation != null ?
                envelope.source.api_generation.goal_input : null;
            if (goals == null || !goals.overrides_applied || goals.effective == null || goals.original == null ||
                goals.coordinate_frame != InfBaGelGoalCoordinates.NativeFrame ||
                goals.initial_state != "original_official_case_including_original_heading")
                throw new ArgumentException("Response does not confirm goal overrides with fixed initial state. Motion was not applied.");
            float tolerance = InfBaGelGoalCoordinates.PositionToleranceM;
            if (Vector3.Distance(InfBaGelGoalCoordinates.FromArray(goals.effective.pelvis_goal), InfBaGelGoalCoordinates.FromArray(expected.pelvis_goal)) > tolerance ||
                Vector3.Distance(InfBaGelGoalCoordinates.FromArray(goals.effective.object_goal), InfBaGelGoalCoordinates.FromArray(expected.object_goal)) > tolerance ||
                Vector3.Distance(InfBaGelGoalCoordinates.FromArray(goals.initial_alignment_pelvis_goal), InfBaGelGoalCoordinates.FromArray(goals.original.pelvis_goal)) > tolerance)
                throw new ArgumentException("Server effective goals/initial heading disagree with the request. Motion was not applied.");
        }

        public static bool IsJsonContentType(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                string.Equals(value.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase);
        }

        public static InfBaGelApiHealth ReadHealth(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxHealthBytes)
                throw new ArgumentException("Invalid health response.");
            InfBaGelApiHealth health = JsonUtility.FromJson<InfBaGelApiHealth>(json);
            if (health == null || health.status != "ready")
                throw new ArgumentException("Health response does not report status=ready.");
            if (health.runtime == null || health.runtime.backend != "infbagel" || health.runtime.mode != "official_hosi_case")
                throw new ArgumentException("This is not the InfBaGel official-case API. Check host port 8002 (DART uses 8001).");
            return health;
        }

        /// <summary>Checks identity, shared skeleton/mesh/time contract, and selected case before replacing playback.</summary>
        public static CommonMotionClip ReadMotion(string json, string headerMotionId, InfBaGelGenerateMotionRequest expected)
        {
            if (expected == null) throw new ArgumentException("Missing expected case request.");
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("API returned an empty response.");
            if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
                throw new ArgumentException("API response is larger than 64 MiB.");
            CommonMotionClip clip = JsonUtility.FromJson<CommonMotionClip>(json);
            if (clip == null || clip.time == null || clip.time.frame_count > MaxResponseFrames)
                throw new ArgumentException("Response is not a supported CommonMotion document or exceeds the frame limit.");
            CommonMotionValidation.Validate(clip);
            if (clip.space.frame_id != "infbagel_scene:" + expected.scene_id)
                throw new ArgumentException("Returned CommonMotion frame_id differs from the selected scene.");
            if (clip.motion_id == null || !MotionIdPattern.IsMatch(clip.motion_id))
                throw new ArgumentException("Missing/invalid InfBaGel motion_id.");
            if (!string.IsNullOrEmpty(headerMotionId) && !string.Equals(headerMotionId, clip.motion_id, StringComparison.Ordinal))
                throw new ArgumentException("X-Motion-ID and CommonMotion.motion_id disagree.");
            // The v0.1 server explicitly generates one manipulated rigid object with a mesh.
            if (clip.objects.Length != 1 || clip.objects[0].mesh == null || string.IsNullOrWhiteSpace(clip.objects[0].asset_id))
                throw new ArgumentException("Expected one object track with its embedded mesh. No partial human-only playback was applied.");
            if (clip.objects[0].mesh.vertices_m.Length > 500000 || clip.objects[0].mesh.triangles.Length > 3000000)
                throw new ArgumentException("Embedded object mesh exceeds the preview limits.");
            InfBaGelMotionEnvelope envelope = JsonUtility.FromJson<InfBaGelMotionEnvelope>(json);
            InfBaGelGenerationMetadata gen = envelope != null && envelope.source != null ? envelope.source.api_generation : null;
            if (gen == null || envelope.source.backend != "infbagel" || gen.mode != "official_hosi_case")
                throw new ArgumentException("Missing/unsupported InfBaGel API generation metadata.");
            if (gen.scene_id != expected.scene_id || gen.test_item_index != expected.test_item_index || gen.seed != expected.seed)
                throw new ArgumentException("Returned case or seed differs from the requested case. Motion was not applied.");
            return clip;
        }

        public static double ReadMilliseconds(string value)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
                   !double.IsNaN(parsed) && !double.IsInfinity(parsed) && parsed >= 0 ? parsed : -1;
        }

        public static string HttpError(long status, string transportError, string body)
        {
            string hint;
            switch (status)
            {
                case 0: hint = "Cannot reach server / timeout. Check the InfBaGel API container and host port 8002."; break;
                case 401: hint = "Server requires a Bearer token."; break;
                case 404: hint = "Scene/item or endpoint not found. Check Scene ID / zero-based index; GET /cases lists cases. For /goals, update the server image to v0.2."; break;
                case 409: hint = "Selected case/goals are incompatible, out of scene bounds, unreachable, or exceed limits. Check [INFBAGEL_API] server logs."; break;
                case 413: hint = "Request is too large."; break;
                case 415: hint = "Server requires application/json."; break;
                case 422: hint = "Input validation failed. Use scene_id / test_item_index / seed and optional v0.2 goal_overrides, not DART text/duration fields. Old v0.1 servers reject goal_overrides."; break;
                case 429: hint = "InfBaGel is busy. Wait for the current generation; no automatic retry was sent."; break;
                case 500: hint = "InfBaGel generation failed. Check the server's [INFBAGEL_API] FAILED log."; break;
                default: hint = "HTTP request failed."; break;
            }
            string detail = string.IsNullOrWhiteSpace(body) ? transportError : body;
            return "HTTP " + status + ": " + hint + " " + ShortDetail(detail);
        }

        public static string ShortDetail(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Substring(0, Math.Min(text.Length, 600));
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
    }
}
