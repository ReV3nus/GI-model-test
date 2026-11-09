using DynamicGI;
using GIEvaluation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DynamicGI.EditorTools
{
    public static class DynamicGIEditorTools
    {
        [MenuItem("GI Evaluation/Dynamic GI/Install In Open Scene")]
        public static string Install()
        {
            if (Application.isPlaying) return "ERROR stop play mode first";
            var existing = Object.FindAnyObjectByType<DynamicGISystem>(FindObjectsInactive.Include);
            if (existing) return "already installed on " + existing.name;
            var fixture = Object.FindAnyObjectByType<GIBreachScenario>();
            var go = new GameObject("Dynamic GI System");
            var system = go.AddComponent<DynamicGISystem>();
            system.cascades = DynamicGISystem.DefaultCascades();
            Undo.RegisterCreatedObjectUndo(go, "Install Dynamic GI");
            EditorSceneManager.MarkSceneDirty(go.scene);
            EditorSceneManager.SaveScene(go.scene);
            return "installed (fixture " + (fixture ? "found" : "missing") + ")";
        }
    }
}
