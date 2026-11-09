using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace DynamicGI.DevBridge
{
    // Development-only automation bridge (not a GI runtime dependency).
    // Opt-in via an ENABLED marker file. Commands are text files dropped into <project>/Logs/gi_bridge/cmd/*.txt; replies go to .../out/<name>.txt.
    // A background thread only pokes the editor loop so commands are handled while the editor is unfocused.
    [InitializeOnLoad]
    internal static class GIDevBridge
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/gi_bridge"));
        private static readonly string CmdDir = Path.Combine(Root, "cmd");
        private static readonly string OutDir = Path.Combine(Root, "out");
        private static readonly string LogPath = Path.Combine(Root, "console.log");
        private static readonly string CompilePath = Path.Combine(Root, "compile.log");
        private static Thread pump;
        private static volatile bool running;
        private static readonly object logLock = new object();

        static GIDevBridge()
        {
            // Opt-in: only runs when <project>/Logs/gi_bridge/ENABLED exists (Logs/ is not version controlled).
            if (!File.Exists(Path.Combine(Root, "ENABLED"))) return;
            Directory.CreateDirectory(CmdDir); Directory.CreateDirectory(OutDir);
            running = true;
            pump = new Thread(() =>
            {
                while (running)
                {
                    try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
                    Thread.Sleep(30);
                }
            }) { IsBackground = true, Name = "GIDevBridgePump" };
            pump.Start();
            AssemblyReloadEvents.beforeAssemblyReload += () => running = false;
            EditorApplication.quitting += () => running = false;
            EditorApplication.update += Tick;
            Application.logMessageReceivedThreaded += OnLog;
            CompilationPipeline.compilationStarted += _ => WriteCompile("STARTED " + DateTime.Now.ToString("HH:mm:ss"), false);
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            WriteLog("[bridge] loaded " + DateTime.Now.ToString("HH:mm:ss.fff") + " playing=" + EditorApplication.isPlaying);
        }

        private static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ASSEMBLY " + Path.GetFileName(assembly) + " errors=" + messages.Count(m => m.type == CompilerMessageType.Error));
            foreach (var m in messages) sb.AppendLine(m.type + " " + m.file + ":" + m.line + " " + m.message);
            WriteCompile(sb.ToString(), true);
        }

        private static void WriteCompile(string text, bool append)
        {
            lock (logLock) { if (append) File.AppendAllText(CompilePath, text + "\n"); else File.WriteAllText(CompilePath, text + "\n"); }
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "][" + type + "] " + condition;
            if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
                line += "\n" + stackTrace;
            WriteLog(line);
        }

        private static void WriteLog(string line)
        {
            lock (logLock) { try { File.AppendAllText(LogPath, line + "\n"); } catch { } }
        }

        private static void Tick()
        {
            string[] files;
            try { files = Directory.GetFiles(CmdDir, "*.txt"); } catch { return; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file), text;
                try { text = File.ReadAllText(file).Trim(); File.Delete(file); } catch { continue; }
                string reply;
                try { reply = Execute(text); }
                catch (Exception e) { reply = "ERROR " + (e.InnerException ?? e); }
                try { File.WriteAllText(Path.Combine(OutDir, name + ".txt"), reply ?? ""); } catch { }
            }
        }

        private static string Execute(string text)
        {
            int space = text.IndexOf(' ');
            string verb = space < 0 ? text : text.Substring(0, space), arg = space < 0 ? "" : text.Substring(space + 1).Trim();
            switch (verb)
            {
                case "status":
                    return "compiling=" + EditorApplication.isCompiling + " updating=" + EditorApplication.isUpdating + " playing=" + EditorApplication.isPlaying +
                           " paused=" + EditorApplication.isPaused + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().path +
                           " gpu=" + SystemInfo.graphicsDeviceName + " api=" + SystemInfo.graphicsDeviceVersion;
                case "refresh":
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    return "refreshed compiling=" + EditorApplication.isCompiling;
                case "recompile":
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
                    return "recompile requested";
                case "reimport": AssetDatabase.ImportAsset(arg, ImportAssetOptions.ForceUpdate); return "reimported " + arg;
                case "play": EditorApplication.EnterPlaymode(); return "entering";
                case "stop": EditorApplication.ExitPlaymode(); return "exiting";
                case "menu": return "menu " + EditorApplication.ExecuteMenuItem(arg);
                case "invoke": return Invoke(arg);
                default: return "unknown " + verb;
            }
        }

        // invoke Namespace.Type.Method [string argument]
        private static string Invoke(string arg)
        {
            int space = arg.IndexOf(' ');
            string target = space < 0 ? arg : arg.Substring(0, space), parameter = space < 0 ? null : arg.Substring(space + 1);
            int dot = target.LastIndexOf('.');
            string typeName = target.Substring(0, dot), methodName = target.Substring(dot + 1);
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
            if (type == null) return "ERROR type not found " + typeName;
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) return "ERROR method not found " + methodName;
            var result = method.GetParameters().Length == 0 ? method.Invoke(null, null) : method.Invoke(null, new object[] { parameter });
            return result == null ? "ok" : result.ToString();
        }
    }
}
