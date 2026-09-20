using System;
using UnityEngine;

namespace Utopia.Motion
{
    /// <summary>Pure geometry helpers; no scene objects or Animator state are modified.</summary>
    public static class CommonMotionRetargetMath
    {
        public static Vector3[] RestPositions(MotionSkeleton skeleton)
        {
            Vector3[] result = new Vector3[22];
            for (int i = 0; i < result.Length; i++)
            {
                int parent = skeleton.parent_indices[i];
                if (parent >= i) throw new ArgumentException("Skeleton must be ordered parent first.");
                result[i] = parent < 0 ? Vector3.zero : result[parent] + skeleton.rest_offsets_m[i];
            }
            return result;
        }

        // Anatomical right and up define a right/up/forward frame independently of
        // whether the imported character faces +Z or -Z at rest.
        public static Quaternion BodyBasis(Vector3 leftHip, Vector3 rightHip, Vector3 hips, Vector3 head)
        {
            Vector3 right = (rightHip - leftHip).normalized;
            Vector3 up = head - hips;
            up -= right * Vector3.Dot(up, right);
            if (right.sqrMagnitude < 0.5f || up.sqrMagnitude < 1e-10f)
                throw new ArgumentException("Degenerate reference hips/head; cannot determine body axes.");
            up.Normalize();
            Vector3 forward = Vector3.Cross(right, up).normalized;
            return Quaternion.LookRotation(forward, up);
        }

        public static Quaternion BoneCorrection(Quaternion bodyAlignment, Quaternion targetBindRotation,
                                                 Vector3 targetDirection, Vector3 sourceDirection,
                                                 bool alignDirections)
        {
            Quaternion alignment = bodyAlignment;
            if (alignDirections && targetDirection.sqrMagnitude > 1e-10f && sourceDirection.sqrMagnitude > 1e-10f)
                alignment = Quaternion.FromToRotation(bodyAlignment * targetDirection, sourceDirection) * bodyAlignment;
            return (alignment * targetBindRotation).normalized;
        }

        // world target = world source joint * constant reference-axis correction.
        // Not a direct copy of source localRotation onto an arbitrary FBX bone.
        public static Quaternion RetargetRotation(Quaternion sourceWorld, Quaternion correction)
        {
            return (sourceWorld * correction).normalized;
        }

        public static void DecomposeRigidTRS(Matrix4x4 matrix, out Vector3 position,
                                              out Quaternion rotation, out Vector3 scale)
        {
            position = matrix.GetColumn(3);
            Vector3 x = matrix.GetColumn(0), y = matrix.GetColumn(1), z = matrix.GetColumn(2);
            scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (!IsFinite(scale.x) || !IsFinite(scale.y) || !IsFinite(scale.z) ||
                !IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z) ||
                Mathf.Min(scale.x, Mathf.Min(scale.y, scale.z)) < 1e-8f)
                throw new ArgumentException("Invalid/zero scale in target reference skeleton.");
            x /= scale.x; y /= scale.y; z /= scale.z;
            if (Mathf.Abs(Vector3.Dot(x, y)) > 0.002f || Mathf.Abs(Vector3.Dot(x, z)) > 0.002f ||
                Mathf.Abs(Vector3.Dot(y, z)) > 0.002f || Vector3.Dot(Vector3.Cross(x, y), z) < 0.998f)
                throw new ArgumentException("Mirrored or sheared bone transforms are not supported in this preview.");
            float max = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
            float min = Mathf.Min(scale.x, Mathf.Min(scale.y, scale.z));
            if ((max - min) / max > 0.002f)
                throw new ArgumentException("Non-uniform bone scale is not supported in this preview.");
            rotation = Quaternion.LookRotation(z, y);
        }

        public static bool IsFinite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
