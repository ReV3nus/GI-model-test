using UnityEditor;
using UnityEngine;

namespace DynamicGI.Editor
{
    public static class GIVerificationMenu
    {
        [MenuItem("GI Evaluation/Dynamic GI/Run Recorded Verification")]
        public static void Run() => GIVerification.StartRun();

        [MenuItem("GI Evaluation/Dynamic GI/Run World Transport Checks")]
        public static void Transport() => GITransportChecks.StartRun(8192);

        [MenuItem("GI Evaluation/Dynamic GI/Run Generic Geometry Checks")]
        public static void Geometry() => GIGeometryChecks.StartRun();

        [MenuItem("GI Evaluation/Dynamic GI/Stop Recorded Verification")]
        public static void Stop()
        {
            var recorder = Object.FindAnyObjectByType<GIVerification>();
            if (recorder) recorder.StopRun();
        }
    }
}
