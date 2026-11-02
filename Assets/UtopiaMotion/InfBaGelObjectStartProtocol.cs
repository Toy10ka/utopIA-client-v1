using System;
using System.Text;
using UnityEngine;

namespace Utopia.Motion
{
    [Serializable] public sealed class InfBaGelObjectPose
    {
        public float[] position_m;
        public float[] rotation_xyzw;
    }
    [Serializable] public sealed class InfBaGelObjectStartOverride
    {
        public string coordinate_frame = InfBaGelGoalCoordinates.NativeFrame;
        public string object_name;
        public float[] position_m;
        public float[] rotation_xyzw;
        public string history_mode = "rigid_transform_official_history";
    }
    [Serializable] public sealed class InfBaGelInitialObject
    {
        public string api_version, coordinate_frame, frame_id, scene_id, object_name;
        public int test_item_index;
        public float[] position_m, rotation_xyzw;
        public string initial_state, history_mode;
        public float max_shift_m, max_yaw_deg;
    }
    [Serializable] public sealed class InfBaGelObjectStartMetadata
    {
        public string coordinate_frame, object_name, history_mode;
        public bool overrides_applied, numerical_noop, human_initial_state_changed, scene_changed;
        public InfBaGelObjectPose original, effective;
        public int released_hand_contact_flags;
    }
    [Serializable] public sealed class InfBaGelGenerateWithObjectStartRequest
    {
        public string scene_id;
        public int test_item_index, seed;
        public InfBaGelObjectStartOverride object_start_override;
    }
    [Serializable] public sealed class InfBaGelGenerateWithObjectStartAndGoalsRequest
    {
        public string scene_id;
        public int test_item_index, seed;
        public InfBaGelObjectStartOverride object_start_override;
        public InfBaGelGoalOverrides goal_overrides;
    }

    public static class InfBaGelObjectStartProtocol
    {
        public const string HistoryMode = "rigid_transform_official_history";
        public static Quaternion FromArray(float[] a)
        {
            if(a==null || a.Length!=4) throw new ArgumentException("Expected [x,y,z,w] quaternion.");
            Quaternion q=new Quaternion(a[0],a[1],a[2],a[3]);
            float s=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;
            if(!InfBaGelGoalCoordinates.Finite(s) || Mathf.Abs(s-1f)>0.002f)
                throw new ArgumentException("Initial object rotation must be a finite unit quaternion.");
            return q.normalized;
        }
        public static float[] ToArray(Quaternion q)
        { return new[]{q.x,q.y,q.z,q.w}; }
        // Basis change C=diag(1,1,-1): R_common = C R_native C.
        // A reflection changes the axial part of a quaternion by det(C)*C.
        public static Quaternion MirrorRotation(Quaternion q)
        { return FromArray(new[]{-q.x,-q.y,q.z,q.w}); }
        public static Quaternion NativeToWorldRotation(Transform placement, Quaternion native)
        {
            InfBaGelGoalCoordinates.ValidatePlacement(placement);
            return placement.rotation * MirrorRotation(native);
        }
        public static Quaternion WorldToNativeRotation(Transform placement, Quaternion world)
        {
            InfBaGelGoalCoordinates.ValidatePlacement(placement);
            return MirrorRotation(Quaternion.Inverse(placement.rotation)*world);
        }
        public static string InitialEndpoint(string url,string scene,int index)
        { string e=InfBaGelApiProtocol.CaseGoalsEndpoint(url,scene,index); return e.Substring(0,e.Length-6)+"/initial-object"; }
        public static InfBaGelInitialObject ReadInitial(string json,string scene,int index)
        {
            if(string.IsNullOrWhiteSpace(json)||Encoding.UTF8.GetByteCount(json)>InfBaGelApiProtocol.MaxHealthBytes)
                throw new ArgumentException("Invalid initial-object response.");
            InfBaGelInitialObject s=JsonUtility.FromJson<InfBaGelInitialObject>(json);
            if(s==null || s.coordinate_frame!=InfBaGelGoalCoordinates.NativeFrame || s.scene_id!=scene ||
                s.test_item_index!=index || s.frame_id!="infbagel_scene:"+scene || string.IsNullOrEmpty(s.object_name)||
                s.history_mode!=HistoryMode || s.initial_state!="original_official_case_including_original_heading")
                throw new ArgumentException("Initial object case/frame/history metadata differs. Use API v0.3.");
            InfBaGelGoalCoordinates.FromArray(s.position_m); FromArray(s.rotation_xyzw);
            return s;
        }
        public static InfBaGelObjectStartOverride Validate(InfBaGelObjectStartOverride x)
        {
            if(x==null || x.coordinate_frame!=InfBaGelGoalCoordinates.NativeFrame ||
                string.IsNullOrWhiteSpace(x.object_name) || x.history_mode!=HistoryMode)
                throw new ArgumentException("Missing initial object coordinate/asset/history contract.");
            Vector3 p=InfBaGelGoalCoordinates.FromArray(x.position_m);
            if(Mathf.Max(Mathf.Abs(p.x),Mathf.Abs(p.y),Mathf.Abs(p.z))>1000f)
                throw new ArgumentException("Initial object point exceeds the coordinate limit.");
            return new InfBaGelObjectStartOverride {object_name=x.object_name,
                position_m=InfBaGelGoalCoordinates.ToArray(p),rotation_xyzw=ToArray(FromArray(x.rotation_xyzw))};
        }
        public static string Serialize(InfBaGelGenerateMotionRequest r,InfBaGelGoalOverrides g,InfBaGelObjectStartOverride o)
        {
            if(o==null) return InfBaGelApiProtocol.SerializeRequest(r,g);
            // Do not depend on Unity inline-object null serialization for optional goals.
            if(g==null) return JsonUtility.ToJson(new InfBaGelGenerateWithObjectStartRequest {scene_id=r.scene_id,
                test_item_index=r.test_item_index,seed=r.seed,object_start_override=Validate(o)});
            return JsonUtility.ToJson(new InfBaGelGenerateWithObjectStartAndGoalsRequest {scene_id=r.scene_id,
                test_item_index=r.test_item_index,seed=r.seed,object_start_override=Validate(o),
                goal_overrides=InfBaGelApiProtocol.ValidateGoals(g)});
        }
        public static InfBaGelObjectStartMetadata CheckEcho(string json,InfBaGelObjectStartOverride expected)
        {
            if(expected==null) return null;
            InfBaGelMotionEnvelope e=JsonUtility.FromJson<InfBaGelMotionEnvelope>(json);
            InfBaGelObjectStartMetadata m=e!=null && e.source!=null && e.source.api_generation!=null ?
                e.source.api_generation.object_start_input : null;
            if(m==null || !m.overrides_applied || m.coordinate_frame!=InfBaGelGoalCoordinates.NativeFrame ||
                m.object_name!=expected.object_name || m.history_mode!=HistoryMode || m.effective==null ||
                m.human_initial_state_changed || m.scene_changed)
                throw new ArgumentException("Response does not confirm object-start-only override. Motion not applied.");
            if(Vector3.Distance(InfBaGelGoalCoordinates.FromArray(m.effective.position_m),
                InfBaGelGoalCoordinates.FromArray(expected.position_m))>0.001f ||
                Quaternion.Angle(FromArray(m.effective.rotation_xyzw),FromArray(expected.rotation_xyzw))>0.1f)
                throw new ArgumentException("Server initial object pose differs from sent input. Motion not applied.");
            return m;
        }
    }
}
