using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public static class WritePolicy
    {
        private static readonly Regex GenericLineErrorRegex = new Regex(
            @"^(erro|error)\s*,\s*line\s*:\s*\d+\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsUnchangedSourceWrite(string existingSource, string incomingSource) =>
            string.Equals(
                NormalizeSourceForComparison(existingSource),
                NormalizeSourceForComparison(incomingSource),
                StringComparison.Ordinal);

        public static string NormalizeSourceForComparison(string text)
        {
            if (text == null)
            {
                return string.Empty;
            }

            return text.Replace("\r\n", "\n")
                       .Replace("\r", "\n")
                       .TrimEnd('\n');
        }

        public static bool IsLogicalSourcePart(string partName)
        {
            if (string.IsNullOrWhiteSpace(partName))
            {
                return false;
            }

            return partName.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                   partName.Equals("Events", StringComparison.OrdinalIgnoreCase) ||
                   partName.Equals("Code", StringComparison.OrdinalIgnoreCase);
        }

        public static string BuildFailureDetails(string primaryMessage, JArray issues)
        {
            var details = new List<string>();

            if (!string.IsNullOrWhiteSpace(primaryMessage))
            {
                details.Add(primaryMessage.Trim());
            }

            if (issues != null)
            {
                foreach (var issue in issues.OfType<JObject>())
                {
                    string description = issue["description"]?.ToString();
                    if (string.IsNullOrWhiteSpace(description))
                    {
                        continue;
                    }

                    if (details.Any(d => string.Equals(d, description, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    details.Add(description.Trim());
                }
            }

            return string.Join(" | ", details);
        }

        public static bool ShouldRetryWithoutPartSave(string partName, string exceptionMessage, string diagnosticText)
        {
            if (!IsLogicalSourcePart(partName))
            {
                return false;
            }

            string normalizedException = exceptionMessage?.Trim() ?? string.Empty;
            string normalizedDiagnostic = diagnosticText?.Trim() ?? string.Empty;

            bool genericFailure = string.IsNullOrWhiteSpace(exceptionMessage) ||
                                  string.Equals(normalizedException, "Erro", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(normalizedException, "Error", StringComparison.OrdinalIgnoreCase) ||
                                  GenericLineErrorRegex.IsMatch(normalizedException) ||
                                  exceptionMessage.IndexOf("Part save failed: Erro", StringComparison.OrdinalIgnoreCase) >= 0;
            bool genericDiagnostics = string.IsNullOrWhiteSpace(diagnosticText) ||
                                      string.Equals(normalizedDiagnostic, "Erro", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(normalizedDiagnostic, "Error", StringComparison.OrdinalIgnoreCase) ||
                                      GenericLineErrorRegex.IsMatch(normalizedDiagnostic);
            return genericFailure && genericDiagnostics;
        }
    }
}
