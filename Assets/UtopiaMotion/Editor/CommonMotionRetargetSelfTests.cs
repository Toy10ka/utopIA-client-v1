using System;
using UnityEditor;
using UnityEngine;

namespace Utopia.Motion.Editor
{
    public static class CommonMotionRetargetSelfTests
    {
        private static void Near(Vector3 actual, Vector3 expected, string name)
        {
            if (Vector3.Distance(actual, expected) > 0.0001f) throw new Exception(name + ": " + actual + " != " + expected);
        }
        [MenuItem("Tools/utopIA/Run Retarget Math Self Tests")]
        public static void Run()
        {
            int count = 0;
            try
            {
                Quaternion body = CommonMotionRetargetMath.BodyBasis(Vector3.left, Vector3.right, Vector3.zero, Vector3.up);
                Near(body * Vector3.forward, Vector3.forward, "body basis"); count++;
                Quaternion bind = Quaternion.Euler(73, -24, 119);
                Quaternion pose = Quaternion.Euler(11, 52, -87);
                Quaternion alignment = Quaternion.Euler(0, 180, 0);
                Vector3 targetDirection = new Vector3(1, -.4f, .03f).normalized;
                Vector3 sourceDirection = new Vector3(-1, 0, 0);
                Quaternion c = CommonMotionRetargetMath.BoneCorrection(alignment, bind, targetDirection, sourceDirection, true);
                Quaternion world = CommonMotionRetargetMath.RetargetRotation(pose, c);
                Near(world * (Quaternion.Inverse(bind) * targetDirection), pose * sourceDirection, "bind-axis/A-pose correction"); count++;
                Quaternion parent = Quaternion.Euler(9, 22, 76);
                Near((parent * (Quaternion.Inverse(parent) * world)) * Vector3.up, world * Vector3.up, "world/local hierarchy"); count++;
                Matrix4x4 root = Matrix4x4.TRS(new Vector3(3, 1, -4), parent, Vector3.one * .8f);
                Matrix4x4 bone = Matrix4x4.TRS(new Vector3(.1f, 1, .3f), bind, Vector3.one);
                Matrix4x4 mesh = root * Matrix4x4.Translate(new Vector3(.2f, 0, 0));
                Matrix4x4 bindPose = (root * bone).inverse * mesh;
                Matrix4x4 reconstructed = root.inverse * mesh * bindPose.inverse;
                CommonMotionRetargetMath.DecomposeRigidTRS(reconstructed, out Vector3 p, out Quaternion q, out Vector3 s);
                Near(p, new Vector3(.1f, 1, .3f), "bind pose position"); count++;
                Near(q * Vector3.forward, bind * Vector3.forward, "bind pose rotation"); count++;
                Near(s, Vector3.one, "bind pose scale"); count++;
                bool rejected = false;
                try { CommonMotionRetargetMath.DecomposeRigidTRS(Matrix4x4.Scale(new Vector3(-1, 1, 1)), out _, out _, out _); }
                catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new Exception("Mirrored scale was not rejected."); count++;
                Debug.Log("[CommonMotionRetarget] PASS: " + count + " Unity math self tests. This does NOT test a real avatar or contacts.");
            }
            catch (Exception e) { Debug.LogError("[CommonMotionRetarget] Self test failed after " + count + " checks: " + e); }
        }
    }
}
