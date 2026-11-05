using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Utopia.Motion
{
    /// <summary>Registers static upright BoxColliders relative to a FIXED floor frame.
    /// Not attached to an animated character. No physics objects are moved by registration.
    /// Background, goals and every initial-state provider all use this same rigid room frame.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CommonMotionSceneEnvironment : MonoBehaviour
    {
        [Tooltip("Optional convenience only. NOT needed for registration or fresh InfBaGel generation.")]
        public CommonMotionDartToInfBaGelClient dartClient;
        public CommonMotionEnvironmentGenerationClient sceneGenerationClient;
        public CommonMotionInfBaGelApiClient apiClient;
        [Header("Fixed room frame: origin on floor, Y up, scale 1 (NOT Player)")]
        public Transform roomFrame;
        [Tooltip("Optional horizontal BoxCollider. Its TOP must match roomFrame Y=0 and cover the registration XZ bounds. It is NOT exported as an obstacle.")]
        public BoxCollider floorSurface;
        [Header("Bounds in roomFrame coordinates (Y min must be 0, lengths multiple of 0.02m)")]
        public Vector3 boundsMin = new Vector3(-3,0,-4);
        public Vector3 boundsMax = new Vector3(3,2.4f,4);
        public string environmentId = "utopia_room_01";
        [Tooltip("Only these upright BoxColliders are exported. Empty list = flat floor only. EXCLUDE avatar, manipulated object, markers and debug skeleton.")]
        public BoxCollider[] backgroundBoxes = new BoxCollider[0];
        [Header("Explicit destinations in THIS room")]
        public Transform pelvisGoalMarker;
        public Transform objectGoalMarker;
        [Header("Optional point checks (inside obstacle / free space / below floor)")]
        public Transform[] probePoints = new Transform[0];
        public string Status { get; private set; } = "Register the room; any supported start provider can use this environment.";
        public string LastError { get; private set; } = "";
        public bool IsBusy { get; private set; }
        public InfBaGelEnvironmentRef Reference { get { return receipt==null ? null:receipt.environment; } }
        public bool Registered { get { return receipt!=null; } }
        private InfBaGelSceneReceipt receipt;
        private string registeredJson;
        private Matrix4x4 registeredFrame;
        private UnityWebRequest active;
        private Coroutine task;
        private string bearerToken="";
        private Vector3[] pathPreview;
        public void SetBearerToken(string value)
        {
            if(IsBusy)throw new InvalidOperationException("Request in progress.");
            if(value!=null && (value.Contains("\r")||value.Contains("\n")))throw new ArgumentException("Invalid token.");
            bearerToken=value??"";
        }
        private void Fail(string s) { LastError=s;Status="FAILED: "+s;Debug.LogError("[INFBAGEL_ENV] "+s,this); }
        private void Say(string s) { LastError="";Status=s;Debug.Log("[INFBAGEL_ENV] "+s,this); }
        private void OnDisable() { Cancel(); }
        public void Cancel()
        {
            if(task!=null){StopCoroutine(task);task=null;}
            if(active!=null){active.Abort();active.Dispose();active=null;}
            IsBusy=false;
        }
        private void Ready()
        {
            if(!UnityEngine.Application.isPlaying)throw new InvalidOperationException("Enter Play Mode.");
            if(IsBusy || (sceneGenerationClient!=null && sceneGenerationClient.IsBusy) || (dartClient!=null && dartClient.IsBusy) || (apiClient!=null && apiClient.IsBusy))throw new InvalidOperationException("Wait for other requests to finish.");
            if(apiClient==null)throw new ArgumentException("Assign the existing InfBaGel API client.");
            if(roomFrame==null)throw new ArgumentException("Assign a fixed Room Frame Empty on the floor.");
            InfBaGelGoalCoordinates.ValidatePlacement(roomFrame);
            if(apiClient.targetPlayer!=null && (roomFrame==apiClient.targetPlayer.transform || roomFrame.IsChildOf(apiClient.targetPlayer.transform)))
                throw new ArgumentException("Room Frame must NOT be Player or a child of Player.");
            if(dartClient!=null && dartClient.player!=null && (roomFrame==dartClient.player.transform || roomFrame.IsChildOf(dartClient.player.transform)))
                throw new ArgumentException("Room Frame must NOT be Player or a child of Player.");
        }
        public void CopyRememberedFrame()
        {
            try
            {
                Ready();if(dartClient==null || !dartClient.ReferenceReady)throw new InvalidOperationException("Remember the official scene in the DART client first.");
                Matrix4x4 m=dartClient.ReferencePlacement;
                roomFrame.SetPositionAndRotation(m.MultiplyPoint3x4(Vector3.zero),m.rotation);
                receipt=null;registeredJson=null;
                Say("Copied reference placement explicitly. Check that Room Frame Y=0 is the real floor TOP before registration.");
            }catch(Exception e){Fail(e.Message);}
        }
        private InfBaGelSceneBox Box(BoxCollider c,int index)
        {
            if(c==null || !c.enabled || !c.gameObject.activeInHierarchy || c.isTrigger)throw new ArgumentException("Background box "+index+" is missing, disabled or a trigger.");
            if(c==floorSurface)throw new ArgumentException("Do not include Floor Surface in Background Boxes.");
            if(c.attachedRigidbody!=null && !c.attachedRigidbody.isKinematic)throw new ArgumentException("Dynamic Rigidbody cannot be background: "+c.name);
            if(c.GetComponentInParent<Animator>()!=null || c.GetComponentInParent<CommonMotionPlayer>()!=null)throw new ArgumentException("Avatar / animated preview cannot be background: "+c.name);
            Matrix4x4 m=c.transform.localToWorldMatrix;
            Vector3 ax=m.MultiplyVector(Vector3.right), ay=m.MultiplyVector(Vector3.up), az=m.MultiplyVector(Vector3.forward);
            if(m.determinant<=0 || ax.magnitude<1e-5f || ay.magnitude<1e-5f || az.magnitude<1e-5f ||
                Mathf.Abs(Vector3.Dot(ax.normalized,ay.normalized))>1e-4f || Mathf.Abs(Vector3.Dot(ax.normalized,az.normalized))>1e-4f || Mathf.Abs(Vector3.Dot(ay.normalized,az.normalized))>1e-4f ||
                Vector3.Distance(ay.normalized,Vector3.up)>1e-4f)
                throw new ArgumentException("Box must be upright, no reflection/tilt/shear: "+c.name);
            Quaternion worldRotation=Quaternion.LookRotation(az.normalized,ay.normalized);
            Quaternion q=Quaternion.Inverse(roomFrame.rotation)*worldRotation;
            Vector3 centre=roomFrame.InverseTransformPoint(c.transform.TransformPoint(c.center));
            Vector3 size=Vector3.Scale(c.size,new Vector3(ax.magnitude,ay.magnitude,az.magnitude));
            if(size.x<.02f || size.y<.02f || size.z<.02f)throw new ArgumentException("Obstacle box thickness must be at least 2cm: "+c.name);
            return new InfBaGelSceneBox { name=index+"_"+c.name,center_m=InfBaGelSceneProtocol.Reflect(centre),rotation_xyzw=InfBaGelSceneProtocol.Reflect(q),size_m=size };
        }
        public InfBaGelSceneRegistration BuildRegistration()
        {
            if(roomFrame==null)throw new ArgumentException("Assign Room Frame.");
            InfBaGelGoalCoordinates.ValidatePlacement(roomFrame);
            if(Mathf.Abs(boundsMin.y)>1e-6f)throw new ArgumentException("Bounds Min Y must be 0 (floor top).");
            if(floorSurface!=null)
            {
                // Use actual transformed corners; no Renderer.bounds/pivot-height assumption.
                Transform t=floorSurface.transform;Vector3 centre=t.TransformPoint(floorSurface.center);
                Vector3 up=t.TransformVector(Vector3.up*floorSurface.size.y*.5f);
                if(Vector3.Distance(up.normalized,Vector3.up)>1e-4f || Mathf.Abs(roomFrame.InverseTransformPoint(centre+up).y)>.001f)
                    throw new ArgumentException("Floor Surface TOP must coincide with Room Frame Y=0 (within 1mm).");
                // Conservative exact local box membership at all four registration corners.
                foreach(float x in new float[]{boundsMin.x,boundsMax.x}) foreach(float z in new float[]{boundsMin.z,boundsMax.z})
                {
                    Vector3 local=t.InverseTransformPoint(roomFrame.TransformPoint(new Vector3(x,0,z)))-floorSurface.center;
                    if(Mathf.Abs(local.x)>floorSurface.size.x*.5f+.001f || Mathf.Abs(local.z)>floorSurface.size.z*.5f+.001f)
                        throw new ArgumentException("Floor Surface must cover the entire registration XZ bounds.");
                }
            }
            if(backgroundBoxes==null || backgroundBoxes.Length>256)throw new ArgumentException("Use at most 256 explicitly assigned background boxes.");
            HashSet<BoxCollider> unique=new HashSet<BoxCollider>();InfBaGelSceneBox[] boxes=new InfBaGelSceneBox[backgroundBoxes.Length];
            for(int i=0;i<boxes.Length;++i){if(!unique.Add(backgroundBoxes[i]))throw new ArgumentException("Duplicate background collider.");boxes[i]=Box(backgroundBoxes[i],i);}
            return new InfBaGelSceneRegistration { environment_id=environmentId,
                bounds_min_m=new Vector3(boundsMin.x,0,-boundsMax.z),bounds_max_m=new Vector3(boundsMax.x,boundsMax.y,-boundsMin.z),boxes=boxes };
        }
        public void RegisterRoom()
        {
            try { Ready();string json=JsonUtility.ToJson(BuildRegistration());if(Encoding.UTF8.GetByteCount(json)>131072)throw new ArgumentException("Scene request exceeds 128KiB.");
                Matrix4x4 atSend=roomFrame.localToWorldMatrix;task=StartCoroutine(Post("environments/register",json,(reply)=>{
                    if(!InfBaGelGoalCoordinates.SamePlacement(atSend,roomFrame.localToWorldMatrix) || json!=JsonUtility.ToJson(BuildRegistration()))throw new InvalidOperationException("Room changed during registration. Register again.");
                    var r=JsonUtility.FromJson<InfBaGelSceneReceipt>(reply);
                    if(r==null || r.environment==null || r.environment.environment_id!=environmentId || r.environment.revision==null || r.environment.revision.Length!=64 || r.kind!="unity_boxes" || r.registration==null)
                        throw new ArgumentException("Invalid environment receipt.");
                    if(r.frame_id!=r.environment.FrameId)throw new ArgumentException("Environment frame mismatch.");
                    receipt=r;registeredJson=json;registeredFrame=atSend;pathPreview=null;
                    Say("ENVIRONMENT REGISTERED: "+r.environment.environment_id+" / "+r.environment.revision.Substring(0,12)+" cells="+r.occupied_cells+". Geometry registration, not motion quality.");
                }));
            }catch(Exception e){Fail(e.Message);}
        }
        public void RequireCurrent()
        {
            if(IsBusy || receipt==null)throw new InvalidOperationException("Register the room and wait for completion.");
            if(!InfBaGelGoalCoordinates.SamePlacement(registeredFrame,roomFrame.localToWorldMatrix) || registeredJson!=JsonUtility.ToJson(BuildRegistration()))
                throw new InvalidOperationException("Room Frame / boxes / bounds changed since registration. Re-register and prepare/capture the initial state again.");
        }
        public InfBaGelGoalOverrides ReadGoals()
        {
            RequireCurrent();if(pelvisGoalMarker==null || objectGoalMarker==null)throw new ArgumentException("Assign or create both room goal markers.");
            Vector3 p=roomFrame.InverseTransformPoint(pelvisGoalMarker.position);Vector3 o=roomFrame.InverseTransformPoint(objectGoalMarker.position);
            if(Mathf.Abs(p.y)>.001f)throw new ArgumentException("Pelvis Goal is a floor projection; put it on Room Frame Y=0.");
            return new InfBaGelGoalOverrides { pelvis_goal=new float[]{p.x,0,-p.z},object_goal=new float[]{o.x,o.y,-o.z} };
        }
        public void CreateGoalsFromCase()
        {
            try
            {
                Ready();RequireCurrent();
                string scene=apiClient.sceneId;int item=apiClient.testItemIndex;
                string route="cases/"+Uri.EscapeDataString(scene)+"/items/"+item+"/goals";
                Matrix4x4 basis=roomFrame.localToWorldMatrix;
                task=StartCoroutine(Post(route,null,(reply)=>{
                    RequireRegisteredGeometry(basis);
                    if(apiClient.sceneId!=scene || apiClient.testItemIndex!=item)throw new InvalidOperationException("Selected template changed during request.");
                    var g=JsonUtility.FromJson<InfBaGelCaseGoals>(reply);
                    if(g==null || g.scene_id!=scene || g.test_item_index!=item || g.pelvis_goal==null || g.pelvis_goal.Length!=3 || g.object_goal==null || g.object_goal.Length!=3)
                        throw new ArgumentException("Invalid official goal metadata.");
                    if(pelvisGoalMarker==null){var o=new GameObject("Room Pelvis Goal (floor projection)");o.hideFlags=HideFlags.DontSave;pelvisGoalMarker=o.transform;pelvisGoalMarker.SetParent(roomFrame,false);}
                    if(objectGoalMarker==null){var o=new GameObject("Room Object Goal (motion origin, NOT bottom)");o.hideFlags=HideFlags.DontSave;objectGoalMarker=o.transform;objectGoalMarker.SetParent(roomFrame,false);}
                    pelvisGoalMarker.position=roomFrame.TransformPoint(new Vector3(g.pelvis_goal[0],0,-g.pelvis_goal[2]));
                    objectGoalMarker.position=roomFrame.TransformPoint(new Vector3(g.object_goal[0],g.object_goal[1],-g.object_goal[2]));
                    Say("Room goals initialized from template metadata (NO generation/DART required). Move them to valid locations in this room. They are independent of initial placement.");
                }));
            }catch(Exception e){Fail(e.Message);}
        }
        private void RequireRegisteredGeometry(Matrix4x4 basis)
        {
            if(receipt==null || !InfBaGelGoalCoordinates.SamePlacement(basis,roomFrame.localToWorldMatrix) || registeredJson!=JsonUtility.ToJson(BuildRegistration()))
                throw new InvalidOperationException("Room changed during operation. Re-register.");
        }
        public void QueryProbes()
        {
            try
            {
                Ready();RequireCurrent();if(probePoints==null || probePoints.Length==0)throw new ArgumentException("Assign Probe Points Empty objects inside/outside a box.");
                var p=new Vector3[probePoints.Length];for(int i=0;i<p.Length;++i){if(probePoints[i]==null)throw new ArgumentException("Missing probe.");p[i]=InfBaGelSceneProtocol.Reflect(roomFrame.InverseTransformPoint(probePoints[i].position));}
                var q=new InfBaGelSceneQuery { environment=Reference,points_m=p };
                task=StartCoroutine(Post("environments/query",JsonUtility.ToJson(q),(reply)=>{
                    var r=JsonUtility.FromJson<InfBaGelSceneQueryReply>(reply);
                    if(r==null || !InfBaGelSceneProtocol.Same(r.environment,q.environment) || r.occupied==null || r.occupied.Length!=p.Length)throw new ArgumentException("Unexpected probe response.");
                    string line="STATIC OCCUPANCY PROBES: ";for(int i=0;i<p.Length;++i)line+=probePoints[i].name+"="+(r.occupied[i]?"occupied":"free")+(r.in_bounds[i]?" ":"(outside) ");Say(line);
                }));
            }catch(Exception e){Fail(e.Message);}
        }
        public void PreviewPath()
        {
            try
            {
                Ready();RequireCurrent();
                CommonMotionPlayer pl=apiClient.targetPlayer;
                if(pl==null || !pl.IsLoaded || pl.IsPlaying)throw new InvalidOperationException("Load/pause any source clip, or use Preview validated initial A* path on the new generation client.");
                int idx=Mathf.Clamp(Mathf.FloorToInt(pl.CurrentFrame)-Mathf.RoundToInt(pl.Fps*.1f),0,pl.FrameCount-1);
                PreviewPathFromWorld(pl.transform.TransformPoint(pl.Clip.human.frames[idx].pelvis_position_m));
            }catch(Exception e){pathPreview=null;Fail(e.Message);}
        }
        public void PreviewPathFromWorld(Vector3 startWorld)
        {
            try
            {
                Ready();RequireCurrent();
                Vector3 start=roomFrame.InverseTransformPoint(startWorld);start.y=0;
                InfBaGelGoalOverrides goals=ReadGoals();
                var q=new InfBaGelScenePathRequest { environment=Reference,start_m=InfBaGelSceneProtocol.Reflect(start),goal_m=new Vector3(goals.pelvis_goal[0],0,goals.pelvis_goal[2]) };
                Matrix4x4 basis=roomFrame.localToWorldMatrix;
                task=StartCoroutine(Post("environments/path",JsonUtility.ToJson(q),(reply)=>{
                    RequireRegisteredGeometry(basis);
                    var r=JsonUtility.FromJson<InfBaGelScenePathReply>(reply);
                    if(r==null || !InfBaGelSceneProtocol.Same(r.environment,q.environment) || r.points_m==null || r.points_m.Length<2)throw new ArgumentException("Invalid path response.");
                    pathPreview=r.points_m;Say("ROOM A* PATH: "+r.length_m.ToString("F2")+"m. Pelvis navigation, NOT swept object/full-body collision validation.");
                }));
            }catch(Exception e){pathPreview=null;Fail(e.Message);}
        }
        private IEnumerator Post(string endpoint,string body,Action<string> complete)
        {
            IsBusy=true;LastError="";Status="Sending "+endpoint;
            string root=InfBaGelApiProtocol.Endpoint(apiClient.apiBaseUrl,"health");root=root.Substring(0,root.Length-6);
            UnityWebRequest r=new UnityWebRequest(root+endpoint,body==null?"GET":"POST");active=r;
            if(body!=null)r.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));r.downloadHandler=new DownloadHandlerBuffer();r.SetRequestHeader("Content-Type","application/json");r.timeout=300;
            if(!string.IsNullOrEmpty(bearerToken))r.SetRequestHeader("Authorization","Bearer "+bearerToken);
            try
            {
                var op=r.SendWebRequest();while(!op.isDone){if(r.downloadedBytes>4UL*1024UL*1024UL){r.Abort();Fail("Environment response exceeds 4MiB.");yield break;}yield return null;}
                try{if(r.result!=UnityWebRequest.Result.Success || r.responseCode!=200)throw new InvalidOperationException(InfBaGelApiProtocol.HttpError(r.responseCode,r.error,r.downloadHandler.text));complete(r.downloadHandler.text);}
                catch(Exception e){Fail(e.Message);}
            }
            finally{if(active==r){active=null;r.Dispose();}IsBusy=false;task=null;}
        }
        private void OnDrawGizmos()
        {
            if(roomFrame==null)return;
            Matrix4x4 old=Gizmos.matrix;Gizmos.matrix=roomFrame.localToWorldMatrix;Gizmos.color=Color.cyan;
            Gizmos.DrawWireCube((boundsMin+boundsMax)*.5f,boundsMax-boundsMin);Gizmos.matrix=old;
            if(receipt!=null && receipt.registration!=null)foreach(var b in receipt.registration.boxes)
            {
                Vector3 p=roomFrame.TransformPoint(InfBaGelSceneProtocol.Reflect(b.center_m));Quaternion q=roomFrame.rotation*InfBaGelSceneProtocol.Reflect(b.rotation_xyzw);
                Gizmos.matrix=Matrix4x4.TRS(p,q,Vector3.one);Gizmos.color=Color.yellow;Gizmos.DrawWireCube(Vector3.zero,b.size_m);
            }
            Gizmos.matrix=old;
            if(pathPreview!=null){Gizmos.color=Color.magenta;for(int i=1;i<pathPreview.Length;++i)
                Gizmos.DrawLine(roomFrame.TransformPoint(InfBaGelSceneProtocol.Reflect(pathPreview[i-1])+Vector3.up*.015f),roomFrame.TransformPoint(InfBaGelSceneProtocol.Reflect(pathPreview[i])+Vector3.up*.015f));}
            Gizmos.color=Color.cyan;
            if(pelvisGoalMarker!=null)Gizmos.DrawWireSphere(pelvisGoalMarker.position,.08f);
            if(objectGoalMarker!=null)Gizmos.DrawWireSphere(objectGoalMarker.position,.08f);
        }
    }
}
