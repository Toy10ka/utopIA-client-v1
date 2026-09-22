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
                if (GUILayout.Button("Generate Case and Play (HTTP)")) client.GenerateAndPlay();
            }
            using (new EditorGUI.DisabledScope(!Application.isPlaying || !client.IsBusy))
                if (GUILayout.Button("Cancel Waiting (server continues)")) client.CancelWait();
            EditorGUILayout.HelpBox(client.Status, string.IsNullOrEmpty(client.LastError) ? MessageType.Info : MessageType.Error);
            if (!string.IsNullOrEmpty(client.LastError)) EditorGUILayout.HelpBox(client.LastError, MessageType.Error);
            EditorGUILayout.LabelField("HTTP status", client.LastHttpStatus == 0 ? "-" : client.LastHttpStatus.ToString());
            EditorGUILayout.LabelField("Requested case (scene/index)", string.IsNullOrEmpty(client.LastCase) ? "-" : client.LastCase);
            EditorGUILayout.LabelField("Last motion ID", string.IsNullOrEmpty(client.LastMotionId) ? "-" : client.LastMotionId);
            EditorGUILayout.LabelField("Frames / FPS / objects", client.LastFrameCount == 0 ? "-" : client.LastFrameCount + " / " + client.LastFps + " / " + client.LastObjectCount);
            EditorGUILayout.LabelField("Last object asset", string.IsNullOrEmpty(client.LastObjectAssetId) ? "-" : client.LastObjectAssetId);
            Metric("HTTP round trip (ms)", client.LastHttpRoundtripMs);
            Metric("Validate / load / callbacks (ms)", client.LastPlayerLoadMs);
            Metric("Request -> Play() call (ms)", client.LastRequestToPlayCallMs);
            Metric("Server service (ms, header)", client.LastServerServiceMs);
            EditorGUILayout.HelpBox("Uses a selected official dataset initial state, not the current Unity scene/avatar. Human + manipulated object only; background scene is not returned. No streaming, floor correction, blend, or server-side cancellation. Play() timing is not photon/render latency.", MessageType.None);
            if (Application.isPlaying) Repaint();
        }
        private static void Metric(string label, double value)
        {
            EditorGUILayout.LabelField(label, value < 0 ? "-" : value.ToString("F1"));
        }
    }
}
