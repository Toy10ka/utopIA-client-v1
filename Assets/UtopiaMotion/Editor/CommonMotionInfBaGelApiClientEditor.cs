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
            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("Enter Play Mode. Keep InfBaGel API running on host port 8002. No request is sent automatically.", MessageType.Info);
            if (client.targetPlayer != null)
            {
                // No compile-time dependency on the earlier optional DART HTTP client.
                foreach (MonoBehaviour other in client.targetPlayer.GetComponents<MonoBehaviour>())
                    if (other != null && other.isActiveAndEnabled && other.GetType().FullName == "Utopia.Motion.CommonMotionDartApiClient")
                        EditorGUILayout.HelpBox("DART client is also enabled on this Player. Disable its component for this test; keep Player and Retargeter enabled. Do not send both requests concurrently.", MessageType.Warning);
            }
            using (new EditorGUI.DisabledScope(!Application.isPlaying || !client.isActiveAndEnabled || client.IsBusy))
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
                if (GUILayout.Button(client.useGoalOverrides ? "Generate With Goal Markers and Play (HTTP)" : "Generate Official Case and Play (HTTP)")) client.GenerateAndPlay();
            }
            using (new EditorGUI.DisabledScope(!Application.isPlaying || !client.IsBusy))
                if (GUILayout.Button("Cancel Waiting (server continues)")) client.CancelWait();
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(client.GoalStatus, MessageType.Info);
            if (client.useGoalOverrides)
                EditorGUILayout.HelpBox("Goals only. Initial human/object placement and background remain the official case. Pelvis marker: ground XZ. Object marker: motion origin/centroid (NOT bottom). Keep Source/parents fixed while waiting. Markers reset when exiting Play Mode.", MessageType.Info);
            if (!string.IsNullOrEmpty(client.LastSentGoals))
                EditorGUILayout.SelectableLabel(client.LastSentGoals, EditorStyles.textArea, GUILayout.Height(55));
            if (client.LastGoalEchoVerified)
            {
                EditorGUILayout.HelpBox("Last response GOAL ECHO PASS: server used the sent goal values. This does NOT mean the motion reached them without collision.", MessageType.Info);
                Metric("Source final pelvis XZ error (m)", client.LastFinalPelvisXZErrorM, "F3");
                Metric("Object final origin error (m)", client.LastFinalObjectErrorM, "F3");
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
            EditorGUILayout.HelpBox("API v0.2: selected official initial state/scene, with optional Unity goal points. Not current Unity scene/avatar input. Human + manipulated object only; background scene is not returned. No streaming, floor correction, blend, or server-side cancellation. Play() timing is not photon/render latency.", MessageType.None);
            if (Application.isPlaying) Repaint();
        }
        private void OnSceneGUI()
        {
            CommonMotionInfBaGelApiClient client = (CommonMotionInfBaGelApiClient)target;
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
