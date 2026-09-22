using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Utopia.Motion
{
    /// <summary>
    /// Development client: HTTP request -> existing CommonMotionPlayer.LoadJson -> Play.
    /// No model/coordinate conversion, floor placement, retarget, or routing here.
    /// Does not generate until explicitly requested in Play Mode.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CommonMotionInfBaGelApiClient : MonoBehaviour
    {
        [Header("Existing playback path")]
        public CommonMotionPlayer targetPlayer;
        [Header("InfBaGel API (Unity Editor on the same PC)")]
        public string apiBaseUrl = "http://127.0.0.1:8002";
        [Range(1, 600)] public int timeoutSeconds = 300;
        [Header("One official HOSI case (not the current Unity scene)")]
        public string sceneId = InfBaGelApiProtocol.DefaultSceneId;
        [Min(0)] public int testItemIndex = 0;
        [Min(0)] public int seed = 0;
        public bool playOnResponse = true;
        [Header("Optional: legacy CommonMotionHumanoidPreview only")]
        [Tooltip("Leave empty for CommonMotionHumanoidRetargeter v0.2. For the separate visual-copy Preview version, connect BuildPreview here once.")]
        public UnityEvent afterMotionLoaded = new UnityEvent();

        public bool IsBusy { get { return pending != null; } }
        public string Status { get; private set; } = "IDLE: enter Play Mode, then Check Health or Generate and Play.";
        public string LastError { get; private set; } = "";
        public long LastHttpStatus { get; private set; }
        public double LastHttpRoundtripMs { get; private set; } = -1;
        public double LastPlayerLoadMs { get; private set; } = -1;
        public double LastRequestToPlayCallMs { get; private set; } = -1;
        public double LastServerServiceMs { get; private set; } = -1;
        public string LastMotionId { get; private set; } = "";

        public string LastObjectAssetId { get; private set; } = "";
        public string LastCase { get; private set; } = "";
        public int LastFrameCount { get; private set; }
        public int LastObjectCount { get; private set; }
        public float LastFps { get; private set; }

        private sealed class Pending : IDisposable
        {
            public UnityWebRequest web;
            public Coroutine routine;
            public CommonMotionPlayer player;
            public bool health, autoPlay;
            public string url, body;
            public int timeout;
            public InfBaGelGenerateMotionRequest selectedCase;
            public string expectedMotionId;
            public TextAsset expectedJson;
            private bool disposed;
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (web != null) { web.Dispose(); web = null; }
            }
        }
        private Pending pending;
        // A runtime-only TextAsset also supports the older preview that reads motionJson.
        // No asset file is created/imported and no JSON is written into Assets.
        private TextAsset ownedJson;
        private TextAsset priorJson;
        private CommonMotionPlayer assetOwner;
        private string bearerToken = ""; // Deliberately not serialized into the Unity project.

        private void Reset() { targetPlayer = GetComponent<CommonMotionPlayer>(); }
        private void OnDisable() { CancelWait(); }
        private void OnDestroy() { CancelWait(); ReleaseRuntimeJson(); }

        public void SetBearerToken(string token)
        {
            if (IsBusy) throw new InvalidOperationException("Do not change credentials during a request.");
            if (token != null && (token.Contains("\r") || token.Contains("\n")))
                throw new ArgumentException("Invalid token.");
            bearerToken = token ?? "";
        }

        [ContextMenu("InfBaGel: Check health (Play Mode)")]
        public void CheckHealth()
        {
            if (!CanBegin()) return;
            try
            {
                Begin(new Pending { health = true, url = InfBaGelApiProtocol.Endpoint(apiBaseUrl, "health"), timeout = 10 });
            }
            catch (Exception e) { Fail(e.Message); }
        }

        [ContextMenu("InfBaGel: Generate and play (Play Mode)")]
        public void GenerateAndPlay() { RequestCase(sceneId, testItemIndex, seed); }

        /// <summary>Can be called by a UI/controller later. Returns false if not started.</summary>
        public bool RequestCase(string selectedSceneId, int itemIndex = 0, int randomSeed = 0)
        {
            if (!CanBegin()) return false;
            try
            {
                if (targetPlayer == null) targetPlayer = GetComponent<CommonMotionPlayer>();
                if (targetPlayer == null || !targetPlayer.isActiveAndEnabled)
                    throw new InvalidOperationException("Assign an enabled CommonMotionPlayer to Target Player.");
                InfBaGelGenerateMotionRequest request = InfBaGelApiProtocol.Request(selectedSceneId, itemIndex, randomSeed);
                Begin(new Pending
                {
                    player = targetPlayer, health = false, autoPlay = playOnResponse,
                    url = InfBaGelApiProtocol.Endpoint(apiBaseUrl, "generate_motion"),
                    body = JsonUtility.ToJson(request), timeout = Mathf.Clamp(timeoutSeconds, 1, 600),
                    selectedCase = request, expectedMotionId = targetPlayer.MotionId,
                    expectedJson = targetPlayer.motionJson
                });
                return true;
            }
            catch (Exception e) { Fail(e.Message); return false; }
        }

        private bool CanBegin()
        {
            if (!Application.isPlaying || !isActiveAndEnabled)
            {
                Debug.LogWarning("[INFBAGEL_CLIENT] Enter Play Mode and enable this component first.", this);
                return false;
            }
            if (IsBusy)
            {
                Debug.LogWarning("[INFBAGEL_CLIENT] Already waiting for one request; duplicate request was not sent.", this);
                return false;
            }
            return true;
        }

        private void Begin(Pending request)
        {
            pending = request;
            LastError = "";
            LastHttpStatus = 0;
            LastHttpRoundtripMs = LastPlayerLoadMs = LastRequestToPlayCallMs = LastServerServiceMs = -1;
            Status = request.health ? "CHECKING HEALTH..." : "GENERATING: one official case; waiting for complete human + object motion...";
            if (!request.health) LastCase = request.selectedCase.scene_id + "/" + request.selectedCase.test_item_index;
            // Old playback and its placement are left as-is while the server works.
            Debug.Log("[INFBAGEL_CLIENT] " + (request.health ? "GET /health" : "POST /generate_motion"), this);
            try { request.routine = StartCoroutine(Send(request)); }
            catch
            {
                pending = null;
                request.Dispose();
                throw;
            }
        }

        private bool IsCurrent(Pending request) { return ReferenceEquals(pending, request); }

        private IEnumerator Send(Pending request)
        {
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                string startupError = null;
                try
                {
                    request.web = new UnityWebRequest(request.url, request.health ? "GET" : "POST");
                    request.web.downloadHandler = new DownloadHandlerBuffer();
                    request.web.timeout = request.timeout;
                    request.web.redirectLimit = 0;
                    request.web.SetRequestHeader("Accept", "application/json");
                    if (!string.IsNullOrEmpty(bearerToken))
                        request.web.SetRequestHeader("Authorization", "Bearer " + bearerToken);
                    if (!request.health)
                    {
                        request.web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.body));
                        request.web.SetRequestHeader("Content-Type", "application/json");
                    }
                    request.web.SendWebRequest();
                }
                catch (Exception e) { startupError = e.Message; }
                if (startupError != null) { Fail(startupError); yield break; }

                int byteLimit = request.health ? InfBaGelApiProtocol.MaxHealthBytes : InfBaGelApiProtocol.MaxResponseBytes;
                while (!request.web.isDone)
                {
                    if (!IsCurrent(request)) yield break;
                    if (request.web.downloadedBytes > (ulong)byteLimit)
                    {
                        request.web.Abort();
                        Fail("Response exceeds the client byte limit. Old motion was not replaced.");
                        yield break;
                    }
                    yield return null;
                }
                if (!IsCurrent(request)) yield break;
                LastHttpRoundtripMs = clock.Elapsed.TotalMilliseconds;
                LastHttpStatus = request.web.responseCode;
                if (request.web.downloadedBytes > (ulong)byteLimit)
                {
                    Fail("Response exceeds the client byte limit. Old motion was not replaced.");
                    yield break;
                }
                string response = request.web.downloadHandler.text;
                if (request.web.result != UnityWebRequest.Result.Success || request.web.responseCode != 200)
                {
                    Fail(InfBaGelApiProtocol.HttpError(request.web.responseCode, request.web.error, response));
                    yield break; // In particular: never parse 422/429/500 as a motion; never auto-retry.
                }
                try
                {
                    if (!InfBaGelApiProtocol.IsJsonContentType(request.web.GetResponseHeader("Content-Type")))
                        throw new InvalidOperationException("Expected application/json; check the API URL.");
                    if (request.health)
                    {
                        InfBaGelApiHealth health = InfBaGelApiProtocol.ReadHealth(response);
                        Status = health.busy ? "READY (server busy with another generation)" : "READY (server idle)";
                        Debug.Log("[INFBAGEL_CLIENT] " + Status, this);
                    }
                    else
                    {
                        LastServerServiceMs = InfBaGelApiProtocol.ReadMilliseconds(request.web.GetResponseHeader("X-Infbagel-Service-Ms"));
                        ApplyMotionResponse(request, response, request.web.GetResponseHeader("X-Motion-ID"), clock);
                    }
                }
                catch (Exception e) { if (IsCurrent(request)) Fail(e.Message); }
            }
            finally
            {
                request.Dispose();
                if (IsCurrent(request)) pending = null;
            }
        }

        private void ApplyMotionResponse(Pending request, string json, string headerId, Stopwatch clock)
        {
            CommonMotionPlayer player = request.player;
            if (player == null || player != targetPlayer || !player.isActiveAndEnabled)
                throw new InvalidOperationException("Target Player changed/disabled during the request. Result was not applied.");
            if (player.MotionId != request.expectedMotionId || player.motionJson != request.expectedJson)
                throw new InvalidOperationException("Player motion changed while waiting. Late response was not applied; do not request DART and InfBaGel simultaneously on one Player.");
            double loadStart = clock.Elapsed.TotalMilliseconds;
            CommonMotionClip validated = InfBaGelApiProtocol.ReadMotion(json, headerId, request.selectedCase);
            TextAsset next = new TextAsset(json) { name = validated.motion_id + ".common_motion (HTTP)", hideFlags = HideFlags.DontSave };
            TextAsset previous = player.motionJson;
            bool wasLoaded = player.IsLoaded;
            bool wasPlaying = player.IsPlaying;
            float previousFrame = player.CurrentFrame;
            player.motionJson = next;
            try
            {
                // Existing method: builds the same source skeleton/objects and emits PoseApplied in v0.2.
                player.LoadJson(json);
                if (!player.IsLoaded || player.MotionId != validated.motion_id)
                    throw new InvalidOperationException("Player did not load the returned motion.");
            }
            catch
            {
                player.motionJson = previous;
                Destroy(next);
                // Invalid data is rejected above. If Unity mesh/build itself fails,
                // try to restore the previously loaded source rather than leave it blank.
                if (wasLoaded && previous != null)
                {
                    try
                    {
                        player.LoadJson(previous.text);
                        player.SetFrame(previousFrame);
                        afterMotionLoaded?.Invoke();
                        if (wasPlaying) player.Play();
                    }
                    catch (Exception restoreError)
                    {
                        Debug.LogError("[INFBAGEL_CLIENT] Previous motion could not be restored: " + restoreError.Message, this);
                    }
                }
                throw;
            }
            AdoptRuntimeJson(player, next, previous);
            LastMotionId = validated.motion_id;
            LastObjectAssetId = validated.objects[0].asset_id;
            LastFrameCount = validated.time.frame_count;
            LastFps = validated.time.fps;
            LastObjectCount = validated.objects.Length;

            // Primary retargeter v0.2 follows PoseApplied and the new clip automatically.
            // For the separate visual-copy Preview v0.1, connect its BuildPreview here once.
            try { afterMotionLoaded?.Invoke(); }
            catch (Exception e)
            {
                Debug.LogError("[INFBAGEL_CLIENT] Motion loaded but After Motion Loaded listener failed: " + e.Message, this);
                throw;
            }
            if (!IsCurrent(request) || !isActiveAndEnabled || player == null || !player.isActiveAndEnabled) return;
            LastPlayerLoadMs = clock.Elapsed.TotalMilliseconds - loadStart;
            if (request.autoPlay)
            {
                player.Play();
                LastRequestToPlayCallMs = clock.Elapsed.TotalMilliseconds;
            }
            Status = request.autoPlay ? "RECEIVED + PLAY REQUESTED" : "RECEIVED + LOADED (paused)";
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[INFBAGEL_CLIENT] {0} id={1} frames={2} fps={3} http_ms={4:F1} load_ms={5:F1} to_play_call_ms={6:F1} server_ms={7:F1} objects={8} asset={9} case={10}",
                Status, LastMotionId, player.FrameCount, player.Fps, LastHttpRoundtripMs,
                LastPlayerLoadMs, LastRequestToPlayCallMs, LastServerServiceMs,
                LastObjectCount, LastObjectAssetId, LastCase), this);
        }

        private void AdoptRuntimeJson(CommonMotionPlayer player, TextAsset next, TextAsset previous)
        {
            if (assetOwner != player)
            {
                ReleaseRuntimeJson();
                assetOwner = player;
                priorJson = previous;
            }
            else if (previous != ownedJson)
            {
                // Preserve a manually selected file if the user changed it between requests.
                priorJson = previous;
            }
            if (ownedJson != null) Destroy(ownedJson);
            ownedJson = next;
        }
        private void ReleaseRuntimeJson()
        {
            if (assetOwner != null && assetOwner.motionJson == ownedJson)
                assetOwner.motionJson = priorJson;
            if (ownedJson != null) Destroy(ownedJson);
            ownedJson = null; priorJson = null; assetOwner = null;
        }
        private void Fail(string message)
        {
            LastError = message;
            Status = "ERROR";
            Debug.LogError("[INFBAGEL_CLIENT] " + message, this);
        }

        [ContextMenu("InfBaGel: Cancel waiting (does not stop server GPU work)")]
        public void CancelWait()
        {
            Pending old = pending;
            if (old == null) return;
            pending = null; // Stale result may never be applied after cancellation.
            if (old.web != null) old.web.Abort();
            if (old.routine != null) StopCoroutine(old.routine);
            old.Dispose();
            Status = "WAIT CANCELLED: server may still be generating. No automatic retry.";
            Debug.LogWarning("[INFBAGEL_CLIENT] " + Status, this);
        }
    }
}
