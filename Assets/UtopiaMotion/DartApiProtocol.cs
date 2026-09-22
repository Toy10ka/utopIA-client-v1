using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Utopia.Motion
{
    // Matches DART_API_v0_1/dart_api/contracts.py. Not a motion_plan/router.
    [Serializable]
    public sealed class DartGenerateMotionRequest
    {
        public string text;
        public float duration_s;
        public int seed;
        public float guidance;
    }

    [Serializable]
    public sealed class DartApiHealth
    {
        public string status;
        public bool busy;
        public string api_version;
    }

    /// <summary>Wire contract and validation. Contains no inference or retarget logic.</summary>
    public static class DartApiProtocol
    {
        public const int MaxResponseBytes = 16 * 1024 * 1024;
        public const int MaxResponseFrames = 6000;
        public const int MaxHealthBytes = 64 * 1024;

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

        public static DartGenerateMotionRequest Request(string text, float duration, int seed, float guidance)
        {
            if (text == null || text.Length < 1 || text.Length > 200)
                throw new ArgumentException("Prompt must contain 1-200 characters.");
            foreach (char c in text)
                if (c < 32 || c == 127 || c == ',' || c == ';' || c == '*' || c == '/' || c == '\\')
                    throw new ArgumentException("Use one action description, without , ; * / \\ or control characters. Set Duration S separately.");
            text = Regex.Replace(text.Trim(), @"\s+", " ");
            if (text.Length == 0) throw new ArgumentException("Prompt must not be blank.");
            if (!Finite(duration) || duration < 0.1f || duration > 20f)
                throw new ArgumentException("Duration S must be between 0.1 and 20 seconds.");
            if (seed < 0) throw new ArgumentException("Seed must be between 0 and 2147483647.");
            if (!Finite(guidance) || guidance < 0f || guidance > 10f)
                throw new ArgumentException("Guidance must be between 0 and 10.");
            return new DartGenerateMotionRequest { text = text, duration_s = duration, seed = seed, guidance = guidance };
        }

        public static bool IsJsonContentType(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                string.Equals(value.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase);
        }

        // Validate before replacing the player's current motion. Uses the existing validator.
        public static CommonMotionClip ReadMotion(string json, string headerMotionId)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("API returned an empty response.");
            if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
                throw new ArgumentException("API response is larger than 16 MiB.");
            CommonMotionClip clip = JsonUtility.FromJson<CommonMotionClip>(json);
            if (clip == null || clip.time == null || clip.time.frame_count > MaxResponseFrames)
                throw new ArgumentException("Response is not a supported CommonMotion document or exceeds the frame limit.");
            CommonMotionValidation.Validate(clip);
            if (string.IsNullOrWhiteSpace(clip.motion_id) || clip.motion_id.Length > 160)
                throw new ArgumentException("Missing/invalid motion_id.");
            foreach (char c in clip.motion_id)
                if (char.IsControl(c)) throw new ArgumentException("Invalid motion_id.");
            if (!string.IsNullOrEmpty(headerMotionId) &&
                !string.Equals(headerMotionId, clip.motion_id, StringComparison.Ordinal))
                throw new ArgumentException("X-Motion-ID and CommonMotion.motion_id disagree.");
            return clip;
        }

        public static DartApiHealth ReadHealth(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxHealthBytes)
                throw new ArgumentException("Invalid health response.");
            DartApiHealth health = JsonUtility.FromJson<DartApiHealth>(json);
            if (health == null || health.status != "ready")
                throw new ArgumentException("Health response does not report status=ready.");
            return health;
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
                case 0: hint = "Cannot reach server / timeout. Check API container and host port 8001."; break;
                case 401: hint = "Server requires a Bearer token."; break;
                case 404: hint = "Endpoint not found. Use the DART API base URL, not utopia-server:5000."; break;
                case 413: hint = "Request is too large."; break;
                case 415: hint = "Server requires application/json."; break;
                case 422: hint = "Input validation failed. Check Prompt / Duration S / Seed / Guidance."; break;
                case 429: hint = "DART is busy. Wait for its current generation to finish; no automatic retry was sent."; break;
                case 500: hint = "DART generation failed. Check the server's [DART_API] FAILED log."; break;
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

        private static bool Finite(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }
    }
}
