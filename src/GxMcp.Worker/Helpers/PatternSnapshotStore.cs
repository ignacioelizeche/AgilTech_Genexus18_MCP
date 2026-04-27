using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GxMcp.Worker.Helpers
{
    public static class PatternSnapshotStore
    {
        private const int MaxSnapshotsPerObject = 10;

        private static string Root
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
                return Path.Combine(local, "GenexusMCP", "pattern-cache");
            }
        }

        public static string SaveSnapshot(string objectGuid, string partName, string xml)
        {
            if (string.IsNullOrWhiteSpace(objectGuid) || string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var dir = Path.Combine(Root, Sanitize(objectGuid));
                Directory.CreateDirectory(dir);

                var hash = Hash(xml);
                if (LatestHashEquals(dir, hash)) return null;

                var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
                var fileName = stamp + "_" + (partName ?? "Part") + "_" + hash.Substring(0, 8) + ".xml";
                var path = Path.Combine(dir, fileName);
                File.WriteAllText(path, xml, new UTF8Encoding(false));

                Prune(dir);
                return path;
            }
            catch (Exception ex)
            {
                Logger.Debug("[PatternSnapshotStore] Save failed: " + ex.Message);
                return null;
            }
        }

        public static List<string> List(string objectGuid)
        {
            var dir = Path.Combine(Root, Sanitize(objectGuid ?? string.Empty));
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.EnumerateFiles(dir, "*.xml")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToList();
        }

        public static string ReadSnapshot(string path)
        {
            try { return File.ReadAllText(path); }
            catch { return null; }
        }

        private static bool LatestHashEquals(string dir, string hash)
        {
            try
            {
                var latest = Directory.EnumerateFiles(dir, "*.xml")
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (latest == null) return false;
                var stem = Path.GetFileNameWithoutExtension(latest);
                var idx = stem.LastIndexOf('_');
                if (idx < 0) return false;
                var existingShort = stem.Substring(idx + 1);
                return string.Equals(existingShort, hash.Substring(0, Math.Min(8, hash.Length)), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void Prune(string dir)
        {
            try
            {
                var files = Directory.EnumerateFiles(dir, "*.xml")
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .ToList();
                for (int i = MaxSnapshotsPerObject; i < files.Count; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        private static string Hash(string s)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string Sanitize(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
