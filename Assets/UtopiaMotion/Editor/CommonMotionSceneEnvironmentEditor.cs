using UnityEditor;
using UnityEngine;
namespace Utopia.Motion.Editor
{
    [CustomEditor(typeof(CommonMotionSceneEnvironment))]
    public sealed class CommonMotionSceneEnvironmentEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();var c=(CommonMotionSceneEnvironment)target;
            EditorGUILayout.HelpBox("Static upright BoxColliders only. Room Frame is a fixed Empty at the FLOOR TOP; not the Player. Floor is implicit Y=0 and is NOT in Background Boxes. Exclude the avatar, manipulated object and preview markers. Re-register after any room change.",MessageType.Info);
            using(new EditorGUI.DisabledScope(!UnityEngine.Application.isPlaying || !c.isActiveAndEnabled || c.IsBusy))
            {
                if(GUILayout.Button("Copy remembered InfBaGel placement to Room Frame (explicit)"))c.CopyRememberedFrame();
                if(GUILayout.Button("Register / Update static Unity room"))c.RegisterRoom();
                if(GUILayout.Button("Fetch template goals / Reset room markers (no generation)"))c.CreateGoalsFromCase();
                if(GUILayout.Button("Query Probe Points (no generation)"))c.QueryProbes();
                if(GUILayout.Button("Preview room A* path (no generation)"))c.PreviewPath();
            }
            EditorGUILayout.HelpBox(c.Status,string.IsNullOrEmpty(c.LastError)?MessageType.Info:MessageType.Error);
            if(c.Registered){EditorGUILayout.LabelField("Environment",c.Reference.environment_id);EditorGUILayout.LabelField("Revision",c.Reference.revision.Substring(0,16));}
            if(UnityEngine.Application.isPlaying)Repaint();
        }
    }
}
