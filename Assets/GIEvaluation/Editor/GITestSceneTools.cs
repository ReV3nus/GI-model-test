using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GIEvaluation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GIEvaluation.Editor
{
    public static class GITestSceneTools
    {
        public const string MainScenePath = "Assets/GIEvaluation/Scenes/GI_TestScene.unity";
        [MenuItem("GI Evaluation/Open GI Test Scene")]
        public static void OpenMainScene()
        {
            if (Application.isPlaying || SceneManager.GetActiveScene().isDirty)
                throw new InvalidOperationException("Stop Play and save current changes before opening the test scene.");
            EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        }

        private static GIBreachScenario Fixture()
        {
            var fixture=UnityEngine.Object.FindAnyObjectByType<GIBreachScenario>();if(!fixture)throw new InvalidOperationException("Open GI_TestScene first.");return fixture;
        }
        [MenuItem("GI Evaluation/Start Interactive Test")]
        public static void StartInteractiveTest()
        {
            if(!Application.isPlaying && SceneManager.GetActiveScene().path!=MainScenePath)OpenMainScene();
            var type=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if(type!=null)
            {
                var game=EditorWindow.GetWindow(type);
                var size=type.GetProperty("selectedSizeIndex",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                if(size!=null&&size.CanWrite)size.SetValue(game,0,null);
                game.Show();game.Focus();
            }
            if(!Application.isPlaying)EditorApplication.isPlaying=true;
        }
        [MenuItem("GI Evaluation/Test Scene/Next State")] public static void NextState()=>Fixture().NextState();
        [MenuItem("GI Evaluation/Test Scene/Reset")] public static void Reset()=>Fixture().ResetFixture();
        [MenuItem("GI Evaluation/Test Scene/Next Camera")] public static void NextView()=>Fixture().NextView();
        [MenuItem("GI Evaluation/Test Scene/Toggle Preview")] public static void TogglePreview(){var f=Fixture();f.SetPreview(!f.preview);}
        [MenuItem("GI Evaluation/Test Scene/Next Lighting")] public static void NextLighting()=>Fixture().environment.Next();
        [MenuItem("GI Evaluation/Test Scene/Cycle Ceiling Breach")] public static void CycleRoof()=>Fixture().CycleRoof();
        [MenuItem("GI Evaluation/Test Scene/Toggle Interior Lamps")] public static void ToggleLamps()=>Fixture().environment.ToggleInteriorLights();
        [MenuItem("GI Evaluation/Test Scene/Toggle Outside Blocker")] public static void ToggleBlocker()=>Fixture().ToggleShutter();
        [MenuItem("GI Evaluation/Test Scene/Toggle Timeline")] public static void ToggleTimeline(){if(!Application.isPlaying)throw new InvalidOperationException("Enter Play mode first.");Fixture().ToggleTimeline();}
        [MenuItem("GI Evaluation/Test Scene/Capture")] public static void Capture()=>Fixture().Capture();

        [Serializable] private sealed class Audit
        {
            public string meaning="Physics visibility checks only; no GI algorithm, accuracy or GPU performance claim.";
            public int renderers,triangles,materials,sealedEnvelopeRays,sealedEnvelopeMisses;
            public int missingScripts,missingMeshes,missingMaterials,interiorLampCount;
            public bool passed=true;public List<StateAudit> states=new List<StateAudit>();
            public List<RoofAudit> roofs=new List<RoofAudit>();
            public BlockerAudit blocker;
        }
        [Serializable] private sealed class BlockerAudit {public bool sunBefore,sunCovered,sunAfter,sameCamera,sameOpenings,sameSun,passed;}
        [Serializable] private sealed class RoofAudit {public int opening;public bool leftOpen,rightOpen,benchSun,roomBDirectSun,passed;}
        [Serializable] private sealed class StateAudit
        {
            public string state;public int roofOpening;public bool exteriorPathOpen,expectedExteriorPath,interiorPathOpen,expectedInteriorPath,passed=true;
            public List<ProbeAudit> probes=new List<ProbeAudit>();
        }
        [Serializable] private sealed class ProbeAudit {public string name,result,occluder;public bool directSun,expectedSun,passed;}
        private static bool OpenPath(Vector3 a,Vector3 b)=>!Physics.Raycast(a,(b-a).normalized,(b-a).magnitude,~0,QueryTriggerInteraction.Ignore);

        [MenuItem("GI Evaluation/Test Scene/Audit Geometry And Sun Paths")]
        public static void Validate()
        {
            var f=Fixture();if(f.TimelineRunning)throw new InvalidOperationException("Stop the timeline before auditing.");
            var original=f.state;var lighting=f.environment.lighting;bool preview=f.preview;int originalRoof=f.RoofOpening;bool interiorLamps=f.environment.interiorLightsEnabled;bool originalShutter=f.ShutterClosed;
            var audit=new Audit();
            audit.missingScripts=f.GetComponentsInChildren<MonoBehaviour>(true).Count(b=>!b);
            audit.missingMeshes=f.GetComponentsInChildren<MeshFilter>(true).Count(m=>!m.sharedMesh);
            audit.missingMaterials=f.GetComponentsInChildren<Renderer>(true).Sum(r=>r.sharedMaterials.Count(m=>!m||!m.shader));
            audit.interiorLampCount=f.environment.interiorLights.Count(l=>l);
            audit.passed=audit.missingScripts==0&&audit.missingMeshes==0&&audit.missingMaterials==0&&audit.interiorLampCount==2;
            try
            {
                f.SetPreview(false);f.environment.SetInteriorLights(false);f.environment.Apply(BreachLighting.SunOnly);f.ApplyState(BreachState.Sealed);
                foreach(var origin in new[]{new Vector3(-3,1.5f,4),new Vector3(3,1.5f,4)})
                for(int x=-1;x<=1;x++) for(int y=-1;y<=1;y++) for(int z=-1;z<=1;z++)
                {
                    if(x==0&&y==0&&z==0)continue;
                    var ray=new Ray(origin,new Vector3(x,y,z).normalized);audit.sealedEnvelopeRays++;
                    bool hit=f.envelopeColliders.Any(c=>c.Raycast(ray,out _,100));if(!hit)audit.sealedEnvelopeMisses++;
                }
                var renderers=f.GetComponentsInChildren<Renderer>(true).Where(r=>r.gameObject.activeInHierarchy).ToArray();audit.renderers=renderers.Length;audit.materials=renderers.SelectMany(r=>r.sharedMaterials).Distinct().Count();
                foreach(var filter in f.GetComponentsInChildren<MeshFilter>(true))if(filter.gameObject.activeInHierarchy&&filter.sharedMesh)audit.triangles+=filter.sharedMesh.triangles.Length/3;
                for(int i=0;i<7;i++)
                {
                    f.ApplyState((BreachState)i);Physics.SyncTransforms();
                    var row=new StateAudit{state=f.state.ToString(),roofOpening=f.RoofOpening,expectedExteriorPath=i>=1&&i<=5,expectedInteriorPath=i>=3&&i<=5};
                    row.exteriorPathOpen=OpenPath(new Vector3(-3.8f,1.55f,-1),new Vector3(-3.8f,1.55f,.8f));
                    row.interiorPathOpen=OpenPath(new Vector3(-.4f,1.3f,5.1f),new Vector3(.4f,1.3f,5.1f));
                    row.passed=row.exteriorPathOpen==row.expectedExteriorPath&&row.interiorPathOpen==row.expectedInteriorPath;
                    for(int p=0;p<f.receivers.Length;p++)
                    {
                        var receiver=f.receivers[p];var direction=-f.environment.sun.transform.forward;var origin=receiver.point.position+receiver.normal*.02f;
                        var probe=new ProbeAudit{name=receiver.name,expectedSun=receiver.name=="Direct_Control"?row.expectedExteriorPath:receiver.name=="Roof_Direct_Control"&&i>=4&&i<=5};
                        if(Vector3.Dot(receiver.normal,direction)<=0)probe.result="Back facing";
                        else if(Physics.Raycast(origin,direction,out var hit,100,~0,QueryTriggerInteraction.Ignore)){probe.result="Occluded";probe.occluder=hit.collider.name;}
                        else{probe.result="Visible";probe.directSun=true;}
                        probe.passed=probe.directSun==probe.expectedSun;row.passed&=probe.passed;row.probes.Add(probe);
                    }
                    audit.passed&=row.passed;audit.states.Add(row);
                }
                audit.passed&=audit.sealedEnvelopeMisses==0;
                f.ApplyState(BreachState.Sealed);
                for(int opening=0;opening<3;opening++)
                {
                    f.SetRoofOpening(opening);Physics.SyncTransforms();
                    var row=new RoofAudit{opening=opening};
                    row.leftOpen=OpenPath(new Vector3(-3.45f,3.35f,4.15f),new Vector3(-3.45f,4.4f,4.15f));
                    row.rightOpen=OpenPath(new Vector3(3.25f,3.35f,5.5f),new Vector3(3.25f,4.4f,5.5f));
                    bool Sun(GIReceiverSample sample)=>Vector3.Dot(sample.normal,-f.environment.sun.transform.forward)>0&&!Physics.Raycast(sample.point.position+sample.normal*.02f,-f.environment.sun.transform.forward,100,~0,QueryTriggerInteraction.Ignore);
                    row.benchSun=Sun(f.receivers.First(r=>r.name=="Roof_Direct_Control"));
                    row.roomBDirectSun=Sun(f.receivers.First(r=>r.name=="Room_B_Indirect"));
                    row.passed=row.leftOpen==(opening>0)&&row.rightOpen==(opening==2)&&row.benchSun==(opening>0)&&!row.roomBDirectSun;
                    audit.passed&=row.passed;audit.roofs.Add(row);
                }
                f.ApplyState(BreachState.LargeBreach);
                var camera=f.mainCamera.transform.position;var cameraRotation=f.mainCamera.transform.rotation;
                var facade=f.exteriorWall.sharedMesh;var inner=f.interiorWall.sharedMesh;var roof=f.roof.sharedMesh;
                float sun=f.environment.sun.intensity;var sunRotation=f.environment.sun.transform.rotation;
                var comparison=new BlockerAudit();f.SetShutter(false);comparison.sunBefore=f.SunVisibleAt("Direct_Control");
                f.SetShutter(true);comparison.sunCovered=f.SunVisibleAt("Direct_Control");
                comparison.sameCamera=f.mainCamera.transform.position==camera&&f.mainCamera.transform.rotation==cameraRotation;
                comparison.sameOpenings=f.exteriorWall.sharedMesh==facade&&f.interiorWall.sharedMesh==inner&&f.roof.sharedMesh==roof;
                comparison.sameSun=f.environment.sun.intensity==sun&&f.environment.sun.transform.rotation==sunRotation;
                f.SetShutter(false);comparison.sunAfter=f.SunVisibleAt("Direct_Control");
                comparison.passed=comparison.sunBefore&&!comparison.sunCovered&&comparison.sunAfter&&comparison.sameCamera&&comparison.sameOpenings&&comparison.sameSun;
                audit.blocker=comparison;audit.passed&=comparison.passed;
            }
            finally{f.ApplyState(original);f.SetRoofOpening(originalRoof);f.SetShutter(originalShutter);f.environment.Apply(lighting);f.environment.SetInteriorLights(interiorLamps);f.SetPreview(preview);}
            string folder=Path.GetFullPath(Path.Combine(Application.dataPath,"../Reports"));Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,"GI_Validation.json");File.WriteAllText(path,JsonUtility.ToJson(audit,true));
            if(!audit.passed)Debug.LogError("[GI Breach] Visibility audit failed. See "+path);
            else Debug.Log("[GI Breach] PASS: 52 envelope rays, seven visible presets, roof stages and controlled blocker comparison. "+path);
        }
    }
}
