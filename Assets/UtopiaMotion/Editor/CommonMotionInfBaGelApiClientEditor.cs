using UnityEditor;
using UnityEngine;

namespace Utopia.Motion.Editor
{
    [CustomEditor(typeof(CommonMotionInfBaGelApiClient))]
    public sealed class CommonMotionInfBaGelApiClientEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            CommonMotionInfBaGelApiClient client = (CommonMotionInfBaGelApiClient)target;
            EditorGUILayout.Space();
            if (!UnityEngine.Application.isPlaying)
                EditorGUILayout.HelpBox("Enter Play Mode. Keep InfBaGel API running on host port 8002. No request is sent automatically.", MessageType.Info);
            if (client.targetPlayer != null)
            {
                // No compile-time dependency on the earlier optional DART HTTP client.
                foreach (MonoBehaviour other in client.targetPlayer.GetComponents<MonoBehaviour>())
                    if (other != null && other.isActiveAndEnabled && other.GetType().FullName == "Utopia.Motion.CommonMotionDartApiClient")
                        EditorGUILayout.HelpBox("DART client is also enabled on this Player. Disable its component for this test; keep Player and Retargeter enabled. Do not send both requests concurrently.", MessageType.Warning);
            }
            using (new EditorGUI.DisabledScope(!UnityEngine.Application.isPlaying || !client.isActiveAndEnabled || client.IsBusy))
            {
                if (GUILayout.Button("Check Health")) client.CheckHealth();
                if (GUILayout.Button("Fetch Case Goals / Reset Markers (no generation)")) client.FetchCaseGoals();
                using (new EditorGUI.DisabledScope(!client.GoalsReady))
                {
                    if (GUILayout.Button("Check Original Goal Round Trip")) client.CheckOriginalGoalRoundTrip();
                    if (GUILayout.Button("Shift Both Goals +0.10m (preview local X)")) client.ShiftBothGoals(0.10f);
                    if (GUILayout.Button("Frame Goal Markers in Scene"))
                    {
                        if (SceneView.lastActiveSceneView != null)
                        {
                            Bounds b = new Bounds(client.pelvisGoalMarker.position, Vector3.one * 0.5f);
                            b.Encapsulate(client.objectGoalMarker.position);
                            SceneView.lastActiveSceneView.Frame(b, false);
                        }
                    }
                }
                EditorGUILayout.Space();
                if (GUILayout.Button("Fetch Initial Object / Reset Input Marker (no generation)")) client.FetchInitialObject();
                using (new EditorGUI.DisabledScope(!client.InitialObjectReady))
                {
                    if (GUILayout.Button("Check Original Initial Object Round Trip")) client.CheckOriginalInitialObjectRoundTrip();
                    if (GUILayout.Button("Shift Initial Object +0.10m (preview local X)")) client.ShiftInitialObject(0.10f);
                    if (GUILayout.Button("Yaw Initial Object +15 deg (world up)")) client.YawInitialObject(15f);
                    if (GUILayout.Button("Frame Initial Object in Scene") && SceneView.lastActiveSceneView != null)
                        SceneView.lastActiveSceneView.Frame(new Bounds(client.initialObjectMarker.position, Vector3.one * 2.5f), false);
                }
                EditorGUILayout.Space();
                if (GUILayout.Button("Pause source at frame 30 (1 second)")) client.PauseAtFrame30();
                if (GUILayout.Button("Capture Human + Object History (0.1s)")) client.CaptureHumanHistory();
                using (new EditorGUI.DisabledScope(!client.HumanHistoryReady))
                    if (GUILayout.Button("Check Human Input on Server (no generation)")) client.ValidateHumanHistory();
                if (GUILayout.Button(client.useHumanHistory ? "Generate From Captured History and Play (HTTP)" : client.useObjectStartOverride ? "Generate With Initial Object and Play (HTTP)" : client.useGoalOverrides ? "Generate With Goal Markers and Play (HTTP)" : "Generate Official Case and Play (HTTP)")) client.GenerateAndPlay();
            }
            using (new EditorGUI.DisabledScope(!UnityEngine.Application.isPlaying || !client.IsBusy))
                if (GUILayout.Button("Cancel Waiting (server continues)")) client.CancelWait();
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(client.HumanHistoryStatus, client.LastHumanFramesVerified || client.LastHumanInputValidated ? MessageType.Info : MessageType.None);
            if (client.useHumanHistory)
                EditorGUILayout.HelpBox("v0.4: paused CommonMotion source history BEFORE retarget/IK, plus the SAME object's two poses. Turn Object Start Override OFF. Body profile and all four contact channels are preserved from that source. Arbitrary Animator/DART input is not enabled. No live replanning/streaming.", MessageType.Info);
            if (client.useTextOverride)
                EditorGUILayout.HelpBox("The first text request may download/load CLIP ViT-L/14@336px. Geometry, object and task flags remain case-owned; use compatible object-movement instructions.", MessageType.Info);
            EditorGUILayout.HelpBox(client.GoalStatus, MessageType.Info);
            if (client.useGoalOverrides)
                EditorGUILayout.HelpBox("Goals are destinations. Initial object changes only when Use Object Start Override is ON. Background remains official; body comes from captured history only when that mode is enabled. Pelvis marker: ground XZ. Object marker: motion origin/centroid (NOT bottom). Keep Source/parents fixed while waiting. Markers reset when exiting Play Mode.", MessageType.Info);
            if (!string.IsNullOrEmpty(client.LastSentGoals))
                EditorGUILayout.SelectableLabel(client.LastSentGoals, EditorStyles.textArea, GUILayout.Height(55));
            if (client.LastGoalEchoVerified)
            {
                EditorGUILayout.HelpBox("Last response GOAL ECHO PASS: server used the sent goal values. This does NOT mean the motion reached them without collision.", MessageType.Info);
                Metric("Source final pelvis XZ error (m)", client.LastFinalPelvisXZErrorM, "F3");
                Metric("Object final origin error (m)", client.LastFinalObjectErrorM, "F3");
            }
            EditorGUILayout.HelpBox(client.InitialObjectStatus, MessageType.Info);
            if (client.useObjectStartOverride)
                EditorGUILayout.HelpBox("Object start only: horizontal shift <=0.50m / yaw change <=30deg, no scale or new height. Official human/scene remain fixed. Two initial frames keep the official temporal delta; this is NOT current measured history. An object moved out of reach may not be picked up.", MessageType.Info);
            if (client.LastInitialObjectEchoVerified)
            {
                EditorGUILayout.HelpBox("OBJECT START ECHO PASS (input agreement only)", MessageType.Info);
                Metric("First object position error (m)", client.LastInitialObjectPositionErrorM, "F6");
                Metric("First object rotation error (deg)", client.LastInitialObjectRotationErrorDeg, "F4");
                EditorGUILayout.HelpBox(client.LastInitialFrameVerified ? "FIRST OBJECT FRAME PASS" : "FIRST OBJECT FRAME MISMATCH", client.LastInitialFrameVerified ? MessageType.Info : MessageType.Warning);
                EditorGUILayout.LabelField("Released initial hand contact flags", client.LastReleasedHandContacts.ToString());
            }
            EditorGUILayout.HelpBox(client.Status, string.IsNullOrEmpty(client.LastError) ? MessageType.Info : MessageType.Error);
            if (!string.IsNullOrEmpty(client.LastError)) EditorGUILayout.HelpBox(client.LastError, MessageType.Error);
            EditorGUILayout.LabelField("Server API version", string.IsNullOrEmpty(client.LastServerApiVersion) ? "-" : client.LastServerApiVersion);
            EditorGUILayout.LabelField("HTTP status", client.LastHttpStatus == 0 ? "-" : client.LastHttpStatus.ToString());
            EditorGUILayout.LabelField("Requested case (scene/index)", string.IsNullOrEmpty(client.LastCase) ? "-" : client.LastCase);
            EditorGUILayout.LabelField("Last motion ID", string.IsNullOrEmpty(client.LastMotionId) ? "-" : client.LastMotionId);
            EditorGUILayout.LabelField("Frames / FPS / objects", client.LastFrameCount == 0 ? "-" : client.LastFrameCount + " / " + client.LastFps + " / " + client.LastObjectCount);
            EditorGUILayout.LabelField("Last object asset", string.IsNullOrEmpty(client.LastObjectAssetId) ? "-" : client.LastObjectAssetId);
            Metric("HTTP round trip (ms)", client.LastHttpRoundtripMs);
            Metric("Validate / load / callbacks (ms)", client.LastPlayerLoadMs);
            Metric("Request -> Play() call (ms)", client.LastRequestToPlayCallMs);
            Metric("Server service (ms, header)", client.LastServerServiceMs);
            EditorGUILayout.HelpBox("API v0.4: optional text and paired CommonMotion human/object history. Scene/BPS/task remain official. Not current Unity scene/avatar input. Human + manipulated object only; background scene is not returned. No streaming, floor correction, blend, or server-side cancellation. Play() timing is not photon/render latency.", MessageType.None);
            if (UnityEngine.Application.isPlaying) Repaint();
        }
        private void OnSceneGUI()
        {
            CommonMotionInfBaGelApiClient client = (CommonMotionInfBaGelApiClient)target;
            if (client.InitialObjectReady)
            {
                Handles.color = new Color(0.2f, 1f, 0.7f, 1f);
                Handles.Label(client.initialObjectMarker.position + Vector3.up * 0.2f, "Initial object INPUT (not goal)");
            }
            if (!client.GoalsReady) return;
            DrawMarker(client, client.pelvisGoalMarker, "Pelvis goal (ground XZ)", Color.cyan, true);
            DrawMarker(client, client.objectGoalMarker, "Object goal (motion origin)", Color.yellow, false);
        }
        private static void DrawMarker(CommonMotionInfBaGelApiClient client, Transform marker, string label, Color color, bool ground)
        {
            if (marker == null) return;
            Handles.color = color;
            Handles.Label(marker.position + Vector3.up * 0.12f, label);
            if (client.IsBusy) return;
            EditorGUI.BeginChangeCheck();
            Quaternion yaw = client.targetPlayer != null ? client.targetPlayer.transform.rotation : Quaternion.identity;
            Vector3 pos = Handles.PositionHandle(marker.position, yaw);
            if (EditorGUI.EndChangeCheck())
            {
                // Pelvis is a ground-plane route destination, not a height target.
                // Position-handle editing intentionally preserves its current Y.
                if (ground) pos.y = marker.position.y;
                Undo.RecordObject(marker, "Move InfBaGel goal marker");
                marker.position = pos;
            }
        }
        private static void Metric(string label, double value, string format = "F1")
        {
            EditorGUILayout.LabelField(label, value < 0 ? "-" : value.ToString(format));
        }
    }
}
