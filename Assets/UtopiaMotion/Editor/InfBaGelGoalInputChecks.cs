using System;
using UnityEditor;
using UnityEngine;

namespace Utopia.Motion.Editor
{
    public static class InfBaGelGoalInputChecks
    {
        [MenuItem("Tools/utopIA/Run InfBaGel Goal Input Checks")]
        public static void Run()
        {
            GameObject parent=new GameObject("goal-test-parent");
            GameObject child=new GameObject("goal-test-placement");
            try
            {
                child.transform.SetParent(parent.transform,false);
                parent.transform.SetPositionAndRotation(new Vector3(2,0.4f,-3),Quaternion.Euler(0,90,0));
                child.transform.localPosition=new Vector3(0.7f,0.2f,0.1f);
                child.transform.localRotation=Quaternion.Euler(0,-35,0);
                Vector3 native=new Vector3(-1.2f,0.813529f,1.293221f);
                Vector3 world=InfBaGelGoalCoordinates.NativeToWorld(child.transform,native);
                Vector3 recovered=InfBaGelGoalCoordinates.WorldToNative(child.transform,world);
                Require(Vector3.Distance(native,recovered)<0.00001f,"round-trip with translated/yaw parent");
                Require(InfBaGelGoalCoordinates.NativeToCommon(new Vector3(1,2,3))==new Vector3(1,2,-3),"Z reflection");
                Vector3 movedWorld=world+child.transform.TransformVector(new Vector3(0,0,0.1f));
                Vector3 movedNative=InfBaGelGoalCoordinates.WorldToNative(child.transform,movedWorld);
                Require(Mathf.Abs(movedNative.z-(native.z-0.1f))<0.00001f,"common +Z means native -Z");
                Matrix4x4 m=child.transform.localToWorldMatrix;
                Require(InfBaGelGoalCoordinates.SamePlacement(m,m),"same placement");
                Matrix4x4 changed=m; changed[0,3]+=0.1f;
                Require(!InfBaGelGoalCoordinates.SamePlacement(m,changed),"changed placement detected");
                child.transform.localScale=new Vector3(2,1,1);
                Reject(()=>InfBaGelGoalCoordinates.ValidatePlacement(child.transform),"nonunit scale");
                child.transform.localScale=Vector3.one;
                child.transform.localRotation=Quaternion.Euler(10,0,0);
                Reject(()=>InfBaGelGoalCoordinates.ValidatePlacement(child.transform),"pitch");
                child.transform.localRotation=Quaternion.identity;
                parent.transform.localScale=new Vector3(-1,1,1);
                Reject(()=>InfBaGelGoalCoordinates.ValidatePlacement(child.transform),"reflection");
                InfBaGelGenerateMotionRequest request=InfBaGelApiProtocol.Request(InfBaGelApiProtocol.DefaultSceneId,0,0);
                InfBaGelGoalOverrides goal=new InfBaGelGoalOverrides {pelvis_goal=new[]{-1.2f,0f,1.54f},object_goal=new[]{-1.28526f,0.813529f,1.293221f}};
                string json=InfBaGelApiProtocol.SerializeRequest(request,goal);
                InfBaGelGenerateWithGoalsRequest decoded=JsonUtility.FromJson<InfBaGelGenerateWithGoalsRequest>(json);
                Require(decoded.goal_overrides!=null && decoded.goal_overrides.coordinate_frame==InfBaGelGoalCoordinates.NativeFrame,"wire frame name");
                Require(decoded.goal_overrides.object_goal.Length==3,"wire arrays");
                string legacy=InfBaGelApiProtocol.SerializeRequest(request,null);
                Require(!legacy.Contains("goal_overrides"),"legacy body unchanged");
                InfBaGelGoalOverrides frozen=InfBaGelApiProtocol.ValidateGoals(goal);
                goal.object_goal[0]+=1;
                Require(frozen.object_goal[0]!=goal.object_goal[0],"request copy");
                goal.pelvis_goal[1]=1;
                Reject(()=>InfBaGelApiProtocol.ValidateGoals(goal),"pelvis ground projection");
                Debug.Log("[INFBAGEL_GOAL_CHECKS] PASS: 13 local checks. No GPU/network/motion-quality test was run.");
            }
            catch(Exception e){Debug.LogError("[INFBAGEL_GOAL_CHECKS] FAIL: "+e);}
            finally {UnityEngine.Object.DestroyImmediate(child);UnityEngine.Object.DestroyImmediate(parent);}
        }
        private static void Require(bool value,string label){if(!value)throw new Exception(label);}
        private static void Reject(Action action,string label)
        {
            bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}
            Require(rejected,"expected rejection: "+label);
        }
    }
}
