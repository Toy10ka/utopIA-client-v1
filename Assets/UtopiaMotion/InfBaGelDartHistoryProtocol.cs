using System;
using UnityEngine;

namespace Utopia.Motion
{
    [Serializable] public sealed class InfBaGelDartBodyProfile
    {
        public string profile_id, gender, body_model_sha256;
        public float[] betas;
        public int[] parent_indices;
        public Vector3[] rest_offsets_m;
    }
    [Serializable] public sealed class InfBaGelDartPoseSample
    {
        public float time_s;
        public InfBaGelHistoryHuman human;
        public Vector3[] reference_joint_positions_m;
    }
    [Serializable] public sealed class InfBaGelRigidPlacement
    {
        public Vector3 translation_m;
        public Quaternion rotation_xyzw;
    }
    [Serializable] public sealed class InfBaGelDartHistory
    {
        public string coordinate_frame = "commonmotion_local_unity_lh_y_up_m";
        public string source_backend = "dart";
        public string source_motion_id, source_frame_id, target_frame_id;
        public float source_fps;
        public int[] source_frames;
        public InfBaGelDartPoseSample[] samples;
        public InfBaGelDartBodyProfile body_profile;
        public InfBaGelRigidPlacement scene_from_source;
        public string object_name;
        public ObjectFrame static_object_pose;
        public string object_history_mode = "static_known_object";
        public string contact_policy = "chois_object4_noncontact_provisional_v1";
        public bool accept_provisional_contact_schema;
        public bool confirmed_no_limb_object_contact;
        public bool confirmed_not_holding_object;
        public string episode_mode = "new_plan_from_captured_state";
    }
    [Serializable] internal sealed class DartSourceFields
    {
        public string backend, gender, body_model_sha256;
        public float[] betas;
    }
    [Serializable] internal sealed class DartSourceEnvelope { public DartSourceFields source; }

    public static class InfBaGelDartHistoryProtocol
    {
        public static InfBaGelDartHistory Capture(CommonMotionClip clip, string json, int endFrame,
            string targetFrame, Matrix4x4 sceneFromSource, string objectName,
            ObjectFrame staticObject, bool confirmUnheld, bool acceptProvisionalSchema = false)
        {
            CommonMotionValidation.Validate(clip);
            DartSourceEnvelope envelope = JsonUtility.FromJson<DartSourceEnvelope>(json);
            DartSourceFields source = envelope == null ? null : envelope.source;
            if (source == null || source.backend != "dart" || clip.objects.Length != 0)
                throw new ArgumentException("Load a DART CommonMotion body-only clip, not an InfBaGel/Animator pose.");
            if (source.gender != "male" || source.betas == null || (source.betas.Length != 10 && source.betas.Length != 16))
                throw new ArgumentException("v0.5 supports the existing male zero-beta DART profile.");
            foreach (float beta in source.betas)
                if (float.IsNaN(beta) || float.IsInfinity(beta) || Mathf.Abs(beta) > 1e-7f)
                    throw new ArgumentException("Do not substitute a new body shape; v0.5 accepts zero-beta only.");
            if (string.IsNullOrEmpty(source.body_model_sha256) || source.body_model_sha256.Length != 64)
                throw new ArgumentException("DART JSON has no source SMPL-X asset hash. Re-export with the existing CommonMotion exporter.");
            if (clip.time.fps != 30f && clip.time.fps != 60f)
                throw new ArgumentException("DART capture currently supports 30 or 60 fps.");
            int stride = Mathf.RoundToInt(clip.time.fps * .1f);
            if (endFrame < stride || endFrame >= clip.time.frame_count)
                throw new ArgumentException("Choose a valid frame with at least 0.1 seconds of prior motion.");
            if (!acceptProvisionalSchema) throw new ArgumentException("Explicitly accept the provisional CHOIS-compatible contact schema. InfBaGel channels 2/3 have NOT been confirmed against distributed labels.");
            if (!confirmUnheld) throw new ArgumentException("Confirm BOTH hands and BOTH feet are clear of the static object at both times. Holding, kicking and standing on the object are outside this test.");
            Quaternion q = sceneFromSource.rotation;
            Vector3 scale = sceneFromSource.lossyScale;
            if (Vector3.Distance(scale, Vector3.one) > 1e-4f || Vector3.Distance(q * Vector3.up, Vector3.up) > 1e-4f || sceneFromSource.determinant < .999f)
                throw new ArgumentException("Only rigid yaw/translation placement is supported. Remove scale, tilt and shear.");
            int[] indices = { endFrame - stride, endFrame };
            InfBaGelDartPoseSample[] samples = new InfBaGelDartPoseSample[2];
            for (int n = 0; n < 2; ++n)
            {
                HumanFrame h = clip.human.frames[indices[n]];
                if (h.reference_joint_positions_m == null || h.reference_joint_positions_m.Length != 22)
                    throw new ArgumentException("DART reference positions are required for independent SMPL-X verification.");
                samples[n] = new InfBaGelDartPoseSample {
                    time_s = indices[n] / clip.time.fps,
                    human = new InfBaGelHistoryHuman { pelvis_position_m = h.pelvis_position_m,
                        root_rotation_xyzw = h.root_rotation_xyzw,
                        body_local_rotation_xyzw = (Quaternion[])h.body_local_rotation_xyzw.Clone() },
                    reference_joint_positions_m = (Vector3[])h.reference_joint_positions_m.Clone()
                };
            }
            return new InfBaGelDartHistory {
                source_motion_id = clip.motion_id, source_frame_id = clip.space.frame_id,
                target_frame_id = targetFrame, source_fps = clip.time.fps, source_frames = indices,
                samples = samples, object_name = objectName, static_object_pose = staticObject,
                body_profile = new InfBaGelDartBodyProfile { profile_id = clip.skeleton.profile_id,
                    gender = source.gender, betas = (float[])source.betas.Clone(), body_model_sha256 = source.body_model_sha256,
                    parent_indices = (int[])clip.skeleton.parent_indices.Clone(),
                    rest_offsets_m = (Vector3[])clip.skeleton.rest_offsets_m.Clone() },
                scene_from_source = new InfBaGelRigidPlacement {
                    translation_m = sceneFromSource.MultiplyPoint3x4(Vector3.zero), rotation_xyzw = q },
                confirmed_not_holding_object = true,
                confirmed_no_limb_object_contact = true,
                accept_provisional_contact_schema = true
            };
        }

