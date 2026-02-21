using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Services;
using Artech.Genexus.Common.Objects;
using Artech.Common.Collections;
using Artech.Genexus.Common;

namespace GxMcp.Worker.Helpers
{
    public static class VariableInjector
    {
        public static void InjectVariables(KBObject obj, string code)
        {
            var variablesPart = obj.Parts.Get<VariablesPart>();
            if (variablesPart == null) return;

            var matches = System.Text.RegularExpressions.Regex.Matches(code, @"&(\w+)");
            var varNames = matches.Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            foreach (var varName in varNames)
            {
                if (!variablesPart.Variables.Any(v => v.Name.Equals(varName, StringComparison.OrdinalIgnoreCase)))
                {
                    global::Artech.Genexus.Common.Variable v = CreateVariable(variablesPart, varName);
                    if (v != null)
                    {
                        variablesPart.Variables.Add(v);
                        Logger.Info($"Injected variable: {varName} into {obj.Name}");
                    }
                }
            }
        }

        private static global::Artech.Genexus.Common.Variable CreateVariable(VariablesPart part, string name)
        {
            global::Artech.Genexus.Common.Variable v = new global::Artech.Genexus.Common.Variable(part);
            v.Name = name;

            // Type setting disabled due to missing eDB2Type definition in current scope
            // Defaulting to VARCHAR(100)
            v.Length = 100;

            return v;
        }
    }
}
