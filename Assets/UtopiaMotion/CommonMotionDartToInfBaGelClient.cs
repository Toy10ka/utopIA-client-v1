using System;
using System.Collections;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Utopia.Motion
{
    /// <summary>v0.5 isolated cross-backend test, reusing the existing Player/retargeter.
    /// Remember InfBaGel scene placement first; show DART with an explicitly chosen
    /// world placement; capture two SOURCE skeleton frames; request a new interaction.
    /// Not Animator input, not contact-preserving retargeting, not live streaming.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CommonMotionDartToInfBaGelClient : MonoBehaviour
    {
        [Header("Existing components (do not replace the Player/retargeter)")]
        public CommonMotionPlayer player;
        public CommonMotionInfBaGelApiClient infBaGelClient;
        [Header("Optional saved DART JSON (or use the existing DART API client)")]
        public TextAsset dartMotionJson;
        [Header("Reference snapshot / static known object")]
        [Min(0)] public int referenceObjectFrame = 0;
        [Tooltip("contact-r2: manually inspect BOTH captured times. BOTH hands and BOTH feet must be clear of the manipulated object. This is NOT a foot-floor contact setting.")]
        public bool confirmUnheldStaticObject = false;
        [Header("Optional v0.6 registered Unity environment")]
        public bool useRegisteredEnvironment = false;
        public CommonMotionSceneEnvironment sceneEnvironment;
        [Header("Generation (case/URL/seed are taken from the existing InfBaGel client)")]
        public bool playOnResponse = true;
        public bool useTextOverride = false;
        [TextArea(2,4)] public string instruction = "Lift the clothesstand, move the clothesstand, and put down the clothesstand.";
        public UnityEvent afterMotionLoaded = new UnityEvent();
        public string Status { get; private set; } = "Remember a loaded InfBaGel scene first, then load DART.";
        public string LastError { get; private set; } = "";
        public bool IsBusy { get; private set; }
        public bool ReferenceReady { get { return referenceClip != null && sceneRoot != null && objectMarker != null; } }
        public bool HasCapture { get { return capture != null; } }
        public Transform ObjectMarker { get { return objectMarker; } }
        public double LastHttpMs { get; private set; } = -1;
        public double LastServerMs { get; private set; } = -1;
        public double LastToPlayCallMs { get; private set; } = -1;
        public bool InputValidated { get; private set; }
        public bool OutputFramesVerified { get; private set; }

        private CommonMotionClip referenceClip;
        private string referenceScene;
        private int referenceItem;
        private Matrix4x4 referencePlacement;
        public Matrix4x4 ReferencePlacement { get { return referencePlacement; } }
        public InfBaGelEffectiveGoals ReferenceGoals { get; private set; }
        private Matrix4x4 capturedTargetPlacement;
        private InfBaGelEnvironmentRef capturedEnvironment;
        private InfBaGelGoalOverrides capturedGoals;
        private bool captureUsedEnvironment;
        private Transform sceneRoot, objectMarker;
        private Mesh previewMesh;
        private InfBaGelDartHistory capture;
        private TextAsset capturedJson;
        private Matrix4x4 capturedPlacement, capturedObjectPlacement;
        private UnityWebRequest activeRequest;
        private Coroutine routine;
        private TextAsset ownedJson, previousJson;
        private string bearerToken = "";

        private void Reset() { player = GetComponent<CommonMotionPlayer>(); infBaGelClient = GetComponent<CommonMotionInfBaGelApiClient>(); }
        private void OnDisable() { CancelWaiting(); }
        private void OnDestroy()
        {
            CancelWaiting(); ClearReference();
            if (ownedJson != null) { if (player != null && player.motionJson == ownedJson) player.motionJson = previousJson; Destroy(ownedJson); }
        }
        public void SetBearerToken(string token)
        {
            if (IsBusy) throw new InvalidOperationException("Cannot change token during a request.");
            if (token != null && (token.Contains("\r") || token.Contains("\n"))) throw new ArgumentException("Invalid token.");
            bearerToken = token ?? "";
        }
        private bool ReadyToAct()
        {
            if (!UnityEngine.Application.isPlaying) { Fail("Enter Play Mode first."); return false; }
            if (IsBusy) return false;
            if (player == null || infBaGelClient == null) { Fail("Assign Player and existing InfBaGel client."); return false; }
            if (infBaGelClient.targetPlayer != player) { Fail("Use the same Player as the existing InfBaGel client."); return false; }
            if (sceneEnvironment != null && sceneEnvironment.sceneGenerationClient != null && sceneEnvironment.sceneGenerationClient.IsBusy) { Fail("Wait for the room generation client."); return false; }
            if (infBaGelClient.IsBusy) { Fail("Wait for the existing InfBaGel request to finish."); return false; }
            return true;
        }
        private void Fail(string message) { LastError = message; Status = "FAILED: " + message; Debug.LogError("[DART_TO_INFBAGEL] " + message, this); }
        private void Say(string message) { LastError = ""; Status = message; Debug.Log("[DART_TO_INFBAGEL] " + message, this); }
        private void ClearReference()
        {
            if (sceneRoot != null) { sceneRoot.gameObject.SetActive(false); Destroy(sceneRoot.gameObject); }
            if (previewMesh != null) Destroy(previewMesh);
            sceneRoot = objectMarker = null; previewMesh = null; referenceClip = null; capture = null;
        }
        public void RememberInfBaGelScene()
        {
            if (!ReadyToAct()) return;
            try
            {
                if (!player.IsLoaded || player.motionJson == null) throw new InvalidOperationException("Generate/load the known InfBaGel case first.");
                InfBaGelGenerationMetadata m = InfBaGelHumanHistoryProtocol.Metadata(player.motionJson.text);
                if (m.scene_id != infBaGelClient.sceneId || m.test_item_index != infBaGelClient.testItemIndex ||
                    player.Clip.space.frame_id != "infbagel_scene:" + m.scene_id || player.Clip.objects.Length != 1)
                    throw new InvalidOperationException("Loaded InfBaGel clip must match the selected case and have one object.");
                InfBaGelGoalCoordinates.ValidatePlacement(player.transform);
                CommonMotionClip clip = player.Clip;
                if (referenceObjectFrame >= clip.time.frame_count) throw new ArgumentException("Reference Object Frame is outside the clip.");
                ClearReference(); referenceClip = clip; referenceScene = m.scene_id; referenceItem = m.test_item_index;
                referencePlacement = player.transform.localToWorldMatrix;
                ReferenceGoals = m.goal_input == null ? null : m.goal_input.effective;
                GameObject root = new GameObject("InfBaGel scene reference v0.5 (temporary)"); root.hideFlags = HideFlags.DontSave;
                sceneRoot = root.transform; sceneRoot.SetPositionAndRotation(player.transform.position, player.transform.rotation);
                sceneRoot.localScale = Vector3.one;
                GameObject marker = new GameObject("Static known object INPUT v0.5 (move with gizmo)"); marker.hideFlags = HideFlags.DontSave;
                objectMarker = marker.transform; objectMarker.SetParent(sceneRoot, false);
                ObjectTrack o = clip.objects[0]; ObjectFrame pose = o.frames[referenceObjectFrame];
                objectMarker.localPosition = pose.position_m; objectMarker.localRotation = pose.rotation_xyzw;
                if (o.mesh != null)
                {
                    previewMesh = new Mesh { name = "Known object INPUT mesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                    previewMesh.vertices = o.mesh.vertices_m; previewMesh.triangles = o.mesh.triangles; previewMesh.RecalculateBounds(); previewMesh.RecalculateNormals();
                }
                player.Pause(); InputValidated = OutputFramesVerified = false;
                Say("SCENE REFERENCE SAVED. This is a placement frame, NOT a Unity scene occupancy export. Load DART next.");
            }
            catch (Exception e) { Fail(e.Message); }
        }
        public void LoadDartJson()
        {
            if (!ReadyToAct()) return;
            try
            {
                if (!ReferenceReady) throw new InvalidOperationException("Remember the InfBaGel scene before replacing the Player clip.");
                if (dartMotionJson == null) throw new ArgumentException("Assign the existing DART CommonMotion TextAsset, or generate with the existing DART client.");
                CommonMotionClip clip = JsonUtility.FromJson<CommonMotionClip>(dartMotionJson.text); CommonMotionValidation.Validate(clip);
                DartSourceEnvelope s = JsonUtility.FromJson<DartSourceEnvelope>(dartMotionJson.text);
                if (s == null || s.source == null || s.source.backend != "dart") throw new ArgumentException("Assigned JSON is not DART.");
                player.motionJson = dartMotionJson; player.LoadJson(dartMotionJson.text);
                player.SetFrame(Mathf.Min(Mathf.RoundToInt(clip.time.fps),clip.time.frame_count-1));
                afterMotionLoaded?.Invoke(); capture = null; InputValidated = OutputFramesVerified = false;
                objectMarker.gameObject.SetActive(true);
                Say("DART LOADED AND PAUSED. Inspect or explicitly align its placement before capture.");
            }
            catch (Exception e) { Fail(e.Message); }
        }
        public void PauseDartAtOneSecond()
        {
            if (!ReadyToAct()) return;
            try
            {
                RequireDart(); player.SetFrame(Mathf.Min(Mathf.RoundToInt(player.Fps),player.FrameCount-1)); capture = null; InputValidated = OutputFramesVerified = false;
                objectMarker.gameObject.SetActive(true);
                Say("DART PAUSED. Capture uses the source body before retarget/IK.");
            }
            catch (Exception e) { Fail(e.Message); }
        }
        private void RequireDart()
        {
            if (!ReferenceReady || !player.IsLoaded || player.motionJson == null) throw new InvalidOperationException("Remember InfBaGel placement, then load DART.");
            DartSourceEnvelope e = JsonUtility.FromJson<DartSourceEnvelope>(player.motionJson.text);
            if (e == null || e.source == null || e.source.backend != "dart") throw new InvalidOperationException("Player is not displaying a DART source.");
            if (infBaGelClient.sceneId != referenceScene || infBaGelClient.testItemIndex != referenceItem) throw new InvalidOperationException("Selected InfBaGel case changed; remember it again.");
            InfBaGelGoalCoordinates.ValidatePlacement(player.transform);
            if (!InfBaGelGoalCoordinates.SamePlacement(referencePlacement,sceneRoot.localToWorldMatrix)) throw new InvalidOperationException("Do not move the saved scene-reference root.");
        }
        private static Quaternion Heading(Quaternion q)
        {
            Vector3 f = q * Vector3.forward; f.y = 0;
            if (f.sqrMagnitude < 1e-6f) throw new ArgumentException("Cannot derive heading from near-vertical root. Choose another frame.");
            return Quaternion.LookRotation(f.normalized,Vector3.up);
        }
        public void AlignPausedDartToCaseStart()
        {
            if (!ReadyToAct()) return;
            try
            {
                RequireDart(); int i = Mathf.Clamp(Mathf.RoundToInt(player.CurrentFrame),0,player.FrameCount-1); player.SetFrame(i);
                HumanFrame src = player.Clip.human.frames[i], dst = referenceClip.human.frames[0];
                Quaternion rot = sceneRoot.rotation * Heading(dst.root_rotation_xyzw) * Quaternion.Inverse(Heading(src.root_rotation_xyzw));
                Vector3 pos = sceneRoot.TransformPoint(dst.pelvis_position_m) - rot * src.pelvis_position_m;
                player.transform.SetPositionAndRotation(pos,rot); player.SetFrame(i); capture = null; InputValidated = OutputFramesVerified = false;
                Say("DART PLACEMENT CHANGED explicitly: pelvis/heading matched to reference start. Body shape is NOT changed; this is not foot IK.");
            }
            catch (Exception e) { Fail(e.Message); }
        }
        public void CaptureDartHistory()
        {
            if (!ReadyToAct()) return;
            try
            {
                RequireDart();
                if (Vector3.Distance(objectMarker.lossyScale, Vector3.one) > .0001f) throw new InvalidOperationException("Do not scale the input object.");
                int i = Mathf.Clamp(Mathf.FloorToInt(player.CurrentFrame),0,player.FrameCount-1); player.SetFrame(i);
                captureUsedEnvironment = useRegisteredEnvironment;
                capturedEnvironment = null; capturedGoals = null;
                capturedTargetPlacement = referencePlacement;
                if (useRegisteredEnvironment)
                {
                    if (sceneEnvironment == null) throw new InvalidOperationException("Assign Scene Environment.");
                    sceneEnvironment.RequireCurrent();
                    capturedEnvironment = JsonUtility.FromJson<InfBaGelEnvironmentRef>(JsonUtility.ToJson(sceneEnvironment.Reference));
                    capturedGoals = sceneEnvironment.ReadGoals();
                    capturedTargetPlacement = sceneEnvironment.roomFrame.localToWorldMatrix;
                }
                Matrix4x4 mapping = capturedTargetPlacement.inverse * player.transform.localToWorldMatrix;
                ObjectFrame o = new ObjectFrame { position_m = capturedTargetPlacement.inverse.MultiplyPoint3x4(objectMarker.position),
                    rotation_xyzw = Quaternion.Inverse(capturedTargetPlacement.rotation) * objectMarker.rotation };
                capture = InfBaGelDartHistoryProtocol.Capture(player.Clip, player.motionJson.text, i,
                    capturedEnvironment == null ? "infbagel_scene:" + referenceScene : capturedEnvironment.FrameId,
                    mapping, referenceClip.objects[0].asset_id, o, confirmUnheldStaticObject);
                capturedJson = player.motionJson; capturedPlacement = player.transform.localToWorldMatrix;
                capturedObjectPlacement = objectMarker.localToWorldMatrix; InputValidated = OutputFramesVerified = false;
                Say("DART CAPTURED: frames " + capture.source_frames[0] + ", " + capture.source_frames[1] + "; exact body hash " + capture.body_profile.body_model_sha256 + "; static object, BOTH hands/feet clear. zero initial contact4; schema evidence is recorded in server metadata.");
            }
            catch (Exception e) { capture = null; Fail(e.Message); }
        }
        private void CheckCapture()
        {
            RequireDart();
            if (!confirmUnheldStaticObject)
                throw new InvalidOperationException("Confirm that both hands and feet are clear of the static manipulated object.");
            if (useRegisteredEnvironment != captureUsedEnvironment)
                throw new InvalidOperationException("Environment mode changed. Capture again.");
            if (captureUsedEnvironment)
            {
                if (sceneEnvironment == null) throw new InvalidOperationException("Missing Scene Environment.");
                sceneEnvironment.RequireCurrent();
                if (!InfBaGelSceneProtocol.Same(capturedEnvironment, sceneEnvironment.Reference) ||
                    !InfBaGelGoalCoordinates.SamePlacement(capturedTargetPlacement,sceneEnvironment.roomFrame.localToWorldMatrix) ||
                    JsonUtility.ToJson(capturedGoals)!=JsonUtility.ToJson(sceneEnvironment.ReadGoals()))
                    throw new InvalidOperationException("Registered room / goals changed. Capture again before generating.");
            }
            if (capture == null || player.motionJson != capturedJson || player.IsPlaying ||
                Mathf.Abs(player.CurrentFrame-capture.source_frames[1]) > .001f ||
                !InfBaGelGoalCoordinates.SamePlacement(capturedPlacement,player.transform.localToWorldMatrix) ||
                !InfBaGelGoalCoordinates.SamePlacement(capturedObjectPlacement,objectMarker.localToWorldMatrix))
                throw new InvalidOperationException("DART clip/frame/placement/object changed. Pause and capture again.");
        }
        public void ValidateDartHistory() { StartRequest(true); }
        public void GenerateFromDart() { StartRequest(false); }
        private void StartRequest(bool validateOnly)
        {
            if (!ReadyToAct()) return;
            try
            {
                CheckCapture();
                InfBaGelGenerateMotionRequest basic = InfBaGelApiProtocol.Request(referenceScene, referenceItem, infBaGelClient.seed);
                InfBaGelGoalOverrides goals = capturedGoals;
                InfBaGelDartHistory snapshot = JsonUtility.FromJson<InfBaGelDartHistory>(JsonUtility.ToJson(capture));
                string text = useTextOverride ? instruction : null;
                string body = InfBaGelSceneProtocol.AddEnvironment(InfBaGelDartHistoryProtocol.Serialize(basic, goals, snapshot, text), capturedEnvironment);
                string url = InfBaGelApiProtocol.Endpoint(infBaGelClient.apiBaseUrl, "generate_motion");
                if (validateOnly) url = url.Substring(0,url.Length-"generate_motion".Length) + "validate_human_start";
                routine = StartCoroutine(Send(url,body,basic,snapshot,text,validateOnly));
            }
            catch (Exception e) { Fail(e.Message); }
        }
        private IEnumerator Send(string url,string body,InfBaGelGenerateMotionRequest basic,InfBaGelDartHistory sent,string text,bool validateOnly)
        {
            IsBusy = true; LastError = ""; Status = validateOnly ? "VALIDATING DART INPUT (no sampling)" : "GENERATING FROM DART (source paused)";
            Stopwatch clock = Stopwatch.StartNew();
            UnityWebRequest req = new UnityWebRequest(url,"POST"); activeRequest = req;
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)); req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type","application/json"); req.timeout = Mathf.Clamp(infBaGelClient.timeoutSeconds,1,600);
            if (!string.IsNullOrEmpty(bearerToken)) req.SetRequestHeader("Authorization","Bearer " + bearerToken);
            try
            {
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (req.downloadedBytes > 64UL * 1024UL * 1024UL) { req.Abort(); Fail("Response exceeds 64 MiB."); yield break; }
                    yield return null;
                }
                LastHttpMs = clock.Elapsed.TotalMilliseconds;
                try
                {
                    if (req.result != UnityWebRequest.Result.Success || req.responseCode != 200)
                        throw new InvalidOperationException(InfBaGelApiProtocol.HttpError(req.responseCode,req.error,req.downloadHandler.text));
                    if (!InfBaGelApiProtocol.IsJsonContentType(req.GetResponseHeader("Content-Type"))) throw new InvalidOperationException("Expected JSON.");
                    CheckCapture();
                    if (validateOnly)
                    {
                        var r = JsonUtility.FromJson<InfBaGelHistoryValidation>(req.downloadHandler.text);
                        if (r == null || !r.no_inference || r.status != "HUMAN INPUT VALIDATION PASS") throw new InvalidOperationException("Unexpected input validation response.");
                        InfBaGelHumanHistoryProtocol.CheckReport(r.report,InfBaGelDartHistoryProtocol.Expected(sent));
                        InputValidated = true;
                        Say("DART INPUT VALIDATION PASS: source body preserved, core FK error=" + r.report.core_forward_max_error_m.ToString("G4") + "m. No diffusion sampling.");
                    }
                    else
                    {
                        LastServerMs = InfBaGelApiProtocol.ReadMilliseconds(req.GetResponseHeader("X-Infbagel-Service-Ms"));
                        ApplyResponse(req.downloadHandler.text,req.GetResponseHeader("X-Motion-ID"),basic,sent,text,clock);
                    }
                }
                catch (Exception e) { Fail(e.Message); }
            }
            finally { if (activeRequest == req) { activeRequest = null; req.Dispose(); } IsBusy = false; routine = null; }
        }
        private void ApplyResponse(string json,string id,InfBaGelGenerateMotionRequest basic,InfBaGelDartHistory sent,string text,Stopwatch clock)
        {
            CommonMotionClip clip = InfBaGelApiProtocol.ReadMotion(json,id,basic,capturedEnvironment == null ? null : capturedEnvironment.FrameId);
            if (capturedEnvironment != null)
            {
                InfBaGelSceneProtocol.CheckEcho(json,capturedEnvironment);
                var m = InfBaGelHumanHistoryProtocol.Metadata(json);
                if (m.goal_input==null || !m.goal_input.overrides_applied || m.goal_input.effective==null)
                    throw new InvalidOperationException("Room goals were not acknowledged.");
                for (int a=0;a<3;++a)
                    if (Mathf.Abs(m.goal_input.effective.pelvis_goal[a]-capturedGoals.pelvis_goal[a])>1e-5f ||
                        Mathf.Abs(m.goal_input.effective.object_goal[a]-capturedGoals.object_goal[a])>1e-5f)
                        throw new InvalidOperationException("Room goal echo differs from request.");
                Debug.Log("[INFBAGEL_ENV] ENVIRONMENT ECHO PASS: " + capturedEnvironment.environment_id + " / " + capturedEnvironment.revision.Substring(0,12),this);
            }
            InfBaGelHumanHistoryProtocol.CheckOutput(clip,json,InfBaGelDartHistoryProtocol.Expected(sent),text);
            DartSourceEnvelope source = JsonUtility.FromJson<DartSourceEnvelope>(json);
            if (source == null || source.source == null || source.source.body_model_sha256 != sent.body_profile.body_model_sha256)
                throw new InvalidOperationException("Response SMPL-X asset differs from the captured DART asset.");
            if (source.source.betas == null || source.source.betas.Length != sent.body_profile.betas.Length)
                throw new InvalidOperationException("Response beta count differs from DART.");
            for (int b = 0; b < sent.body_profile.betas.Length; ++b)
                if (Mathf.Abs(source.source.betas[b] - sent.body_profile.betas[b]) > 1e-7f)
                    throw new InvalidOperationException("Response body shape differs from DART.");
            for (int j = 0; j < 22; ++j)
                if (Vector3.Distance(clip.skeleton.rest_offsets_m[j], sent.body_profile.rest_offsets_m[j]) > .0005f)
                    throw new InvalidOperationException("Response rest skeleton differs from DART.");
            float worldError = Vector3.Distance(capturedPlacement.MultiplyPoint3x4(sent.samples[1].human.pelvis_position_m),
                capturedTargetPlacement.MultiplyPoint3x4(clip.human.frames[3].pelvis_position_m));
            float worldAngle = Quaternion.Angle(capturedPlacement.rotation * sent.samples[1].human.root_rotation_xyzw,
                capturedTargetPlacement.rotation * clip.human.frames[3].root_rotation_xyzw);
            if (worldError > .001f || worldAngle > .1f) throw new InvalidOperationException("World start continuity check failed.");
            Debug.Log("[DART_TO_INFBAGEL] WORLD POSE CONTINUITY PASS: pelvis=" + worldError.ToString("G4") + "m root=" + worldAngle.ToString("G4") + "deg (source skeleton, not contact quality)",this);
            Vector3 oldPosition=player.transform.position; Quaternion oldRotation=player.transform.rotation;
            float oldFrame=player.CurrentFrame; TextAsset oldJson=player.motionJson;
            TextAsset next = new TextAsset(json) { name = clip.motion_id + ".common_motion (DART to InfBaGel)", hideFlags = HideFlags.DontSave };
            try
            {
                // New clip is expressed in remembered InfBaGel scene coords. Place it
                // with THAT transform, not the former DART-local transform. World pose stays continuous.
                player.transform.SetPositionAndRotation(capturedTargetPlacement.MultiplyPoint3x4(Vector3.zero),capturedTargetPlacement.rotation);
                player.motionJson=next;player.LoadJson(json);player.SetFrame(3);afterMotionLoaded?.Invoke();
                if(!player.IsLoaded || player.MotionId!=clip.motion_id) throw new InvalidOperationException("Player did not load the response.");
            }
            catch
            {
                player.transform.SetPositionAndRotation(oldPosition,oldRotation);player.motionJson=oldJson;
                if(oldJson!=null) { player.LoadJson(oldJson.text);player.SetFrame(oldFrame); }
                Destroy(next);throw;
            }
            if (ownedJson != null) Destroy(ownedJson);
            previousJson=oldJson;ownedJson=next;
            OutputFramesVerified = true; capture = null;
            if (playOnResponse) { player.Play(); LastToPlayCallMs=clock.Elapsed.TotalMilliseconds; }
            if (objectMarker != null) objectMarker.gameObject.SetActive(false);
            Say("DART -> INFBAGEL FIXED FRAMES PASS: output frames 0/3 match mapped DART history + static object. Playback starts at 3. Not task/contact quality validation.");
        }
        public void CancelWaiting()
        {
            UnityWebRequest old = activeRequest; activeRequest = null;
            if (old != null) { old.Abort(); old.Dispose(); }
            if (routine != null) { StopCoroutine(routine); routine=null; }
            IsBusy=false;
        }
        private void OnDrawGizmos()
        {
            if(objectMarker==null || !objectMarker.gameObject.activeInHierarchy) return;
            Gizmos.color=new Color(.2f,1f,.7f,.7f);
            if(previewMesh!=null) Gizmos.DrawWireMesh(previewMesh,objectMarker.position,objectMarker.rotation,Vector3.one);
            Gizmos.DrawWireSphere(objectMarker.position,.06f);
        }
    }
}
