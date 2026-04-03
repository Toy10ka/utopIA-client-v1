using UnityEngine;
using System.Linq;

public class LipSyncSimple : MonoBehaviour
{
    [Header("References")]
    public AudioSource audioSource;                 // 未指定でOK（自動検出）
    public SkinnedMeshRenderer faceRenderer;        // MTH_DEF の SkinnedMeshRenderer
    public string openShapeName = "blendShape1.MTH_A";
    public string[] idleCycleShapes = {
        "blendShape1.MTH_I","blendShape1.MTH_U","blendShape1.MTH_E","blendShape1.MTH_O"
    };

    [Header("Tuning")]
    [Range(0f, 0.1f)] public float noiseFloor = 0.01f;
    [Range(1f, 100f)] public float gain = 40f;
    [Range(0f, 0.2f)] public float smoothTime = 0.06f;
    public int sampleSize = 1024;

    [Header("Debug")]
    public bool debugLogOnStart = true;
    public bool showHUD = true;
    public KeyCode testKey = KeyCode.T;
    public bool useAudioListenerFallback = true;

    int _openIdx = -1;
    int[] _idleIdx;
    float _vel, _current;
    float[] _samples;
    float[] _listenerBuf = new float[1024];
    float _lastRms;
    bool _warnedOnce;

    void Reset()
    {
        if (!faceRenderer)
            faceRenderer = GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(r => r.name.Contains("MTH_DEF"));
    }

    void Awake()
    {
        AutoResolveAudioSource();
        ResolveBlendShapeIndices();
        _samples = new float[Mathf.Max(64, sampleSize)];
        if (debugLogOnStart) DumpSetupLog();
    }

    void AutoResolveAudioSource()
    {
        if (!audioSource) audioSource = GetComponentInParent<AudioSource>();
#if UNITY_2023_1_OR_NEWER
        if (!audioSource)
        {
            var uc = FindFirstObjectByType<UtopiaClient>();
            if (uc) audioSource = uc.GetComponent<AudioSource>();
        }
        if (!audioSource) audioSource = FindFirstObjectByType<AudioSource>();
#else
        if (!audioSource)
        {
            var uc = FindObjectOfType<UtopiaClient>();
            if (uc) audioSource = uc.GetComponent<AudioSource>();
        }
        if (!audioSource) audioSource = FindObjectOfType<AudioSource>();
#endif
    }

    void ResolveBlendShapeIndices()
    {
        if (faceRenderer && faceRenderer.sharedMesh)
        {
            var mesh = faceRenderer.sharedMesh;
            _openIdx = mesh.GetBlendShapeIndex(openShapeName);
            _idleIdx = idleCycleShapes.Select(n => mesh.GetBlendShapeIndex(n)).ToArray();
        }
        else
        {
            _openIdx = -1;
            _idleIdx = new int[0];
        }
    }

    void DumpSetupLog()
    {
        Debug.Log($"[LipSyncSimple] AudioSource: {(audioSource ? audioSource.name : "NULL")}");
        if (!faceRenderer || !faceRenderer.sharedMesh)
        {
            Debug.LogWarning("[LipSyncSimple] faceRenderer/sharedMesh が NULLです。MTH_DEF を割り当ててください。");
            return;
        }
        var mesh = faceRenderer.sharedMesh;
        Debug.Log($"[LipSyncSimple] Face Mesh: {faceRenderer.name}, BlendShapes: {mesh.blendShapeCount}");
        for (int i = 0; i < mesh.blendShapeCount; i++)
            Debug.Log($"  [{i}] {mesh.GetBlendShapeName(i)}");
        Debug.Log($"[LipSyncSimple] openShapeName='{openShapeName}' -> index={_openIdx}");
        if (_openIdx < 0) Debug.LogError("[LipSyncSimple] openShapeName が見つからないため口が動きません。Inspectorの表記を一致させてください。");
    }

    void Update()
    {
        // T を押している間は強制的に開く
        if (Input.GetKey(testKey))
        {
            _current = Mathf.SmoothDamp(_current, 1f, ref _vel, smoothTime);
            return;
        }

        float target = 0f;
        bool hasAudio = false;

        if (audioSource && audioSource.isPlaying)
        {
            if (_samples.Length != sampleSize) _samples = new float[sampleSize];
            audioSource.GetOutputData(_samples, 0);
            _lastRms = CalcRms(_samples);
            hasAudio = true;
        }
        else if (useAudioListenerFallback)
        {
            if (_listenerBuf.Length != sampleSize) _listenerBuf = new float[sampleSize];
            AudioListener.GetOutputData(_listenerBuf, 0);
            _lastRms = CalcRms(_listenerBuf);
            hasAudio = true;
        }

        if (hasAudio)
        {
            float level = Mathf.Max(0f, _lastRms - noiseFloor);
            target = Mathf.Clamp01(level * gain);
        }

        _current = Mathf.SmoothDamp(_current, target, ref _vel, smoothTime);
    }

    // ★ Animator に勝つため、ここでブレンドシェイプを適用
    void LateUpdate()
    {
        if (!faceRenderer)
        {
            if (!_warnedOnce) { Debug.LogWarning("[LipSyncSimple] faceRenderer が未設定。"); _warnedOnce = true; }
            return;
        }
        if (_openIdx < 0)
        {
            if (!_warnedOnce) { Debug.LogError("[LipSyncSimple] openShapeName のインデックスが -1。名前不一致"); _warnedOnce = true; }
            return;
        }

        // まず0にする
        faceRenderer.SetBlendShapeWeight(_openIdx, 0f);
        if (_idleIdx != null)
            foreach (var idx in _idleIdx) if (idx >= 0) faceRenderer.SetBlendShapeWeight(idx, 0f);

        // 適用（0〜100）
        float v01 = Mathf.Clamp01(_current);
        faceRenderer.SetBlendShapeWeight(_openIdx, v01 * 100f);

        if (v01 > 0.05f && _idleIdx != null && _idleIdx.Length > 0)
        {
            int pick = _idleIdx[Mathf.Abs(Time.frameCount) % _idleIdx.Length];
            if (pick >= 0) faceRenderer.SetBlendShapeWeight(pick, v01 * 20f);
        }
    }

    float CalcRms(float[] arr)
    {
        double sum = 0.0;
        for (int i = 0; i < arr.Length; i++) sum += arr[i] * arr[i];
        return Mathf.Sqrt((float)(sum / arr.Length));
    }

    void OnGUI()
    {
        if (!showHUD) return;
        GUI.Label(new Rect(10, 10, 700, 20),
            $"[LipSync] isPlaying={(audioSource ? audioSource.isPlaying : false)} RMS={_lastRms:F4} openIdx={_openIdx} current={_current:F2}");
        GUI.Label(new Rect(10, 30, 700, 20),
            $"Press [{testKey}] to FORCE mouth open (debug)");
    }
}
