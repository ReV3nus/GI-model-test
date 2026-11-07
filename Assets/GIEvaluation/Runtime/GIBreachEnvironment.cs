using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GIEvaluation
{
    public enum BreachLighting { SunOnly, SkyOnly, SunAndSky }

    // Lighting input for the tested GI system. This component does not calculate GI.
    public sealed class GIBreachEnvironment : MonoBehaviour
    {
        public Light sun;
        public Light[] interiorLights;
        public bool interiorLightsEnabled = true;
        public Cubemap skyRadiance;
        [Min(0)] public float skyIntensity = 1;
        public Material skyBackdrop;
        public BreachLighting lighting = BreachLighting.SunOnly;
        public event Action<GIBreachEnvironment> Changed;
        public int Revision { get; private set; }
        public float EffectiveSkyIntensity => lighting == BreachLighting.SunOnly ? 0 : skyIntensity;

        private void Awake() => Apply(lighting);
        public void Apply(BreachLighting value)
        {
            lighting = value;
            if (sun) sun.enabled = value != BreachLighting.SkyOnly;
            if (interiorLights != null)
                foreach (var light in interiorLights) if (light) light.enabled = interiorLightsEnabled;
            // A visible sky background is separate from incoming radiance used by GI.
            RenderSettings.skybox = skyBackdrop;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.ambientIntensity = 0;
            RenderSettings.reflectionIntensity = 0;
            Revision++;
            Changed?.Invoke(this);
        }
        public void Next() => Apply((BreachLighting)(((int)lighting + 1) % 3));
        public void ToggleInteriorLights() => SetInteriorLights(!interiorLightsEnabled);
        public void SetInteriorLights(bool enabled) { interiorLightsEnabled = enabled; Apply(lighting); }
    }
}
