using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class GatewayProcessLeaseTests
    {
        [Fact]
        public void BuildInstanceKey_ShouldNormalizeEquivalentPaths()
        {
            var configA = CreateConfig(
                @"C:\KBs\Sample\",
                @"C:\Program Files (x86)\GeneXus\GeneXus18\",
                @"C:\KBs\Sample\.gx_mirror\"
            );
            var configB = CreateConfig(
                @"c:\kbs\sample",
                @"c:\program files (x86)\genexus\genexus18",
                @"c:\kbs\sample\.gx_mirror"
            );

            var keyA = GatewayProcessLease.BuildInstanceKey(configA);
            var keyB = GatewayProcessLease.BuildInstanceKey(configB);

            Assert.Equal(keyA, keyB);
        }

        [Fact]
        public void TryRegisterCurrentProcess_ShouldRecoverStaleLease()
        {
            var config = CreateConfig(
                Path.Combine(Path.GetTempPath(), "GenexusMcpTests", Guid.NewGuid().ToString("N"), "kb"),
                Path.Combine(Path.GetTempPath(), "GenexusMcpTests", Guid.NewGuid().ToString("N"), "gx"),
                null,
                5510
            );
            var leasePath = GatewayProcessLease.GetLeasePath(GatewayProcessLease.BuildInstanceKey(config));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
                File.WriteAllText(
                    leasePath,
                    JsonConvert.SerializeObject(new GatewayLeaseRecord
                    {
                        InstanceKey = GatewayProcessLease.BuildInstanceKey(config),
                        ProcessId = 999999,
                        HttpPort = 5510,
                        KBPath = config.Environment!.KBPath!,
                        ProgramDir = config.GeneXus!.InstallationPath!,
                        ShadowPath = config.Environment.GX_SHADOW_PATH!,
                        UpdatedUtc = DateTime.UtcNow
                    })
                );

                var registration = GatewayProcessLease.TryRegisterCurrentProcess(config);

                Assert.True(registration.Success);
                Assert.NotNull(registration.Lease);
                Assert.Equal(Environment.ProcessId, registration.Lease!.ProcessId);
            }
            finally
            {
                GatewayProcessLease.ReleaseCurrentProcess(config);
                TryDelete(leasePath);
            }
        }

        [Fact]
        public void TryRegisterCurrentProcess_ShouldRejectActiveDuplicateLease()
        {
            var config = CreateConfig(
                Path.Combine(Path.GetTempPath(), "GenexusMcpTests", Guid.NewGuid().ToString("N"), "kb"),
                Path.Combine(Path.GetTempPath(), "GenexusMcpTests", Guid.NewGuid().ToString("N"), "gx"),
                null,
                5511
            );
            var leasePath = GatewayProcessLease.GetLeasePath(GatewayProcessLease.BuildInstanceKey(config));
            using var holder = StartSleeperProcess();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
                File.WriteAllText(
                    leasePath,
                    JsonConvert.SerializeObject(new GatewayLeaseRecord
                    {
                        InstanceKey = GatewayProcessLease.BuildInstanceKey(config),
                        ProcessId = holder.Id,
                        HttpPort = 5511,
                        KBPath = config.Environment!.KBPath!,
                        ProgramDir = config.GeneXus!.InstallationPath!,
                        ShadowPath = config.Environment.GX_SHADOW_PATH!,
                        UpdatedUtc = DateTime.UtcNow
                    })
                );

                var registration = GatewayProcessLease.TryRegisterCurrentProcess(config);

                Assert.False(registration.Success);
                Assert.True(registration.IsDuplicate);
                Assert.NotNull(registration.Lease);
                Assert.Equal(holder.Id, registration.Lease!.ProcessId);
            }
            finally
            {
                TryDelete(leasePath);
                TryStop(holder);
            }
        }

        private static Configuration CreateConfig(string kbPath, string installationPath, string? shadowPath, int httpPort = 5500)
        {
            var effectiveShadow = shadowPath ?? Path.Combine(kbPath, ".gx_mirror");
            return new Configuration
            {
                Server = new ServerConfig
                {
                    HttpPort = httpPort,
                    BindAddress = "127.0.0.1",
                    McpStdio = false
                },
                GeneXus = new GeneXusConfig
                {
                    InstallationPath = installationPath,
                    WorkerExecutable = "worker\\GxMcp.Worker.exe"
                },
                Environment = new EnvironmentConfig
                {
                    KBPath = kbPath,
                    GX_SHADOW_PATH = effectiveShadow
                }
            };
        }

        private static Process StartSleeperProcess()
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
            {
                throw new InvalidOperationException("Could not start sleeper process for lease test.");
            }

            return process;
        }

        private static void TryStop(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(2000);
                }
            }
            catch
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
