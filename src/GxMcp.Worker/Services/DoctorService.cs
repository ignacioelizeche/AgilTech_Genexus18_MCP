using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Services
{
    public class DoctorService
    {
        public string Diagnose(string logPath)
        {
            try
            {
                // Try provided path, then default
                if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
                {
                    logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "msbuild.log");
                }

                if (!File.Exists(logPath))
                    return "{\"status\": \"No build log found. Run a build first.\"}";

                string[] logContent = File.ReadAllLines(logPath);
                var errors = logContent
                    .Where(l => Regex.IsMatch(l, @"error\s*:\s*(spc\w+|rpg\w+)", RegexOptions.IgnoreCase))
                    .ToArray();

                if (errors.Length == 0)
                    return "{\"status\": \"Healthy. No specification errors found.\"}";

                // Diagnose each error
                var diagnoses = errors.Select(err =>
                {
                    var match = Regex.Match(err, @"(spc\w+|rpg\w+)", RegexOptions.IgnoreCase);
                    string code = match.Success ? match.Groups[1].Value : "unknown";
                    string prescription = GetPrescription(code);
                    string severity = GetSeverity(code);
                    return "{\"code\":\"" + code + "\",\"severity\":\"" + severity + "\",\"line\":\"" + CommandDispatcher.EscapeJsonString(err.Trim()) + "\",\"prescription\":\"" + CommandDispatcher.EscapeJsonString(prescription) + "\"}";
                });

                return "{\"errorCount\":" + errors.Length + ",\"diagnoses\":[" + string.Join(",", diagnoses) + "]}";
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string GetPrescription(string code)
        {
            switch (code.ToLower())
            {
                case "spc0096": return "Variable not defined. Check variable name spelling or define it in Variables part.";
                case "spc0055": return "Type mismatch. The variable or attribute type does not match the expected type (e.g., Character vs Numeric).";
                case "spc0001": return "Syntax error. Check for missing semi-colons ';', unclosed blocks (if/endif), or invalid command structure.";
                case "spc0017": return "Attribute not found. Verify if the attribute exists in the KB and is active.";
                case "spc0084": return "Attribute not instantiated. You are using an attribute that is not present in the For Each base table context.";
                case "spc0038": return "Table not found. The table specified in 'Defined by' or Transaction does not exist.";
                case "spc0053": return "Rule not satisfied. A call to this object is missing required parameters defined in 'Parm'.";
                case "spc0082": return "Object not found. You are trying to Call() or reference an object that doesn't exist.";
                case "spc0075": return "Operand type mismatch. Arithmetic or string operation on incompatible types.";
                case "rpg0002": return "Report generation error. Check layout and dataset consistency.";
                default: return "Consult GeneXus Wiki for error code " + code;
            }
        }
        private string GetSeverity(string code)
        {
            switch (code.ToLower())
            {
                case "spc0001": // Syntax
                case "spc0038": // Table not found
                case "spc0082": // Object not found
                    return "Critical";
                case "spc0096": // Undefined var
                case "spc0055": // Type mismatch
                case "spc0084": // Att not instantiated
                case "spc0053": // Parm mismatch
                    return "High";
                default: return "Medium";
            }
        }
    }
}
