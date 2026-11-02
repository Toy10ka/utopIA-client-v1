using System;
using System.Text;
using UnityEngine;

namespace Utopia.Motion
{
    [Serializable] public sealed class InfBaGelHistoryHuman
    {
        public Vector3 pelvis_position_m;
        public Quaternion root_rotation_xyzw;
        public Quaternion[] body_local_rotation_xyzw;
    }
    [Serializable] public sealed class InfBaGelHistorySample
    {
        public float time_s;
        public InfBaGelHistoryHuman human;
        public ObjectFrame @object;
    }
    [Serializable] public sealed class InfBaGelHumanHistory
    {
        public string coordinate_frame = "commonmotion_local_unity_lh_y_up_m";
        public string frame_id;
        public string source_motion_id;
        public string body_profile_id;
        public int[] source_frames;
        public string object_name;
        public InfBaGelHistorySample[] samples;
        public string contact_policy = "same_source_motion_raw4";
        public string episode_mode = "new_plan_from_captured_state";
    }
    [Serializable] public sealed class InfBaGelHistorySupport
    {
        public bool available;
        public int frame_stride;
        public float interval_s;
        public string resampling;
    }
    [Serializable] public sealed class InfBaGelHistoryOutputCheck { public bool passed; }
    [Serializable] public sealed class InfBaGelHistoryReport
    {
        public bool applied;
        public string source_motion_id;
        public int[] source_frames;
        public float history_interval_s;
        public string body_profile_id;
        public string contact_policy;
        public int play_from_frame;
        public float core_forward_max_error_m;
        public float rest_offsets_max_error_m;
        public InfBaGelHistoryOutputCheck output_check;
    }
    [Serializable] public sealed class InfBaGelHistoryValidation
    {
        public string status;
        public bool no_inference;
        public InfBaGelHistoryReport report;
    }
    [Serializable] public sealed class InfBaGelTextReport
    {
        public bool applied;
        public string effective_text;
        public string encoder;
        public float original_feature_cosine;
    }
    [Serializable] internal sealed class InfBaGelTextField { public string text_override; }

    public static class InfBaGelHumanHistoryProtocol
    {
        public static string Serialize(InfBaGelGenerateMotionRequest basic, InfBaGelGoalOverrides goals,
            InfBaGelObjectStartOverride start, InfBaGelHumanHistory history, string text)
        {
            if (history != null && start != null)
                throw new ArgumentException("Turn Use Object Start Override OFF. Captured history contains synchronized object history.");
            string json = InfBaGelObjectStartProtocol.Serialize(basic, goals, start);
            string suffix = "";
            if (history != null) suffix += ",\"human_history_override\":" + JsonUtility.ToJson(history);
            if (text != null)
            {
                if (string.IsNullOrWhiteSpace(text) || text.Length > 512)
                    throw new ArgumentException("Instruction must be non-empty and <=512 characters.");
                string field = JsonUtility.ToJson(new InfBaGelTextField { text_override = text });
                suffix += "," + field.Substring(1, field.Length - 2);
            }
            string result = json.Substring(0, json.Length - 1) + suffix + "}";
            if (Encoding.UTF8.GetByteCount(result) > 131072)
                throw new ArgumentException("Human history request exceeds 128 KiB.");
            return result;
        }

        public static InfBaGelGenerationMetadata Metadata(string json)
        {
            InfBaGelMotionEnvelope envelope = JsonUtility.FromJson<InfBaGelMotionEnvelope>(json);
            if (envelope == null || envelope.source == null || envelope.source.api_generation == null)
                throw new ArgumentException("Missing API source metadata.");
            return envelope.source.api_generation;
        }

