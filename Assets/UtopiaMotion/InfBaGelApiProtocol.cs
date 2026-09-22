using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Utopia.Motion
{
    // InfBaGel_API_v0_1/infbagel_api/contracts.py. Selects one official case;
    // does NOT send arbitrary text, avatar history, object transforms or scene geometry.
    [Serializable]
    public sealed class InfBaGelGenerateMotionRequest
    {
        public string scene_id;
        public int test_item_index;
        public int seed;
    }

    [Serializable]
    public sealed class InfBaGelApiHealth
    {
        public string status;
        public bool busy;
        public string api_version;
        public InfBaGelApiRuntime runtime;
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
    [Serializable] public sealed class InfBaGelMotionSource
    {
        public string backend;
        public InfBaGelGenerationMetadata api_generation;
    }
    [Serializable] public sealed class InfBaGelGenerationMetadata
    {
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
                case 404: hint = "Scene/item or endpoint not found. Check Scene ID / zero-based index; GET /cases lists available cases."; break;
                case 409: hint = "Selected case data/configuration is incompatible or exceeds limits. Check [INFBAGEL_API] server logs."; break;
                case 413: hint = "Request is too large."; break;
                case 415: hint = "Server requires application/json."; break;
                case 422: hint = "Input validation failed. Use scene_id / test_item_index / seed, not DART text/duration fields."; break;
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
