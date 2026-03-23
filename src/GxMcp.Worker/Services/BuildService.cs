using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using GxMcp.Worker.Helpers;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Services;
using Artech.Architecture.Common.Services;
using Artech.Udm.Framework;

namespace GxMcp.Worker.Services
{
    public class BuildService
    {
        private string _msbuildPath;
        private string _gxDir;
        private KbService _kbService;

        public BuildService()
        {
            _gxDir = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
            
            string[] searchPaths = new[] {
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
                Path.Combine(_gxDir, "MSBuild.exe"),
                @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
            };

            foreach (var p in searchPaths) { if (File.Exists(p)) { _msbuildPath = p; break; } }
        }

        public void SetKbService(KbService kbService) { _kbService = kbService; }
        public KbService KbService => _kbService;

        public string Build(string action, string target)
        {
            try
            {
                var kb = _kbService.GetKB();
                if (kb != null && action.Equals("Build", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info("Attempting Native SDK Build for: " + target);
                    IBuildService buildService = null;
                    try {
                        var model = kb.DesignModel.Environment.TargetModel;
                        var method = model.GetType().GetMethod("GetService", new Type[] { typeof(Type) });
                        if (method != null) buildService = method.Invoke(model, new object[] { typeof(IBuildService) }) as IBuildService;
                    } catch { }

                    if (buildService != null)
                    {
                        KBObject obj = kb.DesignModel.Objects.Get(null, new QualifiedName(target));
                        if (obj != null)
                        {
                            buildService.BuildWithTheseOnly(new List<EntityKey> { obj.Key });
                            return "{\"status\": \"Success\", \"message\": \"Native Build triggered for " + target + "\"}";
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Warn("Native Build failed: " + ex.Message); }

            return BuildWithMSBuild(action, target);
        }

        private string BuildWithMSBuild(string action, string target)
        {
            try
            {
                if (_kbService != null)
                {
                    int waitAttempts = 0;
                    while (_kbService.IsInitializing && waitAttempts < 15)
                    {
                        System.Threading.Thread.Sleep(1000);
                        waitAttempts++;
                    }
                }

                string kbPath = GetKBPath();
                if (string.IsNullOrEmpty(kbPath)) return "{\"error\": \"KB Path not found in Environment (GX_KB_PATH)\"}";

                string tempFile = Path.Combine(Path.GetTempPath(), "GxBuild_" + Guid.NewGuid().ToString().Substring(0,8) + ".msbuild");
                var sb = new StringBuilder();
                sb.AppendLine("<Project DefaultTargets=\"Execute\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
                sb.AppendLine("  <Import Project=\"" + Path.Combine(_gxDir, "Genexus.Tasks.targets") + "\" />");
                sb.AppendLine("  <Target Name=\"Execute\">");
                sb.AppendLine("    <OpenKnowledgeBase Directory=\"" + kbPath + "\" />");
                if (action.Equals("Build", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(target))
                    sb.AppendLine("    <BuildOne BuildCalled=\"false\" CompileMains=\"false\" ObjectName=\"" + target + "\" ForceRebuild=\"false\" />");
                else if (action.Equals("RebuildAll", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("    <RebuildAll />");
                else if (action.Equals("Reorg", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("    <CheckAndInstallDatabase />");
                else sb.AppendLine("    <BuildAll />");
                sb.AppendLine("    <CloseKnowledgeBase />");
                sb.AppendLine("  </Target></Project>");
                
                File.WriteAllText(tempFile, sb.ToString());

                var startInfo = new ProcessStartInfo {
                    FileName = _msbuildPath,
                    Arguments = "/nologo /m /v:q /nodeReuse:false /target:Execute \"" + tempFile + "\"", // PERFORMANCE: /m (parallel) and /v:q (quiet) and /nodeReuse:false
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true, WorkingDirectory = _gxDir
                };

                using (var process = Process.Start(startInfo)) {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    try { File.Delete(tempFile); } catch { }
                    return Newtonsoft.Json.JsonConvert.SerializeObject(new { status = process.ExitCode == 0 ? "Success" : "Error", output = output });
                }
            }
            catch (Exception ex) { return "{\"status\": \"Error\", \"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; }
        }

        public string GetKBPath()
        {
            // UNIVERSAL: The Gateway injects this. No file searching anymore.
            return Environment.GetEnvironmentVariable("GX_KB_PATH") ?? "";
        }
    }
}
