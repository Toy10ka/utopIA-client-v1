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
    public enum InfBaGelEnvironmentStartSource { OfficialCaseStart, CapturedInfBaGelHistory }

    /// <summary>Two additional start providers for the SAME environment registry.
    /// Does not require a DART client or a prior generated clip for OfficialCaseStart.
    /// Captured history is the source skeleton before retarget/IK, not Animator pose.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CommonMotionEnvironmentGenerationClient : MonoBehaviour
    {
        public CommonMotionSceneEnvironment sceneEnvironment;
        public CommonMotionInfBaGelApiClient apiClient;
        public CommonMotionPlayer player;
        public InfBaGelEnvironmentStartSource startSource = InfBaGelEnvironmentStartSource.OfficialCaseStart;
        [Tooltip("Optional fixed transform placing the ORIGINAL official scene coordinates in Unity. Empty = use Room Frame (explicit identity mapping). This is not a pelvis position marker.")]
        public Transform officialStartFrame;
        public bool playOnResponse = true;
        public bool useTextOverride = false;
        [TextArea(2,4)] public string instruction = "Lift the clothesstand, move the clothesstand, and put down the clothesstand.";
        public UnityEvent afterMotionLoaded = new UnityEvent();
        public string Status { get; private set; } = "Register room + create room goals. Choose OfficialCaseStart or CapturedInfBaGelHistory.";
        public string LastError { get; private set; } = "";
        public bool IsBusy { get; private set; }
        public bool HasCapture { get { return pending != null; } }
        public double LastHttpMs { get; private set; } = -1;
        public double LastServerMs { get; private set; } = -1;
        private Pending pending;
        private Coroutine routine;
        private UnityWebRequest active;
        private TextAsset ownedJson, originalAsset;
        private string bearerToken = "";
        private InfBaGelStartPreview preview;

        private sealed class Pending
        {
            public InfBaGelEnvironmentStartSource source;
            public InfBaGelGenerateMotionRequest basic;
            public InfBaGelEnvironmentRef environment;
            public InfBaGelGoalOverrides goals;
            public InfBaGelInitialStatePlacement placement;
            public InfBaGelHumanHistory history;
            public InfBaGelHumanHistory expected;
            public Matrix4x4 sourceWorld, roomWorld;
            public Transform officialTransform;
            public TextAsset sourceJson;
            public float sourceFrame;
            public string text, body;
        }
        private void Reset() { player=GetComponent<CommonMotionPlayer>();apiClient=GetComponent<CommonMotionInfBaGelApiClient>(); }
        private void OnDisable() { CancelWaiting(); }
        private void OnDestroy()
        {
            CancelWaiting();
            if(ownedJson!=null){if(player!=null && player.motionJson==ownedJson)player.motionJson=originalAsset;Destroy(ownedJson);}
        }
        public void SetBearerToken(string token)
        {
            if(IsBusy)throw new InvalidOperationException("Request in progress.");
            if(token!=null && (token.Contains("\r")||token.Contains("\n")))throw new ArgumentException("Invalid token.");
            bearerToken=token??"";
        }
        private void Say(string s){Status=s;LastError="";Debug.Log("[INFBAGEL_ROOM] "+s,this);}
        private void Fail(string s){Status="FAILED: "+s;LastError=s;Debug.LogError("[INFBAGEL_ROOM] "+s,this);}
        private void Ready()
        {
            if(!UnityEngine.Application.isPlaying)throw new InvalidOperationException("Enter Play Mode.");
            if(IsBusy)throw new InvalidOperationException("Wait for current request.");
            if(apiClient==null || player==null || sceneEnvironment==null)throw new ArgumentException("Assign Api Client, Player and Scene Environment.");
            if(apiClient.targetPlayer!=player || sceneEnvironment.apiClient!=apiClient)throw new ArgumentException("Use the SAME API client / Player for this room.");
            if(apiClient.IsBusy || sceneEnvironment.IsBusy || (sceneEnvironment.dartClient!=null && sceneEnvironment.dartClient.IsBusy))throw new InvalidOperationException("Another client is busy.");
            sceneEnvironment.RequireCurrent();
            InfBaGelGoalCoordinates.ValidatePlacement(player.transform);
            sceneEnvironment.sceneGenerationClient=this;
        }
        public void PauseAtFrame30()
        {
            try{Ready();if(!player.IsLoaded)throw new InvalidOperationException("Load a server-generated InfBaGel clip first.");
                int end=Mathf.Min(30,player.FrameCount-3);end=end/3*3;
                if(end<3)throw new InvalidOperationException("Clip is too short.");player.SetFrame(end);pending=null;Say("Paused source at frame "+end+". Capture history next.");}
            catch(Exception e){Fail(e.Message);}
        }
        public void PrepareOfficialStart()
        {
            try{Ready();startSource=InfBaGelEnvironmentStartSource.OfficialCaseStart;Capture();}
            catch(Exception e){Fail(e.Message);}
        }
        public void CaptureInfBaGelHistory()
        {
            try{Ready();startSource=InfBaGelEnvironmentStartSource.CapturedInfBaGelHistory;Capture();}
            catch(Exception e){Fail(e.Message);}
        }
        private void Capture()
        {
            Pending p=new Pending();p.source=startSource;
            p.basic=InfBaGelApiProtocol.Request(apiClient.sceneId,apiClient.testItemIndex,apiClient.seed);
            p.environment=JsonUtility.FromJson<InfBaGelEnvironmentRef>(JsonUtility.ToJson(sceneEnvironment.Reference));
            p.goals=InfBaGelApiProtocol.ValidateGoals(sceneEnvironment.ReadGoals());
            p.roomWorld=sceneEnvironment.roomFrame.localToWorldMatrix;
            p.text=useTextOverride ? instruction : null;
            if(startSource==InfBaGelEnvironmentStartSource.CapturedInfBaGelHistory)
            {
                if(!player.IsLoaded || player.motionJson==null)throw new InvalidOperationException("Load a server-generated InfBaGel clip. DART uses its existing separate client.");
                int end=Mathf.FloorToInt(player.CurrentFrame/3f)*3;
                p.history=InfBaGelHumanHistoryProtocol.Capture(player.Clip,player.motionJson.text,p.basic.scene_id,p.basic.test_item_index,end);
                player.SetFrame(end);p.sourceWorld=player.transform.localToWorldMatrix;
                p.placement=InfBaGelSceneProtocol.Placement(p.sourceWorld,sceneEnvironment.roomFrame,p.history.frame_id);
                p.expected=InfBaGelSceneProtocol.TransformExpected(p.history,p.placement);
            }
            else
            {
                if(player.IsLoaded)player.Pause(); // avoid a previous clip running during an explicit reset
                p.officialTransform=officialStartFrame;
                Transform basis=officialStartFrame!=null ? officialStartFrame : sceneEnvironment.roomFrame;
                InfBaGelGoalCoordinates.ValidatePlacement(basis);
                p.sourceWorld=basis.localToWorldMatrix;
                p.placement=InfBaGelSceneProtocol.Placement(p.sourceWorld,sceneEnvironment.roomFrame,"infbagel_scene:"+p.basic.scene_id);
            }
            p.sourceJson=player.motionJson;p.sourceFrame=player.IsLoaded?player.CurrentFrame:0;
            string body=InfBaGelHumanHistoryProtocol.Serialize(p.basic,p.goals,null,p.history,p.text);
            body=InfBaGelSceneProtocol.AddEnvironment(body,p.environment);
            p.body=InfBaGelSceneProtocol.AddPlacement(body,p.placement);
            pending=p;preview=null;
            Say(p.history==null ? "OFFICIAL START PREPARED: no DART/history required; official HUMAN + OBJECT + CONTACT template, not current idle pose. Validate to see initial joint/origin preview."
                : "HISTORY CAPTURED: frames "+p.history.source_frames[0]+", "+p.history.source_frames[1]+". Same-time raw contacts preserved by server; no noncontact restriction.");
        }
        private Pending CheckPending()
        {
            if(pending==null)throw new InvalidOperationException("Prepare official start or capture history first.");
            Pending p=pending;
            sceneEnvironment.RequireCurrent();
            if(p.source!=startSource || apiClient.sceneId!=p.basic.scene_id || apiClient.testItemIndex!=p.basic.test_item_index || apiClient.seed!=p.basic.seed ||
                p.text!=(useTextOverride?instruction:null) || !InfBaGelSceneProtocol.Same(p.environment,sceneEnvironment.Reference) ||
                !InfBaGelGoalCoordinates.SamePlacement(p.roomWorld,sceneEnvironment.roomFrame.localToWorldMatrix) ||
                JsonUtility.ToJson(p.goals)!=JsonUtility.ToJson(sceneEnvironment.ReadGoals()))throw new InvalidOperationException("Room/goals/settings changed. Prepare/capture again.");
            if(player.motionJson!=p.sourceJson || player.IsPlaying || (player.IsLoaded && Mathf.Abs(player.CurrentFrame-p.sourceFrame)>.001f))throw new InvalidOperationException("Player state changed. Prepare/capture again.");
            if(p.history!=null)
            {
                if(!InfBaGelGoalCoordinates.SamePlacement(p.sourceWorld,player.transform.localToWorldMatrix))throw new InvalidOperationException("Source placement changed. Capture again.");
            }
            else
            {
                Transform basis=officialStartFrame!=null?officialStartFrame:sceneEnvironment.roomFrame;
                if(officialStartFrame!=p.officialTransform || !InfBaGelGoalCoordinates.SamePlacement(p.sourceWorld,basis.localToWorldMatrix))throw new InvalidOperationException("Official template placement changed. Prepare again.");
            }
            return p;
        }
        public void ValidateStart()
        {
            try{Ready();Pending p=CheckPending();routine=StartCoroutine(Send(p,true));}
            catch(Exception e){Fail(e.Message);}
        }
        public void GenerateAndPlay()
        {
            try{Ready();Pending p=CheckPending();routine=StartCoroutine(Send(p,false));}
            catch(Exception e){Fail(e.Message);}
        }
        public void PreviewInitialPath()
        {
            try{Ready();CheckPending();if(preview==null || preview.joints_m==null || preview.joints_m.Length==0)throw new InvalidOperationException("Validate the start first.");
                sceneEnvironment.PreviewPathFromWorld(sceneEnvironment.roomFrame.TransformPoint(preview.joints_m[0]));}
            catch(Exception e){Fail(e.Message);}
        }
        private IEnumerator Send(Pending p,bool validation)
        {
            IsBusy=true;Status=validation?"VALIDATING INITIAL STATE...":"GENERATING IN REGISTERED ROOM...";LastError="";
            string url=InfBaGelApiProtocol.Endpoint(apiClient.apiBaseUrl,"generate_motion");
            if(validation)url=url.Substring(0,url.Length-"generate_motion".Length)+"validate_human_start";
            UnityWebRequest r=new UnityWebRequest(url,"POST");active=r;
            r.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(p.body));r.downloadHandler=new DownloadHandlerBuffer();
            r.SetRequestHeader("Content-Type","application/json");r.timeout=Mathf.Clamp(apiClient.timeoutSeconds,1,600);
            if(!string.IsNullOrEmpty(bearerToken))r.SetRequestHeader("Authorization","Bearer "+bearerToken);
            Stopwatch clock=Stopwatch.StartNew();
            try
            {
                var op=r.SendWebRequest();while(!op.isDone){if(r.downloadedBytes>64UL*1024UL*1024UL){r.Abort();Fail("Response exceeds 64 MiB.");yield break;}yield return null;}
                LastHttpMs=clock.Elapsed.TotalMilliseconds;
                try
                {
                    if(r.result!=UnityWebRequest.Result.Success || r.responseCode!=200)throw new InvalidOperationException(InfBaGelApiProtocol.HttpError(r.responseCode,r.error,r.downloadHandler.text));
                    CheckPending();
                    if(validation)
                    {
                        var v=JsonUtility.FromJson<InfBaGelEnvironmentValidation>(r.downloadHandler.text);
                        if(v==null || !v.no_inference || v.status!="HUMAN INPUT VALIDATION PASS" || v.environment==null || !InfBaGelSceneProtocol.Same(v.environment.environment,p.environment))throw new InvalidOperationException("Invalid start/environment validation response.");
                        if(p.history!=null){var old=JsonUtility.FromJson<InfBaGelHistoryValidation>(r.downloadHandler.text);InfBaGelHumanHistoryProtocol.CheckReport(old.report,p.history);}
                        else if(v.report==null || v.report.source_kind!="official_start")throw new InvalidOperationException("Server did not select the official start template.");
                        preview=v.preview;Say("INITIAL STATE + ENVIRONMENT VALIDATION PASS: "+p.source+". No sampling. Cyan points show source skeleton, not avatar/collision quality.");
                    }
                    else
                    {
                        LastServerMs=InfBaGelApiProtocol.ReadMilliseconds(r.GetResponseHeader("X-Infbagel-Service-Ms"));
                        Apply(r.downloadHandler.text,r.GetResponseHeader("X-Motion-ID"),p);
                    }
                }catch(Exception e){Fail(e.Message);}
            }
            finally{if(active==r){active=null;r.Dispose();}IsBusy=false;routine=null;}
        }
        private void Apply(string json,string id,Pending p)
        {
            CommonMotionClip clip=InfBaGelApiProtocol.ReadMotion(json,id,p.basic,p.environment.FrameId);
            InfBaGelSceneProtocol.CheckEcho(json,p.environment);
            InfBaGelGenerationMetadata m=InfBaGelHumanHistoryProtocol.Metadata(json);
            string kind=p.history==null?"official_start":"infbagel_history";
            if(m.code_revision!="unity-scene-r2" || m.start_source!=kind)throw new InvalidOperationException("Server revision/start provider mismatch. Use v0.6 unity-scene-r2.");
            if(m.history_support==null || !m.history_support.available)throw new InvalidOperationException("Server did not retain the resume contact ledger.");
            if(m.goal_input==null || !m.goal_input.overrides_applied || m.goal_input.effective==null)throw new InvalidOperationException("Missing explicit room goal acknowledgement.");
            for(int a=0;a<3;++a)if(Mathf.Abs(m.goal_input.effective.pelvis_goal[a]-p.goals.pelvis_goal[a])>1e-5f || Mathf.Abs(m.goal_input.effective.object_goal[a]-p.goals.object_goal[a])>1e-5f)throw new InvalidOperationException("Goal echo mismatch.");
            int first=0;
            if(p.history!=null)
            {
                InfBaGelHumanHistoryProtocol.CheckOutput(clip,json,p.expected,p.text);first=3;
                Vector3 before=p.sourceWorld.MultiplyPoint3x4(p.history.samples[1].human.pelvis_position_m);
                Vector3 after=p.roomWorld.MultiplyPoint3x4(clip.human.frames[3].pelvis_position_m);
                Quaternion qb=p.sourceWorld.rotation*p.history.samples[1].human.root_rotation_xyzw;
                Quaternion qa=p.roomWorld.rotation*clip.human.frames[3].root_rotation_xyzw;
                if(Vector3.Distance(before,after)>.001f || Quaternion.Angle(qb,qa)>.1f)throw new InvalidOperationException("WORLD POSE CONTINUITY check failed.");
            }
            else
            {
                var initial=m.initial_state_input;
                if(initial==null || initial.source_kind!="official_start" || initial.output_check==null || !initial.output_check.passed || initial.play_from_frame!=0)throw new InvalidOperationException("Official initial-frame preservation check missing.");
                if(p.text!=null && (m.text_input==null || !m.text_input.applied || m.text_input.effective_text!=p.text))throw new InvalidOperationException("Text echo mismatch.");
            }
            Vector3 previousPosition=player.transform.position;Quaternion previousRotation=player.transform.rotation;
            TextAsset previous=player.motionJson;float previousFrame=player.IsLoaded?player.CurrentFrame:0;
            TextAsset next=new TextAsset(json){name=clip.motion_id+".common_motion (registered room)",hideFlags=HideFlags.DontSave};
            try
            {
                player.transform.SetPositionAndRotation(p.roomWorld.MultiplyPoint3x4(Vector3.zero),p.roomWorld.rotation);
                player.motionJson=next;player.LoadJson(json);player.SetFrame(first);afterMotionLoaded?.Invoke();
                if(!player.IsLoaded || player.MotionId!=clip.motion_id)throw new InvalidOperationException("Player load failed.");
            }
            catch
            {
                player.transform.SetPositionAndRotation(previousPosition,previousRotation);player.motionJson=previous;
                if(previous!=null){player.LoadJson(previous.text);player.SetFrame(previousFrame);}Destroy(next);throw;
            }
            if(ownedJson==null)originalAsset=previous;else Destroy(ownedJson);
            ownedJson=next;pending=null;preview=null;
            if(playOnResponse)player.Play();
            Say("ENVIRONMENT ECHO + "+(p.history==null?"OFFICIAL START FIXED FRAMES":"HISTORY FIXED FRAMES + WORLD CONTINUITY")+" PASS; playback starts at "+first+". Resume is available. Not task/contact quality.");
        }
        public void CancelWaiting()
        {
            if(routine!=null){StopCoroutine(routine);routine=null;}if(active!=null){active.Abort();active.Dispose();active=null;}IsBusy=false;
        }
        private void OnDrawGizmos()
        {
            if(preview==null || sceneEnvironment==null || sceneEnvironment.roomFrame==null)return;
            Transform room=sceneEnvironment.roomFrame;Gizmos.color=Color.cyan;
            if(preview.joints_m!=null)foreach(var p in preview.joints_m)Gizmos.DrawWireSphere(room.TransformPoint(p),.02f);
            Gizmos.color=Color.yellow;Gizmos.DrawWireSphere(room.TransformPoint(preview.object_position_m),.08f);
        }
    }
}
