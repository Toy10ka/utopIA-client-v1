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
    /// v0.3 adds known-object initial-pose input. Human initial state and scene remain official.
    /// Does not generate until explicitly requested in Play Mode.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class CommonMotionInfBaGelApiClient : MonoBehaviour
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
        [Header("Optional: Unity goal points (official scene)")]
        public bool useGoalOverrides = false;
        [Tooltip("Created by Fetch Case Goals. This marker is on the model ground plane, not the hip height.")]
        public Transform pelvisGoalMarker;
        [Tooltip("Target of the object's motion origin/centroid; NOT its bottom or supporting surface.")]
        public Transform objectGoalMarker;
        public string GoalStatus { get; private set; } = "Fetch Case Goals to create markers. No generation is needed.";
        public bool GoalsReady
        {
            get
            {
                return caseGoals != null && goalPlayer == targetPlayer && pelvisGoalMarker != null && objectGoalMarker != null &&
            caseGoals.scene_id == sceneId && caseGoals.test_item_index == testItemIndex;
            }
        }
        public bool LastGoalEchoVerified { get; private set; }
        public double LastFinalPelvisXZErrorM { get; private set; } = -1;
        public double LastFinalObjectErrorM { get; private set; } = -1;
        public string LastSentGoals { get; private set; } = "";
        private InfBaGelCaseGoals caseGoals;
        private CommonMotionPlayer goalPlayer;
        private Transform goalRoot;
        public InfBaGelCaseGoals ReferenceGoals { get { return caseGoals; } }
        [Header("Optional v0.3: Known-object INITIAL pose (not goal)")]
        public bool useObjectStartOverride = false;
        [Tooltip("Created by Fetch Initial Object. Move this input marker, NOT the animated output object. The origin/mesh use CommonMotion convention.")]
        public Transform initialObjectMarker;
        public bool showInitialObjectWire = true;
        public string InitialObjectStatus { get; private set; } = "Fetch Initial Object after loading this official case.";
        public bool InitialObjectReady
        {
            get
            {
                return initialObjectReference != null && initialObjectPlayer == targetPlayer && initialObjectMarker != null &&
            initialObjectReference.scene_id == sceneId && initialObjectReference.test_item_index == testItemIndex;
            }
        }
        public bool LastInitialObjectEchoVerified { get; private set; }
        public bool LastInitialFrameVerified { get; private set; }
        public double LastInitialObjectPositionErrorM { get; private set; } = -1;
        public double LastInitialObjectRotationErrorDeg { get; private set; } = -1;
        public int LastReleasedHandContacts { get; private set; }
        private InfBaGelInitialObject initialObjectReference;
        private CommonMotionPlayer initialObjectPlayer;
        private Mesh initialObjectWireMesh;
        [Header("Optional: legacy CommonMotionHumanoidPreview only")]
        [Tooltip("Leave empty for CommonMotionHumanoidRetargeter v0.2. For the separate visual-copy Preview version, connect BuildPreview here once.")]
        public UnityEvent afterMotionLoaded = new UnityEvent();

        public bool IsBusy { get { return pending != null; } }
        public string Status { get; private set; } = "IDLE: enter Play Mode, then Check Health or Generate and Play.";
        public string LastError { get; private set; } = "";
        public long LastHttpStatus { get; private set; }
        public string LastServerApiVersion { get; private set; } = "";
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
            public bool health, fetchGoals, fetchInitial, autoPlay, validateHuman;
            public InfBaGelHumanHistory humanHistory;
            public string textOverride;
            public bool IsRead { get { return health || fetchGoals || fetchInitial; } }
            public InfBaGelGoalOverrides goalOverrides;
            public InfBaGelObjectStartOverride objectStart;
            public Matrix4x4 placement;
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
        private void OnDestroy() { CancelWait(); ReleaseRuntimeJson(); ClearGoalMarkers(); ClearInitialObjectMarker(); }

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
        public void GenerateAndPlay()
        {
            try { RequestCase(sceneId, testItemIndex, seed, useGoalOverrides ? CaptureGoalMarkers() : null, useObjectStartOverride ? CaptureInitialObject() : null, useHumanHistory ? RequireHumanHistory() : null, useTextOverride ? instruction : null); }
            catch (Exception e) { Fail(e.Message); }
        }

        [ContextMenu("InfBaGel: Fetch case goals (no generation)")]
        public void FetchCaseGoals()
        {
            if (!CanBegin()) return;
            try
            {
                if (targetPlayer == null) targetPlayer = GetComponent<CommonMotionPlayer>();
                if (targetPlayer == null) throw new InvalidOperationException("Assign Target Player first.");
                InfBaGelGoalCoordinates.ValidatePlacement(targetPlayer.transform);
                Begin(new Pending
                {
                    fetchGoals = true,
                    health = false,
                    player = targetPlayer,
                    selectedCase = InfBaGelApiProtocol.Request(sceneId, testItemIndex, seed),
                    url = InfBaGelApiProtocol.CaseGoalsEndpoint(apiBaseUrl, sceneId, testItemIndex),
                    timeout = 10
                });
            }
            catch (Exception e) { Fail(e.Message); }
        }

        private void ClearGoalMarkers()
        {
            if (goalRoot != null) { goalRoot.gameObject.SetActive(false); Destroy(goalRoot.gameObject); }
            goalRoot = null; pelvisGoalMarker = null; objectGoalMarker = null;
            caseGoals = null; goalPlayer = null;
        }
        private Transform MakeMarker(string label, Vector3 native)
        {
            GameObject go = new GameObject(label); go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(goalRoot, false);
            go.transform.localPosition = InfBaGelGoalCoordinates.NativeToCommon(native);
            return go.transform;
        }
        private void InstallCaseGoals(Pending request, InfBaGelCaseGoals result)
        {
            if (request.player == null || request.player != targetPlayer || request.selectedCase.scene_id != sceneId ||
                request.selectedCase.test_item_index != testItemIndex)
                throw new InvalidOperationException("Case/Player changed while loading goals. Fetch again.");
            InfBaGelGoalCoordinates.ValidatePlacement(targetPlayer.transform);
            ClearGoalMarkers(); caseGoals = result; goalPlayer = targetPlayer;
            GameObject group = new GameObject("InfBaGel Goal Markers (temporary)"); group.hideFlags = HideFlags.DontSave;
            goalRoot = group.transform; goalRoot.SetParent(targetPlayer.transform, false);
            pelvisGoalMarker = MakeMarker("Pelvis Goal (ground XZ)", InfBaGelGoalCoordinates.FromArray(result.pelvis_goal));
            objectGoalMarker = MakeMarker("Object Goal (motion origin, not bottom)", InfBaGelGoalCoordinates.FromArray(result.object_goal));
            GoalStatus = "GOALS READY: markers at original case goals. Check round trip, then enable Use Goal Overrides.";
            Status = "CASE GOALS LOADED (no inference)";
            Debug.Log("[INFBAGEL_GOALS] " + GoalStatus + " scene=" + result.scene_id + " object=" + result.object_name, this);
        }
        public InfBaGelGoalOverrides CaptureGoalMarkers()
        {
            if (!GoalsReady) throw new InvalidOperationException("Fetch Case Goals first (again after changing Scene ID / Test Item Index / Target Player).");
            if (!targetPlayer.IsLoaded || targetPlayer.SpaceId != caseGoals.frame_id)
                throw new InvalidOperationException("Player must contain this same InfBaGel scene before overriding goals. Turn Use Goal Overrides OFF, Generate the official case once, then Fetch Case Goals.");
            Vector3 pelvis = InfBaGelGoalCoordinates.WorldToNative(targetPlayer.transform, pelvisGoalMarker.position);
            Vector3 obj = InfBaGelGoalCoordinates.WorldToNative(targetPlayer.transform, objectGoalMarker.position);
            if (Mathf.Abs(pelvis.y) > InfBaGelGoalCoordinates.PositionToleranceM)
                throw new InvalidOperationException("Pelvis Goal must stay on the model ground plane. Move it horizontally (local X/Z), not to the avatar hip height.");
            pelvis.y = 0f; // round-off only; larger vertical moves are rejected, not silently projected
            return InfBaGelApiProtocol.ValidateGoals(new InfBaGelGoalOverrides
            {
                pelvis_goal = InfBaGelGoalCoordinates.ToArray(pelvis),
                object_goal = InfBaGelGoalCoordinates.ToArray(obj)
            });
        }
        public void CheckOriginalGoalRoundTrip()
        {
            try
            {
                InfBaGelGoalOverrides goals = CaptureGoalMarkers();
                float p = Vector3.Distance(InfBaGelGoalCoordinates.FromArray(goals.pelvis_goal), InfBaGelGoalCoordinates.FromArray(caseGoals.pelvis_goal));
                float o = Vector3.Distance(InfBaGelGoalCoordinates.FromArray(goals.object_goal), InfBaGelGoalCoordinates.FromArray(caseGoals.object_goal));
                GoalStatus = string.Format(CultureInfo.InvariantCulture, "{0}: pelvis delta={1:F6}m object delta={2:F6}m (point conversion, NOT motion quality)",
                    Mathf.Max(p, o) <= InfBaGelGoalCoordinates.PositionToleranceM ? "ORIGINAL GOAL ROUNDTRIP PASS" : "GOALS MOVED FROM ORIGINAL", p, o);
                Debug.Log("[INFBAGEL_GOALS] " + GoalStatus, this);
            }
            catch (Exception e) { GoalStatus = e.Message; Fail(e.Message); }
        }
        public void ShiftBothGoals(float commonXMetres)
        {
            if (!GoalsReady || IsBusy) return;
            InfBaGelGoalCoordinates.ValidatePlacement(targetPlayer.transform);
            Vector3 delta = targetPlayer.transform.TransformVector(new Vector3(commonXMetres, 0, 0));
            pelvisGoalMarker.position += delta; objectGoalMarker.position += delta;
            GoalStatus = "Both markers moved along preview local X. Check feasibility in the original scene; background is not shown.";
        }
        private void OnDrawGizmosSelected()
        {
            if (pelvisGoalMarker != null) { Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(pelvisGoalMarker.position, 0.08f); }
            if (objectGoalMarker != null) { Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(objectGoalMarker.position, 0.08f); }
            if (pelvisGoalMarker != null && objectGoalMarker != null) Gizmos.DrawLine(pelvisGoalMarker.position, objectGoalMarker.position);
        }

        /// <summary>Can be called by a UI/controller later. Returns false if not started.</summary>
        public bool RequestCase(string selectedSceneId, int itemIndex = 0, int randomSeed = 0, InfBaGelGoalOverrides goals = null, InfBaGelObjectStartOverride objectStart = null, InfBaGelHumanHistory humanHistory = null, string textOverride = null)
        {
            if (!CanBegin()) return false;
            try
            {
                if (targetPlayer == null) targetPlayer = GetComponent<CommonMotionPlayer>();
                if (targetPlayer == null || !targetPlayer.isActiveAndEnabled)
                    throw new InvalidOperationException("Assign an enabled CommonMotionPlayer to Target Player.");
                InfBaGelGenerateMotionRequest request = InfBaGelApiProtocol.Request(selectedSceneId, itemIndex, randomSeed);
                InfBaGelGoalOverrides snapshot = goals == null ? null : InfBaGelApiProtocol.ValidateGoals(goals);
                InfBaGelObjectStartOverride startSnapshot = objectStart == null ? null : InfBaGelObjectStartProtocol.Validate(objectStart);
                if (snapshot != null || startSnapshot != null || humanHistory != null) InfBaGelGoalCoordinates.ValidatePlacement(targetPlayer.transform);
                LastInitialObjectEchoVerified = LastInitialFrameVerified = false;
                LastInitialObjectPositionErrorM = LastInitialObjectRotationErrorDeg = -1;
                LastReleasedHandContacts = 0;
                LastGoalEchoVerified = false;
                LastFinalPelvisXZErrorM = LastFinalObjectErrorM = -1;
                LastSentGoals = snapshot == null ? "Official case goals (no overrides)" : JsonUtility.ToJson(snapshot);
                Begin(new Pending
                {
                    goalOverrides = snapshot,
                    objectStart = startSnapshot,
                    humanHistory = humanHistory,
                    textOverride = textOverride,
                    placement = targetPlayer.transform.localToWorldMatrix,
                    player = targetPlayer,
                    health = false,
                    autoPlay = playOnResponse,
                    url = InfBaGelApiProtocol.Endpoint(apiBaseUrl, "generate_motion"),
                    body = InfBaGelHumanHistoryProtocol.Serialize(request, snapshot, startSnapshot, humanHistory, textOverride),
                    timeout = Mathf.Clamp(timeoutSeconds, 1, 600),
                    selectedCase = request,
                    expectedMotionId = targetPlayer.MotionId,
                    expectedJson = targetPlayer.motionJson
                });
                return true;
            }
            catch (Exception e) { Fail(e.Message); return false; }
        }

        private bool CanBegin()
        {
            if (!UnityEngine.Application.isPlaying || !isActiveAndEnabled)
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
            Status = request.validateHuman ? "VALIDATING HUMAN INPUT (no sampling)..." : request.health ? "CHECKING HEALTH..." : (request.fetchInitial ? "FETCHING INITIAL OBJECT (no generation)..." : request.fetchGoals ? "FETCHING CASE GOALS (no generation)..." : "GENERATING: one case; waiting for human + object motion...");
            if (!request.IsRead) LastCase = request.selectedCase.scene_id + "/" + request.selectedCase.test_item_index;
            // Old playback and its placement are left as-is while the server works.
            Debug.Log("[INFBAGEL_CLIENT] " + (request.health ? "GET /health" : (request.fetchInitial ? "GET initial object" : request.fetchGoals ? "GET case goals" : "POST /generate_motion")), this);
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
                    request.web = new UnityWebRequest(request.url, request.IsRead ? "GET" : "POST");
                    request.web.downloadHandler = new DownloadHandlerBuffer();
                    request.web.timeout = request.timeout;
                    request.web.redirectLimit = 0;
                    request.web.SetRequestHeader("Accept", "application/json");
                    if (!string.IsNullOrEmpty(bearerToken))
                        request.web.SetRequestHeader("Authorization", "Bearer " + bearerToken);
                    if (!request.IsRead)
                    {
                        request.web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.body));
                        request.web.SetRequestHeader("Content-Type", "application/json");
                    }
                    request.web.SendWebRequest();
                }
                catch (Exception e) { startupError = e.Message; }
                if (startupError != null) { Fail(startupError); yield break; }

                int byteLimit = (request.IsRead || request.validateHuman) ? InfBaGelApiProtocol.MaxHealthBytes : InfBaGelApiProtocol.MaxResponseBytes;
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
                    if (request.validateHuman)
                    {
                        ReceiveHumanValidation(request, response);
                    }
                    else if (request.health)
                    {
                        InfBaGelApiHealth health = InfBaGelApiProtocol.ReadHealth(response);
                        LastServerApiVersion = health.api_version;
                        Status = health.busy ? "READY (server busy with another generation)" : "READY (server idle)";
                        Debug.Log("[INFBAGEL_CLIENT] " + Status, this);
                    }
                    else if (request.fetchInitial)
                    {
                        InstallInitialObject(request, InfBaGelObjectStartProtocol.ReadInitial(response, request.selectedCase.scene_id, request.selectedCase.test_item_index));
                    }
                    else if (request.fetchGoals)
                    {
                        InstallCaseGoals(request, InfBaGelApiProtocol.ReadCaseGoals(response, request.selectedCase.scene_id, request.selectedCase.test_item_index));
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
            if ((request.goalOverrides != null || request.objectStart != null || request.humanHistory != null) && !InfBaGelGoalCoordinates.SamePlacement(request.placement, player.transform.localToWorldMatrix))
                throw new InvalidOperationException("Preview placement changed while waiting. The result was not applied; keep Source/parent transforms fixed during generation.");
            if (request.humanHistory != null && (player.IsPlaying || Mathf.Abs(player.CurrentFrame - request.humanHistory.source_frames[1]) > .001f))
                throw new InvalidOperationException("Captured source advanced while waiting. Late result not applied; pause and capture again.");
            double loadStart = clock.Elapsed.TotalMilliseconds;
            CommonMotionClip validated = InfBaGelApiProtocol.ReadMotion(json, headerId, request.selectedCase);
            InfBaGelApiProtocol.CheckGoalEcho(json, request.goalOverrides, request.objectStart != null, request.humanHistory != null);
            InfBaGelHumanHistoryProtocol.CheckOutput(validated, json, request.humanHistory, request.textOverride);
            LastHumanFramesVerified = request.humanHistory != null;
            if (LastHumanFramesVerified)
            {
                HumanHistoryStatus = "HUMAN FIXED FRAMES PASS: received body/object frames 0 and 3 match sent history. Playback starts at frame 3.";
                Debug.Log("[INFBAGEL_HUMAN] " + HumanHistoryStatus, this);
            }
            if (request.textOverride != null) Debug.Log("[INFBAGEL_TEXT] TEXT ECHO PASS (not semantic-quality validation)", this);
            InfBaGelObjectStartMetadata startMeta = InfBaGelObjectStartProtocol.CheckEcho(json, request.objectStart);
            LastInitialObjectEchoVerified = startMeta != null;
            if (startMeta != null)
            {
                LastReleasedHandContacts = startMeta.released_hand_contact_flags;
                Debug.Log("[INFBAGEL_START] OBJECT START ECHO PASS: effective initial input matches sent pose; human/scene unchanged. released_hand_contacts=" + LastReleasedHandContacts, this);
                RecordFirstObjectInputCheck(validated, request.objectStart);
            }
            LastGoalEchoVerified = request.goalOverrides != null;
            if (LastGoalEchoVerified) Debug.Log("[INFBAGEL_GOALS] GOAL ECHO PASS: effective goals match the sent values; this is not a task-success test.", this);
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
            if (request.humanHistory != null) player.SetFrame(3); // skip re-playing the older 0.1 s of history
            AdoptRuntimeJson(player, next, previous);
            LastMotionId = validated.motion_id;
            LastObjectAssetId = validated.objects[0].asset_id;
            LastFrameCount = validated.time.frame_count;
            LastFps = validated.time.fps;
            LastObjectCount = validated.objects.Length;
            if (request.goalOverrides != null)
            {
                Vector3 wantedPelvis = InfBaGelGoalCoordinates.NativeToCommon(InfBaGelGoalCoordinates.FromArray(request.goalOverrides.pelvis_goal));
                Vector3 wantedObject = InfBaGelGoalCoordinates.NativeToCommon(InfBaGelGoalCoordinates.FromArray(request.goalOverrides.object_goal));
                Vector3 dp = validated.human.frames[validated.time.frame_count - 1].pelvis_position_m - wantedPelvis;
                dp.y = 0f;
                LastFinalPelvisXZErrorM = dp.magnitude;
                LastFinalObjectErrorM = Vector3.Distance(validated.objects[0].frames[validated.time.frame_count - 1].position_m, wantedObject);
            }

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
        [ContextMenu("InfBaGel: Fetch initial object (no generation)")]
        public void FetchInitialObject()
        {
            if (!CanBegin()) return;
            try
            {
                if (targetPlayer == null) targetPlayer = GetComponent<CommonMotionPlayer>();
                if (targetPlayer == null || !targetPlayer.IsLoaded || targetPlayer.SpaceId != "infbagel_scene:" + sceneId)
                    throw new InvalidOperationException("Load this same official InfBaGel case first with overrides OFF.");
                InfBaGelGoalCoordinates.ValidatePlacement(targetPlayer.transform);
                Begin(new Pending
                {
                    fetchInitial = true,
                    player = targetPlayer,
                    selectedCase = InfBaGelApiProtocol.Request(sceneId, testItemIndex, seed),
                    url = InfBaGelObjectStartProtocol.InitialEndpoint(apiBaseUrl, sceneId, testItemIndex),
                    timeout = 60
                });
            }
            catch (Exception e) { Fail(e.Message); }
        }
        private void ClearInitialObjectMarker()
        {
            if (initialObjectMarker != null) { initialObjectMarker.gameObject.SetActive(false); Destroy(initialObjectMarker.gameObject); }
            initialObjectMarker = null; initialObjectReference = null; initialObjectPlayer = null;
            if (initialObjectWireMesh != null) Destroy(initialObjectWireMesh);
            initialObjectWireMesh = null;
        }
        private void InstallInitialObject(Pending request, InfBaGelInitialObject s)
        {
            if (request.player == null || request.player != targetPlayer || s.scene_id != sceneId || s.test_item_index != testItemIndex)
                throw new InvalidOperationException("Case/Player changed while loading initial object. Fetch again.");
            InfBaGelGoalCoordinates.ValidatePlacement(targetPlayer.transform);
            if (!targetPlayer.IsLoaded || targetPlayer.SpaceId != s.frame_id || targetPlayer.motionJson == null)
                throw new InvalidOperationException("Load a matching InfBaGel motion before creating the initial-object marker.");
            CommonMotionClip clip = JsonUtility.FromJson<CommonMotionClip>(targetPlayer.motionJson.text);
            CommonMotionValidation.Validate(clip);
            if (clip.objects.Length != 1 || clip.objects[0].asset_id != s.object_name || clip.objects[0].mesh == null)
                throw new InvalidOperationException("Loaded object mesh is not the selected case's asset. Generate that official case first.");
            ClearInitialObjectMarker(); initialObjectReference = s; initialObjectPlayer = targetPlayer;
            GameObject go = new GameObject("Initial Object Pose INPUT (not animated output)");
            go.hideFlags = HideFlags.DontSave;
            initialObjectMarker = go.transform; initialObjectMarker.SetParent(targetPlayer.transform, false);
            initialObjectMarker.localPosition = InfBaGelGoalCoordinates.NativeToCommon(InfBaGelGoalCoordinates.FromArray(s.position_m));
            initialObjectMarker.localRotation = InfBaGelObjectStartProtocol.MirrorRotation(InfBaGelObjectStartProtocol.FromArray(s.rotation_xyzw));
            initialObjectWireMesh = new Mesh { name = "Initial object reference wire mesh", hideFlags = HideFlags.DontSave };
            initialObjectWireMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            initialObjectWireMesh.vertices = clip.objects[0].mesh.vertices_m;
            initialObjectWireMesh.triangles = clip.objects[0].mesh.triangles;
            initialObjectWireMesh.RecalculateBounds();
            targetPlayer.Pause(); targetPlayer.SetFrame(0);
            InitialObjectStatus = "INITIAL OBJECT READY: wireframe is input at original pose. Human/scene/goals unchanged.";
            Status = "INITIAL OBJECT LOADED (no inference)";
            Debug.Log("[INFBAGEL_START] " + InitialObjectStatus, this);
        }
        public InfBaGelObjectStartOverride CaptureInitialObject()
        {
            if (!InitialObjectReady) throw new InvalidOperationException("Fetch Initial Object first (again after changing case/Player).");
            if (!targetPlayer.IsLoaded || targetPlayer.SpaceId != initialObjectReference.frame_id)
                throw new InvalidOperationException("Player's coordinate frame changed; reload the selected case.");
            if ((initialObjectMarker.localScale - Vector3.one).sqrMagnitude > 1e-8f)
                throw new InvalidOperationException("Do not scale the initial object. v0.3 changes pose only.");
            Vector3 p = InfBaGelGoalCoordinates.WorldToNative(targetPlayer.transform, initialObjectMarker.position);
            Quaternion q = InfBaGelObjectStartProtocol.WorldToNativeRotation(targetPlayer.transform, initialObjectMarker.rotation);
            Vector3 oldp = InfBaGelGoalCoordinates.FromArray(initialObjectReference.position_m);
            Quaternion oldq = InfBaGelObjectStartProtocol.FromArray(initialObjectReference.rotation_xyzw);
            if (Mathf.Abs(p.y - oldp.y) > 0.001f || Vector3.Distance(p, oldp) > 0.500001f)
                throw new InvalidOperationException("v0.3: horizontal shifts up to 0.50m; keep original origin height.");
            Quaternion delta = q * Quaternion.Inverse(oldq);
            if ((delta * Vector3.up - Vector3.up).magnitude > 0.001f || Quaternion.Angle(q, oldq) > 30.01f)
                throw new InvalidOperationException("v0.3: world-up yaw changes up to 30 degrees; preserve original tilt.");
            return InfBaGelObjectStartProtocol.Validate(new InfBaGelObjectStartOverride
            {
                object_name = initialObjectReference.object_name,
                position_m = InfBaGelGoalCoordinates.ToArray(p),
                rotation_xyzw = InfBaGelObjectStartProtocol.ToArray(q)
            });
        }
        public void CheckOriginalInitialObjectRoundTrip()
        {
            try
            {
                InfBaGelObjectStartOverride s = CaptureInitialObject();
                float dp = Vector3.Distance(InfBaGelGoalCoordinates.FromArray(s.position_m), InfBaGelGoalCoordinates.FromArray(initialObjectReference.position_m));
                float da = Quaternion.Angle(InfBaGelObjectStartProtocol.FromArray(s.rotation_xyzw), InfBaGelObjectStartProtocol.FromArray(initialObjectReference.rotation_xyzw));
                InitialObjectStatus = string.Format(CultureInfo.InvariantCulture, "{0}: position={1:F6}m rotation={2:F4}deg (conversion only)",
                    dp <= 0.001f && da <= 0.1f ? "ORIGINAL START ROUNDTRIP PASS" : "INITIAL OBJECT MOVED FROM ORIGINAL", dp, da);
                Debug.Log("[INFBAGEL_START] " + InitialObjectStatus, this);
            }
            catch (Exception e) { InitialObjectStatus = e.Message; Fail(e.Message); }
        }
        public void ShiftInitialObject(float localX)
        {
            if (!InitialObjectReady || IsBusy) return;
            InfBaGelGoalCoordinates.ValidatePlacement(targetPlayer.transform);
            initialObjectMarker.position += targetPlayer.transform.TransformVector(new Vector3(localX, 0, 0));
            InitialObjectStatus = "Only the INPUT object moved. Human start, goals, and scene were not moved.";
        }
        public void YawInitialObject(float degrees)
        {
            if (!InitialObjectReady || IsBusy) return;
            initialObjectMarker.rotation = Quaternion.AngleAxis(degrees, Vector3.up) * initialObjectMarker.rotation;
            InitialObjectStatus = "Only INPUT object yaw changed; original pitch/roll retained.";
        }
        private void OnDrawGizmos()
        {
            if (showInitialObjectWire && initialObjectMarker != null && initialObjectWireMesh != null)
            {
                Gizmos.color = new Color(0.2f, 1f, 0.7f, 1f);
                Gizmos.DrawWireMesh(initialObjectWireMesh, initialObjectMarker.position, initialObjectMarker.rotation, Vector3.one);
                Gizmos.DrawWireSphere(initialObjectMarker.position, 0.07f);
            }
        }
        private void RecordFirstObjectInputCheck(CommonMotionClip clip, InfBaGelObjectStartOverride s)
        {
            if (s == null) return;
            if (clip.objects.Length != 1 || clip.objects[0].asset_id != s.object_name)
                throw new InvalidOperationException("Response initial object asset differs from requested asset.");
            ObjectFrame first = clip.objects[0].frames[0];
            LastInitialObjectPositionErrorM = Vector3.Distance(first.position_m, InfBaGelGoalCoordinates.NativeToCommon(InfBaGelGoalCoordinates.FromArray(s.position_m)));
            LastInitialObjectRotationErrorDeg = Quaternion.Angle(first.rotation_xyzw, InfBaGelObjectStartProtocol.MirrorRotation(InfBaGelObjectStartProtocol.FromArray(s.rotation_xyzw)));
            LastInitialFrameVerified = LastInitialObjectPositionErrorM <= 0.001 && LastInitialObjectRotationErrorDeg <= 0.1;
            string text = string.Format(CultureInfo.InvariantCulture, "[INFBAGEL_START] FIRST OBJECT FRAME {0}: position={1:F6}m rotation={2:F4}deg. This is initial-state agreement, NOT grasp success.",
                LastInitialFrameVerified ? "PASS" : "MISMATCH", LastInitialObjectPositionErrorM, LastInitialObjectRotationErrorDeg);
            if (LastInitialFrameVerified) Debug.Log(text, this); else Debug.LogWarning(text, this);
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
