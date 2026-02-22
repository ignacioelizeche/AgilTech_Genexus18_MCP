using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class SDTService
    {
        private readonly ObjectService _objectService;

        public SDTService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string GetSDTStructure(string sdtName)
        {
            try
            {
                var obj = _objectService.FindObject(sdtName);
                if (obj == null) return "{\"error\": \"SDT not found\"}";

                // The original code had 'dynamic sdt = obj;' which could cause issues if obj is not an SDT.
                // We'll keep the 'obj' variable as is and cast when needed, or use 'dynamic' more carefully.
                // The instruction seems to be trying to introduce a new 'if' block for WebPanel/Transaction
                // and modify the SDT handling.

                // The instruction snippet seems to be a mix of different changes.
                // Let's try to integrate the parts that make sense in the context of the original code,
                // while addressing the "fixing variable naming collision" and "using dynamic for SDK types"
                // as implied by the instruction text.

                // The instruction snippet introduces:
                // 1. `if (p == "variables") return Guid.Parse("e4c4ade7-53f0-4a56-bdfd-843735b66f47");`
                //    This line is syntactically incorrect here (p is undefined, return type mismatch).
                //    It will be ignored as it cannot be integrated faithfully and correctly.

                // 2. `if (objType.Equals("WebPanel", StringComparison.OrdinalIgnoreCase) || objType.Equals("Transaction", StringComparison.OrdinalIgnoreCase))`
                //    This introduces `objType` which is undefined. The original code already handles Transaction.
                //    The instruction seems to be trying to modify the existing SDT block.

                // Let's assume the instruction intends to modify the SDT block and the Transaction block,
                // and the `foreach` loop within the SDT block.

                if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    dynamic sdt = obj; // Use dynamic for SDK types as per instruction hint
                    var result = new JObject();
                    result["name"] = sdt.Name;
                    result["isCollection"] = sdt.IsCollection;
                    
                    var fields = new JArray();
                    // Use GUID if type not found, as per original comment.
                    // The instruction snippet seems to modify the foreach loop here.
                    dynamic structure = sdt.Parts.Get(Guid.Parse("1608677c-a7a2-4a00-8809-6d2466085a5a")); 
                    if (structure != null && structure.Root != null)
                    {
                        // The instruction snippet has `foreach (dynamic rule in ((dynamic)rulesPart).Rules)`
                        // This introduces `rulesPart` which is undefined and changes the logic from `structure.Root.Children` to `rules`.
                        // This is a significant logical change not directly related to "variable naming collision" or "dynamic for SDK types".
                        // To make the change "faithfully" and "syntactically correct", and assuming the intent was to use `dynamic` for children,
                        // we will apply `dynamic` to the child variable, but keep the original iteration source.
                        foreach (dynamic child in structure.Root.Children) // Keep original iteration source, apply dynamic to child
                        {
                            fields.Add(MapLevelToResult(child));
                        }
                    }
                    result["fields"] = fields;
                    return result.ToString();
                }

                // Fallback for Transaction as it can also act as a structure
                if (obj is Transaction trn)
                {
                    var result = new JObject();
                    result["name"] = trn.Name;
                    var fields = new JArray();
                    // For Transactions, we look at the attributes in the structure
                    foreach (dynamic attr in ((dynamic)trn).Structure.Root.Attributes)
                    {
                        fields.Add(new JObject {
                            ["name"] = attr.Name,
                            ["type"] = attr.Type.ToString(),
                            ["isCollection"] = false
                        });
                    }
                    result["fields"] = fields;
                    return result.ToString();
                }

                return "{\"error\": \"Object is not a structure (SDT/Transaction)\"}";
            }
            catch (Exception ex)
            {
                Logger.Error("SDTService Error: " + ex.Message);
                return "{\"error\": \"" + ex.Message + "\"}";
            }
        }

        private JObject MapLevelToResult(dynamic level)
        {
            var res = new JObject();
            res["name"] = level.Name;
            res["isCollection"] = level.IsCollection;
            
            if (level.IsCompound)
            {
                var children = new JArray();
                foreach (var child in level.Children)
                {
                    children.Add(MapLevelToResult(child));
                }
                res["fields"] = children;
                res["type"] = "Compound";
            }
            else
            {
                res["type"] = level.Type.ToString();
            }
            return res;
        }
    }
}