        public static InfBaGelHumanHistory Expected(InfBaGelDartHistory d)
        {
            Quaternion q = d.scene_from_source.rotation_xyzw;
            Vector3 p = d.scene_from_source.translation_m;
            InfBaGelHistorySample[] samples = new InfBaGelHistorySample[2];
            for (int n = 0; n < 2; ++n)
            {
                InfBaGelHistoryHuman h = d.samples[n].human;
                samples[n] = new InfBaGelHistorySample { time_s = d.samples[n].time_s,
                    human = new InfBaGelHistoryHuman { pelvis_position_m = q * h.pelvis_position_m + p,
                        root_rotation_xyzw = q * h.root_rotation_xyzw,
                        body_local_rotation_xyzw = (Quaternion[])h.body_local_rotation_xyzw.Clone() },
                    @object = new ObjectFrame { position_m = d.static_object_pose.position_m,
                        rotation_xyzw = d.static_object_pose.rotation_xyzw } };
            }
            return new InfBaGelHumanHistory { frame_id = d.target_frame_id, source_motion_id = d.source_motion_id,
                body_profile_id = d.body_profile.profile_id, source_frames = (int[])d.source_frames.Clone(),
                object_name = d.object_name, samples = samples, contact_policy = d.contact_policy };
        }

        public static string Serialize(InfBaGelGenerateMotionRequest basic, InfBaGelGoalOverrides goals,
            InfBaGelDartHistory history, string text)
        {
            string json = InfBaGelHumanHistoryProtocol.Serialize(basic, goals, null, null, text);
            string result = json.Substring(0, json.Length - 1) + ",\"dart_history_override\":" + JsonUtility.ToJson(history) + "}";
            if (System.Text.Encoding.UTF8.GetByteCount(result) > 131072)
                throw new ArgumentException("DART history exceeds 128 KiB. Only two source frames should be sent.");
            return result;
        }
    }
}
