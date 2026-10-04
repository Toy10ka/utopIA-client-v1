using System;
using UnityEngine;

namespace Utopia.Motion
{
    /// <summary>
    /// Input counterpart of commonmotion/export_motion.py OBJECT_TO_COMMON=diag(1,1,-1).
    /// Native scene Y-up RH point <-> CommonMotion local Unity LH point <-> placed world.
    /// Never use the avatar/root bone transform here: use CommonMotionPlayer.transform.
    /// </summary>
    public static class InfBaGelGoalCoordinates
    {
        public const string NativeFrame = "infbagel_scene_y_up_m";
        public const string CommonBasis = "unity_lh_y_up_z_forward";
        public const float PositionToleranceM = 0.001f;
        public static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
        public static void CheckPoint(Vector3 p)
        {
            if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z))
                throw new ArgumentException("Goal/placement contains a non-finite coordinate.");
        }
        public static Vector3 FromArray(float[] values)
        {
            if (values == null || values.Length != 3) throw new ArgumentException("Expected a three-number goal array.");
            Vector3 p = new Vector3(values[0], values[1], values[2]); CheckPoint(p); return p;
        }
        public static float[] ToArray(Vector3 p) { CheckPoint(p); return new[] { p.x, p.y, p.z }; }
        public static Vector3 NativeToCommon(Vector3 p) { CheckPoint(p); return new Vector3(p.x, p.y, -p.z); }
        public static Vector3 CommonToNative(Vector3 p) { return NativeToCommon(p); }

        public static void ValidatePlacement(Transform placement)
        {
            if (placement == null) throw new ArgumentException("Assign CommonMotionPlayer (not the Animator) as the placement reference.");
            ValidatePlacement(placement.localToWorldMatrix);
        }
        public static void ValidatePlacement(Matrix4x4 m)
        {
            for (int r = 0; r < 4; ++r) for (int c = 0; c < 4; ++c)
                    if (!Finite(m[r, c])) throw new ArgumentException("Non-finite placement matrix.");
            Vector3 x = m.MultiplyVector(Vector3.right), y = m.MultiplyVector(Vector3.up), z = m.MultiplyVector(Vector3.forward);
            const float e = 0.0002f;
            if (Mathf.Abs(x.magnitude - 1) > e || Mathf.Abs(y.magnitude - 1) > e || Mathf.Abs(z.magnitude - 1) > e ||
                Mathf.Abs(Vector3.Dot(x, y)) > e || Mathf.Abs(Vector3.Dot(x, z)) > e || Mathf.Abs(Vector3.Dot(y, z)) > e ||
                Vector3.Dot(Vector3.Cross(x, y), z) < 1 - e)
                throw new ArgumentException("Goal-input v0.2 requires unit scale, no reflection/shear (including parent transforms). Do not rescale the preview to fix avatar size.");
            if ((y - Vector3.up).magnitude > e)
                throw new ArgumentException("Goal-input v0.2 allows translation + yaw only. Pitch/roll or tilted floors are outside this test.");
            if (Mathf.Abs(m[3, 0]) + Mathf.Abs(m[3, 1]) + Mathf.Abs(m[3, 2]) + Mathf.Abs(m[3, 3] - 1) > e)
                throw new ArgumentException("Expected an affine placement matrix.");
        }
        public static Vector3 NativeToWorld(Transform placement, Vector3 point)
        {
            ValidatePlacement(placement); return placement.TransformPoint(NativeToCommon(point));
        }
        public static Vector3 WorldToNative(Transform placement, Vector3 point)
        {
            ValidatePlacement(placement); CheckPoint(point);
            return CommonToNative(placement.InverseTransformPoint(point));
        }
        public static bool SamePlacement(Matrix4x4 a, Matrix4x4 b)
        {
            for (int r = 0; r < 4; ++r) for (int c = 0; c < 4; ++c)
                    if (!Finite(a[r, c]) || !Finite(b[r, c]) || Mathf.Abs(a[r, c] - b[r, c]) > 0.0001f) return false;
            return true;
        }
    }
}
