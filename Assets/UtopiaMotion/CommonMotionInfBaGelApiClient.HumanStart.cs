using System;
using UnityEngine;

namespace Utopia.Motion
{
    // Partial extension of the EXISTING component. Do not add another component.
    public sealed partial class CommonMotionInfBaGelApiClient
    {
        [Header("Optional v0.4: captured CommonMotion body + object history")]
        [Tooltip("Source skeleton BEFORE retarget/IK, not Unity-chan's rendered/physics pose. Requires a new v0.4 motion.")]
        public bool useHumanHistory = false;
        [Header("Optional v0.4: text condition (known-object movement task)")]
        public bool useTextOverride = false;
        [TextArea(2, 4)] public string instruction = "Lift the clothesstand, move the clothesstand, and put down the clothesstand.";
        public string HumanHistoryStatus { get; private set; } = "Generate v0.4 baseline; pause at frame 30 and capture history.";
        public bool LastHumanInputValidated { get; private set; }
        public bool LastHumanFramesVerified { get; private set; }
        private InfBaGelHumanHistory capturedHistory;
        private TextAsset capturedHistoryJson;
        private Matrix4x4 capturedHistoryPlacement;
        private CommonMotionPlayer capturedHistoryPlayer;
        public bool HumanHistoryReady { get { return capturedHistory != null && capturedHistoryPlayer == targetPlayer &&
            targetPlayer != null && targetPlayer.IsLoaded && targetPlayer.motionJson == capturedHistoryJson &&
            targetPlayer.MotionId == capturedHistory.source_motion_id && !targetPlayer.IsPlaying &&
            Mathf.Abs(targetPlayer.CurrentFrame - capturedHistory.source_frames[1]) < .001f; } }

        public void PauseAtFrame30()
        {
            try
            {
                if (!CanBegin() || targetPlayer == null || !targetPlayer.IsLoaded)
                    throw new InvalidOperationException("Load the baseline first.");
                targetPlayer.SetFrame(Mathf.Min(30, targetPlayer.FrameCount - 3));
                HumanHistoryStatus = "Paused. Capture the displayed source history next.";
            }
            catch (Exception e) { Fail(e.Message); }
        }

        public void CaptureHumanHistory()
        {
            try
            {
                if (!CanBegin() || targetPlayer == null || !targetPlayer.IsLoaded || targetPlayer.motionJson == null)
                    throw new InvalidOperationException("Load a new v0.4 baseline first.");
                InfBaGelGoalCoordinates.ValidatePlacement(targetPlayer.transform);
                // Diagnostic capture pauses/snaps visibly to a native 10 Hz knot.
                // This is not continuous, zero-latency, moving-world capture.
                int end = Mathf.FloorToInt(targetPlayer.CurrentFrame / 3f) * 3;
                InfBaGelHumanHistory h = InfBaGelHumanHistoryProtocol.Capture(targetPlayer.Clip,
                    targetPlayer.motionJson.text, sceneId, testItemIndex, end);
                targetPlayer.SetFrame(end);
                capturedHistory = h; capturedHistoryJson = targetPlayer.motionJson;
                capturedHistoryPlayer = targetPlayer;
                capturedHistoryPlacement = targetPlayer.transform.localToWorldMatrix;
                LastHumanInputValidated = LastHumanFramesVerified = false;
                HumanHistoryStatus = "CAPTURED frames " + h.source_frames[0] + ", " + h.source_frames[1] +
                    " (0.1 s apart). Body + object are paused together; raw contact state stays on server.";
                Debug.Log("[INFBAGEL_HUMAN] " + HumanHistoryStatus, this);
            }
            catch (Exception e) { HumanHistoryStatus = e.Message; Fail(e.Message); }
        }

        private InfBaGelHumanHistory RequireHumanHistory()
        {
            if (!HumanHistoryReady || capturedHistory.frame_id != "infbagel_scene:" + sceneId ||
                !InfBaGelGoalCoordinates.SamePlacement(capturedHistoryPlacement, targetPlayer.transform.localToWorldMatrix))
                throw new InvalidOperationException("Capture again: pose/frame/clip/scene/placement changed. Keep the source paused while waiting.");
            if (useObjectStartOverride)
                throw new InvalidOperationException("Turn Use Object Start Override OFF; human history already includes same-time object history.");
            // JSON round-trip makes an immutable request snapshot of the small input.
            return JsonUtility.FromJson<InfBaGelHumanHistory>(JsonUtility.ToJson(capturedHistory));
        }

        public void ValidateHumanHistory()
        {
            if (!CanBegin()) return;
            try
            {
                InfBaGelHumanHistory h = RequireHumanHistory();
                InfBaGelGenerateMotionRequest basic = InfBaGelApiProtocol.Request(sceneId, testItemIndex, seed);
                string url = InfBaGelApiProtocol.Endpoint(apiBaseUrl, "generate_motion");
                url = url.Substring(0, url.Length - "generate_motion".Length) + "validate_human_start";
                Begin(new Pending { validateHuman = true, humanHistory = h, player = targetPlayer,
                    selectedCase = basic, url = url, timeout = Mathf.Clamp(timeoutSeconds, 1, 600),
                    body = InfBaGelHumanHistoryProtocol.Serialize(basic, null, null, h, null),
                    expectedMotionId = targetPlayer.MotionId, expectedJson = targetPlayer.motionJson,
                    placement = targetPlayer.transform.localToWorldMatrix });
            }
            catch (Exception e) { HumanHistoryStatus = e.Message; Fail(e.Message); }
        }

        private void ReceiveHumanValidation(Pending request, string json)
        {
            if (!HumanHistoryReady || request.expectedJson != targetPlayer.motionJson ||
                !InfBaGelGoalCoordinates.SamePlacement(request.placement, targetPlayer.transform.localToWorldMatrix))
                throw new InvalidOperationException("Source changed during validation. Capture again.");
            InfBaGelHistoryValidation validation = JsonUtility.FromJson<InfBaGelHistoryValidation>(json);
            if (validation == null || !validation.no_inference || validation.status != "HUMAN INPUT VALIDATION PASS")
                throw new InvalidOperationException("Invalid human-input validation response.");
            InfBaGelHumanHistoryProtocol.CheckReport(validation.report, request.humanHistory);
            LastHumanInputValidated = true;
            HumanHistoryStatus = "HUMAN INPUT VALIDATION PASS; SMPL-X core error=" +
                validation.report.core_forward_max_error_m.ToString("G4") + " m. No diffusion sampling run.";
            Status = HumanHistoryStatus;
            Debug.Log("[INFBAGEL_HUMAN] " + HumanHistoryStatus, this);
        }
    }
}
