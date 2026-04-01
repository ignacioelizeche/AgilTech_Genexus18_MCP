using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace GxMcp.Gateway
{
    internal static class GatewayProcessLease
    {
        internal static readonly TimeSpan LeaseStaleAfter = TimeSpan.FromSeconds(45);
        private const string LeaseFolderName = "GenexusMCP\\gateway-leases";

        internal static string BuildInstanceKey(Configuration config)
        {
            string kbPath = NormalizePath(config.Environment?.KBPath);
            string programDir = NormalizePath(config.GeneXus?.InstallationPath);
            string shadowPath = NormalizePath(
                config.Environment?.GX_SHADOW_PATH ??
                (string.IsNullOrWhiteSpace(kbPath) ? string.Empty : Path.Combine(kbPath, ".gx_mirror"))
            );
            int port = config.Server?.HttpPort ?? 0;

            return $"port={port}|kb={kbPath}|program={programDir}|shadow={shadowPath}";
        }

        internal static string GetLeasePath(string instanceKey)
        {
            string hash = ComputeHash(instanceKey);
            return Path.Combine(GetLeaseDirectory(), $"{hash}.json");
        }

        internal static LeaseRegistrationResult TryRegisterCurrentProcess(Configuration config)
        {
            return TryRegisterInternal(config, false);
        }

        internal static LeaseRegistrationResult ForceRegisterCurrentProcess(Configuration config)
        {
            return TryRegisterInternal(config, true);
        }

        private static LeaseRegistrationResult TryRegisterInternal(Configuration config, bool force)
        {
            string instanceKey = BuildInstanceKey(config);
            string leasePath = GetLeasePath(instanceKey);
            using var mutex = CreateMutex(instanceKey);

            if (!TryEnterMutex(mutex))
            {
                return LeaseRegistrationResult.Blocked(instanceKey, leasePath, null, "lease_mutex_timeout");
            }

            try
            {
                var existing = TryReadLeaseFile(leasePath);
                if (!force && existing != null && existing.ProcessId != Environment.ProcessId && IsLeaseActive(existing))
                {
                    return LeaseRegistrationResult.Duplicate(instanceKey, leasePath, existing);
                }

                var portConflict = !force ? FindActiveLeaseForPort(config.Server?.HttpPort ?? 0, Environment.ProcessId, leasePath) : null;
                if (portConflict != null)
                {
                    Program.Log($"[Gateway] duplicate_instance_prevented existingPid={portConflict.ProcessId} port={portConflict.HttpPort} existingKey={portConflict.InstanceKey}");
                    return LeaseRegistrationResult.Duplicate(instanceKey, leasePath, portConflict);
                }

                if (existing != null && existing.ProcessId != Environment.ProcessId)
                {
                    Program.Log($"[Gateway] lease_recovered key={instanceKey} previousPid={existing.ProcessId} forced={force}");
                }

                DeleteLeasesOwnedByCurrentProcessExcept(leasePath);
                var current = CreateLeaseRecord(config, instanceKey);
                WriteLeaseFile(leasePath, current);
                return LeaseRegistrationResult.Registered(instanceKey, leasePath, current);
            }
            finally
            {
                ReleaseMutex(mutex);
            }
        }

        internal static void RefreshCurrentProcess(Configuration config)
        {
            string instanceKey = BuildInstanceKey(config);
            string leasePath = GetLeasePath(instanceKey);
            using var mutex = CreateMutex(instanceKey);
            if (!TryEnterMutex(mutex)) return;

            try
            {
                DeleteLeasesOwnedByCurrentProcessExcept(leasePath);
                var current = TryReadLeaseFile(leasePath);
                if (current == null || current.ProcessId != Environment.ProcessId)
                {
                    current = CreateLeaseRecord(config, instanceKey);
                }
                else
                {
                    current.UpdatedUtc = DateTime.UtcNow;
                }

                WriteLeaseFile(leasePath, current);
            }
            finally
            {
                ReleaseMutex(mutex);
            }
        }

        internal static void ReleaseCurrentProcess(Configuration config)
        {
            try
            {
                foreach (var leasePath in Directory.GetFiles(GetLeaseDirectory(), "*.json"))
                {
                    var current = TryReadLeaseFile(leasePath);
                    if (current != null && current.ProcessId == Environment.ProcessId && File.Exists(leasePath))
                    {
                        File.Delete(leasePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[Gateway] Failed to release lease: {ex.Message}");
            }
        }

        internal static GatewayLeaseRecord? TryReadLease(Configuration config)
        {
            return TryReadLeaseFile(GetLeasePath(BuildInstanceKey(config)));
        }

        internal static bool IsLeaseActive(GatewayLeaseRecord lease)
        {
            if ((DateTime.UtcNow - lease.UpdatedUtc) > LeaseStaleAfter)
            {
                return false;
            }

            return IsProcessAlive(lease.ProcessId);
        }

        internal static bool IsProcessAlive(int processId)
        {
            if (processId <= 0) return false;

            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static GatewayLeaseRecord CreateLeaseRecord(Configuration config, string instanceKey)
        {
            string kbPath = NormalizePath(config.Environment?.KBPath);
            string programDir = NormalizePath(config.GeneXus?.InstallationPath);
            string shadowPath = NormalizePath(
                config.Environment?.GX_SHADOW_PATH ??
                (string.IsNullOrWhiteSpace(kbPath) ? string.Empty : Path.Combine(kbPath, ".gx_mirror"))
            );

            return new GatewayLeaseRecord
            {
                InstanceKey = instanceKey,
                ProcessId = Environment.ProcessId,
                HttpPort = config.Server?.HttpPort ?? 0,
                KBPath = kbPath,
                ProgramDir = programDir,
                ShadowPath = shadowPath,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private static string GetLeaseDirectory()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LeaseFolderName
            );
            Directory.CreateDirectory(root);
            return root;
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
            }
            catch
            {
                return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
            }
        }

        private static string ComputeHash(string value)
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte valueByte in bytes)
            {
                builder.Append(valueByte.ToString("x2"));
            }

            return builder.ToString();
        }

        private static GatewayLeaseRecord? TryReadLeaseFile(string leasePath)
        {
            try
            {
                if (!File.Exists(leasePath)) return null;
                string json = File.ReadAllText(leasePath, Encoding.UTF8);
                return JsonConvert.DeserializeObject<GatewayLeaseRecord>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void WriteLeaseFile(string leasePath, GatewayLeaseRecord record)
        {
            string json = JsonConvert.SerializeObject(record, Formatting.Indented);
            File.WriteAllText(leasePath, json, Encoding.UTF8);
        }

        private static GatewayLeaseRecord? FindActiveLeaseForPort(int httpPort, int currentProcessId, string currentLeasePath)
        {
            if (httpPort <= 0)
            {
                return null;
            }

            try
            {
                foreach (var leasePath in Directory.GetFiles(GetLeaseDirectory(), "*.json"))
                {
                    if (string.Equals(leasePath, currentLeasePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var lease = TryReadLeaseFile(leasePath);
                    if (lease == null || lease.ProcessId == currentProcessId)
                    {
                        continue;
                    }

                    if (lease.HttpPort != httpPort)
                    {
                        continue;
                    }

                    if (IsLeaseActive(lease))
                    {
                        return lease;
                    }

                    TryDeleteLease(leasePath, lease.ProcessId);
                }
            }
            catch
            {
            }

            return null;
        }

        private static void DeleteLeasesOwnedByCurrentProcessExcept(string keepLeasePath)
        {
            try
            {
                foreach (var leasePath in Directory.GetFiles(GetLeaseDirectory(), "*.json"))
                {
                    if (string.Equals(leasePath, keepLeasePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var lease = TryReadLeaseFile(leasePath);
                    if (lease != null && lease.ProcessId == Environment.ProcessId)
                    {
                        TryDeleteLease(leasePath, lease.ProcessId);
                    }
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteLease(string leasePath, int processId)
        {
            try
            {
                if (File.Exists(leasePath))
                {
                    File.Delete(leasePath);
                    Program.Log($"[Gateway] lease_recovered pid={processId} path={leasePath}");
                }
            }
            catch
            {
            }
        }

        private static System.Threading.Mutex CreateMutex(string instanceKey)
        {
            string hash = ComputeHash(instanceKey);
            return new System.Threading.Mutex(false, $"Local\\GenexusMcpGatewayLease_{hash}");
        }

        private static bool TryEnterMutex(System.Threading.Mutex mutex)
        {
            try
            {
                return mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        private static void ReleaseMutex(System.Threading.Mutex mutex)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
            }
        }
    }

    internal sealed class GatewayLeaseRecord
    {
        [JsonProperty("instanceKey")]
        public string InstanceKey { get; set; } = string.Empty;

        [JsonProperty("processId")]
        public int ProcessId { get; set; }

        [JsonProperty("httpPort")]
        public int HttpPort { get; set; }

        [JsonProperty("kbPath")]
        public string KBPath { get; set; } = string.Empty;

        [JsonProperty("programDir")]
        public string ProgramDir { get; set; } = string.Empty;

        [JsonProperty("shadowPath")]
        public string ShadowPath { get; set; } = string.Empty;

        [JsonProperty("updatedUtc")]
        public DateTime UpdatedUtc { get; set; }
    }

    internal sealed class LeaseRegistrationResult
    {
        public bool Success { get; private set; }
        public bool IsDuplicate { get; private set; }
        public string InstanceKey { get; private set; } = string.Empty;
        public string LeasePath { get; private set; } = string.Empty;
        public GatewayLeaseRecord? Lease { get; private set; }
        public string FailureReason { get; private set; } = string.Empty;

        public static LeaseRegistrationResult Registered(string instanceKey, string leasePath, GatewayLeaseRecord lease) => new LeaseRegistrationResult
        {
            Success = true,
            InstanceKey = instanceKey,
            LeasePath = leasePath,
            Lease = lease
        };

        public static LeaseRegistrationResult Duplicate(string instanceKey, string leasePath, GatewayLeaseRecord lease) => new LeaseRegistrationResult
        {
            Success = false,
            IsDuplicate = true,
            InstanceKey = instanceKey,
            LeasePath = leasePath,
            Lease = lease,
            FailureReason = "duplicate_instance"
        };

        public static LeaseRegistrationResult Blocked(string instanceKey, string leasePath, GatewayLeaseRecord? lease, string failureReason) => new LeaseRegistrationResult
        {
            Success = false,
            IsDuplicate = false,
            InstanceKey = instanceKey,
            LeasePath = leasePath,
            Lease = lease,
            FailureReason = failureReason
        };
    }
}