        public static InfBaGelHumanHistory Capture(CommonMotionClip clip, string json, string scene, int item, int endFrame)
        {
            if (clip == null || clip.space.frame_id != "infbagel_scene:" + scene || clip.time.fps != 30f)
                throw new ArgumentException("Load a 30fps InfBaGel motion from this same scene.");
            InfBaGelGenerationMetadata metadata = Metadata(json);
            InfBaGelHistorySupport support = metadata.history_support;
            if (metadata.scene_id != scene || metadata.test_item_index != item || support == null ||
                !support.available || support.frame_stride != 3 || Mathf.Abs(support.interval_s - .1f) > 1e-6f ||
                support.resampling != "uniform_10_to_30_hold_last_v1")
                throw new ArgumentException("Generate a NEW official case using API v0.4 first. v0.3/DART clips lack the matching raw contact history.");
            if (endFrame < 3 || endFrame > clip.time.frame_count - 3 || endFrame % 3 != 0)
                throw new ArgumentException("History end must be a valid multiple-of-3 frame >=3 before the held tail.");
            if (clip.objects == null || clip.objects.Length != 1)
                throw new ArgumentException("Expected one matching object track.");
            int[] indices = { endFrame - 3, endFrame };
            InfBaGelHistorySample[] samples = new InfBaGelHistorySample[2];
            for (int n = 0; n < 2; ++n)
            {
                HumanFrame h = clip.human.frames[indices[n]];
                ObjectFrame o = clip.objects[0].frames[indices[n]];
                samples[n] = new InfBaGelHistorySample {
                    time_s = indices[n] / clip.time.fps,
                    human = new InfBaGelHistoryHuman { pelvis_position_m = h.pelvis_position_m,
                        root_rotation_xyzw = h.root_rotation_xyzw,
                        body_local_rotation_xyzw = (Quaternion[])h.body_local_rotation_xyzw.Clone() },
                    @object = new ObjectFrame { position_m = o.position_m, rotation_xyzw = o.rotation_xyzw }
                };
            }
            return new InfBaGelHumanHistory { frame_id = clip.space.frame_id, source_motion_id = clip.motion_id,
                body_profile_id = clip.skeleton.profile_id, object_name = clip.objects[0].asset_id,
                source_frames = indices, samples = samples };
        }

        public static void CheckReport(InfBaGelHistoryReport report, InfBaGelHumanHistory expected)
        {
            if (report == null || !report.applied || report.source_motion_id != expected.source_motion_id ||
                report.body_profile_id != expected.body_profile_id || report.contact_policy != expected.contact_policy ||
                report.source_frames == null || report.source_frames.Length != 2 ||
                report.source_frames[0] != expected.source_frames[0] || report.source_frames[1] != expected.source_frames[1] ||
                Mathf.Abs(report.history_interval_s - .1f) > 1e-6f)
                throw new ArgumentException("Human-history report disagrees with the request.");
        }

        public static void CheckOutput(CommonMotionClip clip, string json, InfBaGelHumanHistory expected, string expectedText)
        {
            InfBaGelGenerationMetadata m = Metadata(json);
            if (expectedText != null && (m.text_input == null || !m.text_input.applied || m.text_input.effective_text != expectedText))
                throw new ArgumentException("Server did not acknowledge the sent text.");
            if (expected == null) return;
            CheckReport(m.human_history_input, expected);
            if (m.human_history_input.output_check == null || !m.human_history_input.output_check.passed ||
                m.human_history_input.play_from_frame != 3)
                throw new ArgumentException("Server history preservation check failed or playback boundary is missing.");
            // Independent client-side comparison; do not trust an echo label alone.
            for (int n = 0; n < 2; ++n)
            {
                int i = n * 3;
                HumanFrame h = clip.human.frames[i]; ObjectFrame o = clip.objects[0].frames[i];
                InfBaGelHistorySample e = expected.samples[n];
                if (Vector3.Distance(h.pelvis_position_m, e.human.pelvis_position_m) > .001f ||
                    Quaternion.Angle(h.root_rotation_xyzw, e.human.root_rotation_xyzw) > .1f ||
                    Vector3.Distance(o.position_m, e.@object.position_m) > .001f ||
                    Quaternion.Angle(o.rotation_xyzw, e.@object.rotation_xyzw) > .1f)
                    throw new ArgumentException("Fixed human/object history does not match at output frame " + i);
                for (int j = 0; j < 21; ++j)
                    if (Quaternion.Angle(h.body_local_rotation_xyzw[j], e.human.body_local_rotation_xyzw[j]) > .1f)
                        throw new ArgumentException("Fixed joint rotation differs at frame " + i + " joint " + (j+1));
            }
        }
    }
}
