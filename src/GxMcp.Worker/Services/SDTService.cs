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

                if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    dynamic sdt = obj;
                    var result = new JObject();
                    result["name"] = sdt.Name;
                    result["type"] = "SDT";
                    try { result["isCollection"] = sdt.IsCollection; } catch { result["isCollection"] = false; }
                    
                    var children = new JArray();
                    dynamic structure = FindStructurePart(sdt);
                    
                    if (structure != null && structure.Root != null)
                    {
                        foreach (dynamic child in structure.Root.Items)
                        {
                            children.Add(MapLevelToResult(child));
                        }
                    }
                    result["children"] = children;
                    return result.ToString();
                }

                return "{\"error\": \"Object is not an SDT\"}";
            }
            catch (Exception ex)
            {
                Logger.Error("SDTService Error: " + ex.Message);
                return "{\"error\": \"" + ex.Message + "\"}";
            }
        }

        /// <summary>
        /// Finds the SDT Structure Part using multiple strategies.
        /// In GX18 SDK, the part has TypeDescriptor.Name="SDTStructure" and class SDTStructurePart.
        /// </summary>
        private dynamic FindStructurePart(dynamic sdt)
        {
            // Strategy 1: Iterate parts matching by TypeDescriptor name or class name
            foreach (dynamic part in sdt.Parts)
            {
                try {
                    string descName = part.TypeDescriptor?.Name ?? "";
                    string className = part.GetType().Name;
                    if (descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        descName.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                        className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                    { return part; }
                } catch { }
            }
            
            // Strategy 2: Parts.Get with known GUID
            try {
                var part = sdt.Parts.Get(Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3"));
                if (part != null) return part;
            } catch { }
            
            // Strategy 3: Duck typing - any part with Root.Items
            foreach (dynamic part in sdt.Parts)
            {
                try {
                    if (part.Root != null && part.Root.Items != null) return part;
                } catch { }
            }
            
            return null;
        }

        private JObject MapLevelToResult(dynamic level)
        {
            var res = new JObject();
            try { res["name"] = (string)level.Name; } catch { res["name"] = "?"; }
            
            bool isLeaf = true;
            try { isLeaf = level.IsLeafItem; } catch { }
            
            try { res["isCollection"] = (bool)level.IsCollection; } catch { res["isCollection"] = false; }
            
            if (!isLeaf)
            {
                res["isLevel"] = true;
                var children = new JArray();
                try {
                    foreach (dynamic child in level.Items)
                    {
                        children.Add(MapLevelToResult(child));
                    }
                } catch { }
                res["children"] = children;
                res["type"] = "Compound";
            }
            else
            {
                res["isLevel"] = false;
                try { res["type"] = level.Type.ToString(); } catch { res["type"] = "Unknown"; }
            }
            return res;
        }
    }
}
