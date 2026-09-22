using UnityEditor;
using UnityEngine;

namespace Utopia.Motion.Editor
{
    [CustomEditor(typeof(CommonMotionDartApiClient))]
    public sealed class CommonMotionDartApiClientEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            CommonMotionDartApiClient client = (CommonMotionDartApiClient)target;
            EditorGUILayout.Space();
            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("Enter Play Mode. Keep the DART API container running on this PC. No request is sent automatically.", MessageType.Info);
            using (new EditorGUI.DisabledScope(!Application.isPlaying || !client.isActiveAndEnabled || client.IsBusy))
            {
                if (GUILayout.Button("Check Health")) client.CheckHealth();
                if (GUILayout.Button("Generate and Play (HTTP)")) client.GenerateAndPlay();
            }
            using (new EditorGUI.DisabledScope(!Application.isPlaying || !client.IsBusy))
                if (GUILayout.Button("Cancel Waiting (server continues)")) client.CancelWait();
            EditorGUILayout.HelpBox(client.Status, string.IsNullOrEmpty(client.LastError) ? MessageType.Info : MessageType.Error);
            if (!string.IsNullOrEmpty(client.LastError)) EditorGUILayout.HelpBox(client.LastError, MessageType.Error);
            EditorGUILayout.LabelField("HTTP status", client.LastHttpStatus == 0 ? "-" : client.LastHttpStatus.ToString());
            EditorGUILayout.LabelField("Last motion ID", string.IsNullOrEmpty(client.LastMotionId) ? "-" : client.LastMotionId);
            Metric("HTTP round trip (ms)", client.LastHttpRoundtripMs);
            Metric("Validate / load / callbacks (ms)", client.LastPlayerLoadMs);
            Metric("Request -> Play() call (ms)", client.LastRequestToPlayCallMs);
            Metric("Server service (ms, header)", client.LastServerServiceMs);
            EditorGUILayout.HelpBox("This requests a complete new DART clip from the standing seed. No streaming, blend, floor correction, or server-side cancellation. Play() timing is NOT photon/render latency.", MessageType.None);
            if (Application.isPlaying) Repaint();
        }
        private static void Metric(string label, double value)
        {
            EditorGUILayout.LabelField(label, value < 0 ? "-" : value.ToString("F1"));
        }
    }
}
