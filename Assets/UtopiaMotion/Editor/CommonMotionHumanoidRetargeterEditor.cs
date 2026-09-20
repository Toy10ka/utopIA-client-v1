using System;
using UnityEditor;
using UnityEngine;

namespace Utopia.Motion.Editor
{
    [CustomEditor(typeof(CommonMotionHumanoidRetargeter))]
    public sealed class CommonMotionHumanoidRetargeterEditor : UnityEditor.Editor
    {
        private string targetCheck = "";
        private bool targetValid;
        private bool showMapping;
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            CommonMotionHumanoidRetargeter retargeter = (CommonMotionHumanoidRetargeter)target;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(retargeter.Status, retargeter.Status.StartsWith("ERROR:") ? MessageType.Error : MessageType.Info);
            if (GUILayout.Button("Check Target Animator (no pose changes)"))
            {
                try
                {
                    CommonMotionHumanoidRetargeter.CheckAnimator(retargeter.targetAnimator);
                    Transform hips = retargeter.targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
                    if (hips == null) throw new ArgumentException("Hips transform was not returned. Check Humanoid mapping / exposed bones.");
                    targetCheck = "PASS: valid Humanoid Avatar, Hips=" + hips.name + ". Reference-pose calibration is checked at Play/Bind.";
                    targetValid = true;
                }
                catch (Exception e) { targetCheck = e.Message; targetValid = false; }
            }
            if (!string.IsNullOrEmpty(targetCheck)) EditorGUILayout.HelpBox(targetCheck, targetValid ? MessageType.Info : MessageType.Error);
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Bind / Rebind")) retargeter.Bind();
                if (GUILayout.Button("Unbind / Restore")) retargeter.Unbind();
                EditorGUILayout.EndHorizontal();
                if (retargeter.IsBound && retargeter.source != null && retargeter.source.IsLoaded)
                {
                    CommonMotionPlayer player = retargeter.source;
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Play source")) player.Play();
                    if (GUILayout.Button("Pause source")) player.Pause();
                    if (GUILayout.Button("Restart source")) player.Restart();
                    EditorGUILayout.EndHorizontal();
                    EditorGUI.BeginChangeCheck();
                    float frame = EditorGUILayout.Slider("Source frame", player.CurrentFrame, 0, player.FrameCount - 1);
                    if (EditorGUI.EndChangeCheck()) { player.SetFrame(frame); SceneView.RepaintAll(); }
                    if (GUILayout.Button("Validate applied pose")) retargeter.ValidateAppliedPose();
                }
            }
            if (!string.IsNullOrEmpty(retargeter.LastValidation)) EditorGUILayout.HelpBox(retargeter.LastValidation, MessageType.Info);
            showMapping = EditorGUILayout.Foldout(showMapping, "Bone mapping", true);
            if (showMapping) EditorGUILayout.HelpBox(retargeter.DescribeMapping(), MessageType.None);
            EditorGUILayout.HelpBox("Play Mode only. Use a separate preview avatar. Avatar root is placed in Source coordinates. Animator is temporarily paused; object tracks stay with Source. This is rotation/root transfer, not grasp/foot/contact IK.", MessageType.Info);
            if (Application.isPlaying) Repaint();
        }
    }
}
