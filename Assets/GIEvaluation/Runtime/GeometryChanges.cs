using System;
using UnityEngine;

namespace GIEvaluation
{
    public enum GeometryChangeKind { Added, Removed, TransformChanged, MeshChanged, SceneReset }

    public readonly struct GeometryChange
    {
        public readonly GeometryChangeKind Kind;
        public readonly Renderer Renderer;
        public readonly Bounds PreviousBounds;
        public readonly int Revision;
        public readonly double Time;

        public GeometryChange(GeometryChangeKind kind, Renderer renderer, Bounds previousBounds, int revision)
        {
            Kind = kind;
            Renderer = renderer;
            PreviousBounds = previousBounds;
            Revision = revision;
            Time = UnityEngine.Time.realtimeSinceStartupAsDouble;
        }
    }

    // Raw scene notifications only. No GI data structure or light-transport implementation.
    public static class GeometryChanges
    {
        public static event Action<GeometryChange> Changed;
        public static int Revision { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize() { Changed = null; Revision = 0; }

        public static void Publish(GeometryChangeKind kind, Renderer renderer, Bounds previousBounds)
        {
            var change = new GeometryChange(kind, renderer, previousBounds, ++Revision);
            Changed?.Invoke(change);
            Debug.Log("[GI Fixture] " + change.Revision + " " + kind + " " + (renderer ? renderer.name : "scene"));
        }
    }
}
