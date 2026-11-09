using System.IO;
using System.Text;
using GIEvaluation;
using UnityEngine;

namespace DynamicGI.EditorTools
{
    // Single-step actions used during development and by the evaluation runner (invoked through the dev bridge).
    public static class DynamicGIDev
    {
        public static string OutputFolder => Path.GetFullPath(Path.Combine(Application.dataPath, "../Captures/DynamicGI"));

        public static string Act(string args)
        {
            var fixture = Object.FindAnyObjectByType<GIBreachScenario>();
            var gi = DynamicGISystem.Active;
            var parts = (args ?? "").Split(' ');
            var sb = new StringBuilder();
            switch (parts[0])
            {
                case "state": fixture.StopTimeline(); fixture.ApplyState((BreachState)(int.Parse(parts[1]) - 1)); break;
                case "blocker": fixture.SetShutter(parts[1] == "on"); break;
                case "light": fixture.environment.Apply((BreachLighting)System.Enum.Parse(typeof(BreachLighting), parts[1])); break;
                case "lamps": fixture.environment.SetInteriorLights(parts[1] == "on"); break;
                case "preview": fixture.SetPreview(parts[1] == "on"); break;
                case "view": fixture.SetView(int.Parse(parts[1])); break;
                case "gi": gi.giEnabled = parts[1] == "on"; break;
                case "debug": gi.debugView = (GIDebugView)int.Parse(parts[1]); break;
                case "capture": return Capture(parts[1], parts.Length > 2 ? int.Parse(parts[2]) : 1920, parts.Length > 3 ? int.Parse(parts[3]) : 1080);
                case "info": break;
                case "sky":
                {
                    var cube = fixture.environment.skyRadiance;
                    for (int f = 0; f < 6; f++)
                    {
                        var px = cube.GetPixels((CubemapFace)f, 0);
                        float mx = 0, sum = 0; Color mc = Color.black;
                        foreach (var c in px) { float l = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f; sum += l; if (l > mx) { mx = l; mc = c; } }
                        sb.Append($"face{f} avg={sum / px.Length:F3} max={mx:F3} maxColor={mc}\n");
                    }
                    return sb.ToString();
                }
                case "set":
                {
                    var f = typeof(DynamicGISystem).GetField(parts[1]);
                    object v = f.FieldType == typeof(int) ? int.Parse(parts[2]) : f.FieldType == typeof(bool) ? (object)bool.Parse(parts[2]) : float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                    f.SetValue(gi, v); break;
                }
                default: return "unknown " + parts[0];
            }
            sb.Append("ok frame=").Append(Time.frameCount);
            if (gi) sb.Append($" gi={gi.giEnabled} inst={gi.StatInstances} tris={gi.StatTriangles} probes={gi.StatProbes} gpuProbe={gi.GpuProbeMs:F2} albedo={gi.GpuAlbedoMs:F2} comp={gi.GpuCompositeMs:F2} cpu={gi.CpuUpdateMs:F2} rebuild={gi.LastRebuildMs:F2} lights={gi.StatLights} kernels={gi.KernelGpuMs[0]:F2}/{gi.KernelGpuMs[1]:F2}/{gi.KernelGpuMs[2]:F2}/{gi.KernelGpuMs[3]:F2}");
            return sb.ToString();
        }

        public static string Capture(string name, int width, int height)
        {
            var fixture = Object.FindAnyObjectByType<GIBreachScenario>();
            var cam = fixture ? fixture.mainCamera : Camera.main;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevTarget;
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            Directory.CreateDirectory(OutputFolder);
            string path = Path.Combine(OutputFolder, name + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex); rt.Release(); Object.DestroyImmediate(rt);
            return path;
        }
    }
}
