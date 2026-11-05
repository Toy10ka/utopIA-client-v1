using UnityEditor;
using UnityEngine;
namespace Utopia.Motion.Editor
{
    [CustomEditor(typeof(CommonMotionEnvironmentGenerationClient))]
    public sealed class CommonMotionEnvironmentGenerationClientEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();var c=(CommonMotionEnvironmentGenerationClient)target;
            EditorGUILayout.HelpBox("OfficialCaseStart needs NO previous DART/InfBaGel clip. It resets to the official human+object template (may already hold an object), NOT the current idle/Animator/IK pose. Captured history preserves raw contact state, including grasps. Both use the same registered room.",MessageType.Info);
            using(new EditorGUI.DisabledScope(!UnityEngine.Application.isPlaying || !c.isActiveAndEnabled || c.IsBusy))
            {
                if(GUILayout.Button("1A. Prepare Official Start (no previous motion required)"))c.PrepareOfficialStart();
                if(GUILayout.Button("Pause loaded InfBaGel at frame 30"))c.PauseAtFrame30();
                if(GUILayout.Button("1B. Capture paused InfBaGel History (0.1s)"))c.CaptureInfBaGelHistory();
                if(GUILayout.Button("2. Validate selected start + room (no sampling)"))c.ValidateStart();
                if(GUILayout.Button("Preview validated initial A* path"))c.PreviewInitialPath();
                if(GUILayout.Button("3. Generate in registered room and play"))c.GenerateAndPlay();
            }
            using(new EditorGUI.DisabledScope(!c.IsBusy))if(GUILayout.Button("Cancel Waiting (server continues)"))c.CancelWaiting();
            EditorGUILayout.HelpBox(c.Status,string.IsNullOrEmpty(c.LastError)?MessageType.Info:MessageType.Error);
            EditorGUILayout.LabelField("HTTP round trip ms",c.LastHttpMs.ToString("F1"));
            EditorGUILayout.LabelField("Server service ms",c.LastServerMs.ToString("F1"));
            if(UnityEngine.Application.isPlaying)Repaint();
        }
    }
}
