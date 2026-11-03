using UnityEditor;
using UnityEngine;

namespace Utopia.Motion.Editor
{
    [CustomEditor(typeof(CommonMotionDartToInfBaGelClient))]
    public sealed class CommonMotionDartToInfBaGelClientEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            CommonMotionDartToInfBaGelClient c = (CommonMotionDartToInfBaGelClient)target;
            EditorGUILayout.HelpBox("v0.5: DART SOURCE body (not avatar bones) + a STATIC UNHELD known object. Official scene/goal/task remain. First load an InfBaGel case and save its scene reference. Then load DART, inspect placement, capture, validate, generate. Existing v0.4 controls are not replaced.", MessageType.Info);
            EditorGUILayout.HelpBox("contact-r2: CHOIS defines left/right hand + left/right FOOT contact with the OBJECT, not the floor. InfBaGel channels 2/3 remain a hypothesis. To run this limited test, explicitly enable Accept Provisional Contact Schema AND confirm BOTH hands and feet are clear of the static object at BOTH captured times. Server checks joint-to-surface margins, not exact mesh-part contacts. A validation PASS is NOT semantic verification.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!UnityEngine.Application.isPlaying || !c.isActiveAndEnabled || c.IsBusy))
            {
                if (GUILayout.Button("1. Remember loaded InfBaGel scene + static object")) c.RememberInfBaGelScene();
                using (new EditorGUI.DisabledScope(!c.ReferenceReady))
                {
                    if (GUILayout.Button("2. Load assigned DART JSON and pause")) c.LoadDartJson();
                    if (GUILayout.Button("Pause current DART at 1 second")) c.PauseDartAtOneSecond();
                    if (GUILayout.Button("3. Align paused DART pelvis / heading to case start (placement only)")) c.AlignPausedDartToCaseStart();
                    if (GUILayout.Button("Frame static input object in Scene") && SceneView.lastActiveSceneView != null)
                        SceneView.lastActiveSceneView.Frame(new Bounds(c.ObjectMarker.position,Vector3.one*2.5f),false);
                    if (GUILayout.Button("4. Capture DART body + static object (0.1s)")) c.CaptureDartHistory();
                }
                using (new EditorGUI.DisabledScope(!c.HasCapture))
                {
                    if (GUILayout.Button("5. Validate DART input on server (no sampling)")) c.ValidateDartHistory();
                    if (GUILayout.Button("6. Generate from captured DART and play")) c.GenerateFromDart();
                }
            }
            using (new EditorGUI.DisabledScope(!c.IsBusy))
                if (GUILayout.Button("Cancel waiting (server continues)")) c.CancelWaiting();
            EditorGUILayout.HelpBox(c.Status, string.IsNullOrEmpty(c.LastError)?MessageType.Info:MessageType.Error);
            EditorGUILayout.LabelField("Contact schema", "PROVISIONAL (not dataset-verified)");
            EditorGUILayout.LabelField("Explicit provisional opt-in",c.acceptProvisionalContactSchema.ToString());
            EditorGUILayout.LabelField("Input validation passed",c.InputValidated.ToString());
            EditorGUILayout.LabelField("Fixed output frames passed",c.OutputFramesVerified.ToString());
            EditorGUILayout.LabelField("HTTP round trip (ms)",c.LastHttpMs<0?"-":c.LastHttpMs.ToString("F1"));
            EditorGUILayout.LabelField("Server service (ms)",c.LastServerMs<0?"-":c.LastServerMs.ToString("F1"));
            EditorGUILayout.LabelField("Request to Play call (ms)",c.LastToPlayCallMs<0?"-":c.LastToPlayCallMs.ToString("F1"));
            if(UnityEngine.Application.isPlaying) Repaint();
        }
        private void OnSceneGUI()
        {
            CommonMotionDartToInfBaGelClient c=(CommonMotionDartToInfBaGelClient)target;
            if(!c.ReferenceReady || c.ObjectMarker==null || !c.ObjectMarker.gameObject.activeInHierarchy)return;
            Transform t=c.ObjectMarker;
            Handles.color=new Color(.2f,1f,.7f,1f);
            Handles.Label(t.position+Vector3.up*.15f,"STATIC OBJECT INPUT (not animated / not a goal)");
            if(c.IsBusy)return;
            EditorGUI.BeginChangeCheck();
            Vector3 p=Handles.PositionHandle(t.position,Quaternion.identity);
            Quaternion q=Handles.RotationHandle(t.rotation,p);
            if(EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(t,"Move static object input");t.SetPositionAndRotation(p,q);
            }
        }
    }
}
