using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class AssetService
    {
        private readonly BuildService _buildService;

        public AssetService(BuildService buildService)
        {
            _buildService = buildService;
        }

        public string Find(string pattern, string relativeRoot = null, int limit = 20)
        {
            try
            {
                string kbRoot = RequireKbRoot();
                string rootPath = ResolvePath(kbRoot, relativeRoot, false);
                string normalizedPattern = string.IsNullOrWhiteSpace(pattern) ? "*.*" : pattern.Trim();
                int cappedLimit = Math.Max(1, Math.Min(limit, 200));

                if (!Directory.Exists(rootPath))
                {
                    return Models.McpResponse.Error("Asset root not found", relativeRoot, null, "The requested relativeRoot does not exist inside the active Knowledge Base.");
                }

                var results = Directory.EnumerateFiles(rootPath, normalizedPattern, SearchOption.AllDirectories)
                    .Take(cappedLimit)
                    .Select(path => BuildAssetDescriptor(kbRoot, path, includeContent: false))
                    .ToArray();

                return new JObject
                {
                    ["status"] = "Success",
                    ["action"] = "Find",
                    ["pattern"] = normalizedPattern,
                    ["relativeRoot"] = GetRelativePath(kbRoot, rootPath),
                    ["count"] = results.Length,
                    ["results"] = new JArray(results)
                }.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Error("Asset search failed", pattern, null, ex.Message);
            }
        }

        public string Read(string path, bool includeContent = false, int? maxBytes = null)
        {
            try
            {
                string kbRoot = RequireKbRoot();
                string fullPath = ResolvePath(kbRoot, path, true);

                if (!File.Exists(fullPath))
                {
                    return Models.McpResponse.Error("Asset not found", path, null, "The requested asset path does not exist inside the active Knowledge Base.");
                }

                long fileSize = new FileInfo(fullPath).Length;
                int effectiveMaxBytes = Math.Max(1024, Math.Min(maxBytes ?? 131072, 524288));
                if (includeContent && fileSize > effectiveMaxBytes)
                {
                    return Models.McpResponse.Error(
                        "Asset exceeds read limit",
                        path,
                        null,
                        string.Format("The asset is {0} bytes and exceeds maxBytes={1}. Read metadata only or request a smaller file.", fileSize, effectiveMaxBytes));
                }

                var asset = BuildAssetDescriptor(kbRoot, fullPath, includeContent);
                asset["includeContent"] = includeContent;
                if (includeContent)
                {
                    asset["maxBytes"] = effectiveMaxBytes;
                }

                return asset.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Error("Asset read failed", path, null, ex.Message);
            }
        }

        public string Write(string path, string contentBase64)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(contentBase64))
                {
                    return Models.McpResponse.Error("Asset content is required", path, null, "Provide contentBase64 for write operations.");
                }

                string kbRoot = RequireKbRoot();
                string fullPath = ResolvePath(kbRoot, path, true);
                byte[] content = Convert.FromBase64String(contentBase64);

                string existingHash = File.Exists(fullPath) ? ComputeSha256(File.ReadAllBytes(fullPath)) : string.Empty;

                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(fullPath, content);

                byte[] persistedBytes = File.ReadAllBytes(fullPath);
                string persistedHash = ComputeSha256(persistedBytes);
                string expectedHash = ComputeSha256(content);
                if (!string.Equals(persistedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Models.McpResponse.Error(
                        "Asset write verification failed",
                        path,
                        null,
                        "The asset was written but the persisted hash does not match the provided content.");
                }

                var result = BuildAssetDescriptor(kbRoot, fullPath, includeContent: false);
                result["status"] = "Success";
                result["action"] = "Write";
                result["previousSha256"] = existingHash;
                result["sha256"] = persistedHash;
                result["bytesWritten"] = persistedBytes.Length;
                return result.ToString();
            }
            catch (FormatException)
            {
                return Models.McpResponse.Error("Invalid base64 content", path, null, "contentBase64 is not valid Base64.");
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Error("Asset write failed", path, null, ex.Message);
            }
        }

        private string RequireKbRoot()
        {
            string kbRoot = _buildService.GetKBPath();
            if (string.IsNullOrWhiteSpace(kbRoot))
            {
                throw new InvalidOperationException("KB Path not found in Environment (GX_KB_PATH).");
            }

            string fullRoot = Path.GetFullPath(kbRoot);
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException("The configured KB path does not exist.");
            }

            return fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ResolvePath(string kbRoot, string path, bool requireFilePath)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (requireFilePath)
                {
                    throw new ArgumentException("Path is required.");
                }

                return kbRoot;
            }

            string candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(kbRoot, path));

            string rootWithSeparator = kbRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? kbRoot
                : kbRoot + Path.DirectorySeparatorChar;

            bool isSamePath = string.Equals(candidate, kbRoot, StringComparison.OrdinalIgnoreCase);
            bool isChildPath = candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
            if (!isSamePath && !isChildPath)
            {
                throw new InvalidOperationException("The requested asset path points outside the active Knowledge Base.");
            }

            return candidate;
        }

        private static JObject BuildAssetDescriptor(string kbRoot, string fullPath, bool includeContent)
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            var descriptor = new JObject
            {
                ["status"] = "Success",
                ["path"] = fullPath,
                ["relativePath"] = GetRelativePath(kbRoot, fullPath),
                ["fileName"] = Path.GetFileName(fullPath),
                ["size"] = bytes.LongLength,
                ["mimeType"] = GuessMimeType(fullPath),
                ["sha256"] = ComputeSha256(bytes)
            };

            if (includeContent)
            {
                descriptor["contentBase64"] = Convert.ToBase64String(bytes);
            }

            return descriptor;
        }

        private static string GetRelativePath(string kbRoot, string fullPath)
        {
            string normalizedRoot = kbRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return ".";
            }

            var rootUri = new Uri(AppendDirectorySeparator(kbRoot));
            var pathUri = new Uri(fullPath);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('\\', '/');
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string GuessMimeType(string path)
        {
            string extension = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
            switch (extension)
            {
                case ".xlsx":
                    return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".xls":
                    return "application/vnd.ms-excel";
                case ".csv":
                    return "text/csv";
                case ".json":
                    return "application/json";
                case ".xml":
                    return "application/xml";
                case ".txt":
                case ".log":
                    return "text/plain";
                default:
                    return "application/octet-stream";
            }
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }
    }
}
