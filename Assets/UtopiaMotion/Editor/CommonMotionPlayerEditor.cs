using UnityEditor;
using UnityEngine;

namespace Utopia.Motion.Editor
{
    [CustomEditor(typeof(CommonMotionPlayer))]
    public sealed class CommonMotionPlayerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            CommonMotionPlayer player = (CommonMotionPlayer)target;
            player.RefreshVisibility();
            EditorGUILayout.Space();
            if (GUILayout.Button("Load / Reload JSON")) player.Reload();
            if (!player.IsLoaded) return;
            EditorGUILayout.LabelField("Motion", player.MotionId);
            EditorGUILayout.LabelField("Frames / FPS", player.FrameCount + " / " + player.Fps);
            EditorGUILayout.LabelField("Placement space", player.SpaceId);
            if (Application.isPlaying)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Play")) player.Play();
                if (GUILayout.Button("Pause")) player.Pause();
                if (GUILayout.Button("Restart")) player.Restart();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.BeginChangeCheck();
            float frame = EditorGUILayout.Slider("Frame (scrub)", player.CurrentFrame, 0, player.FrameCount - 1);
            if (EditorGUI.EndChangeCheck())
            {
                player.SetFrame(frame);
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Validate current frame FK")) player.ValidateCurrentFrame();
            if (!string.IsNullOrEmpty(player.LastValidationMessage))
                EditorGUILayout.HelpBox(player.LastValidationMessage, player.LastValidationPassed ? MessageType.Info : MessageType.Warning);
            if (Application.isPlaying && player.IsPlaying) Repaint();
            EditorGUILayout.HelpBox("Source-skeleton/objects player. Add CommonMotionHumanoidRetargeter on another object for avatar preview in Play Mode. Human and object share this placement transform. Scene geometry is not included.", MessageType.Info);
        }

        [MenuItem("Tools/utopIA/Create CommonMotion Preview")]
        private static void CreatePreview()
        {
            TextAsset selectedJson = Selection.activeObject as TextAsset;
            GameObject go = new GameObject("CommonMotion Preview");
            Undo.RegisterCreatedObjectUndo(go, "Create CommonMotion Preview");
            CommonMotionPlayer player = go.AddComponent<CommonMotionPlayer>();
            player.motionJson = selectedJson;
            Selection.activeGameObject = go;
            if (selectedJson != null) player.Reload();
        }
    }
}
