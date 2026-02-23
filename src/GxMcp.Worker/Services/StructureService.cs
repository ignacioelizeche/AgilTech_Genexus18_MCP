using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class StructureService
    {
        private readonly ObjectService _objectService;

        public StructureService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string GetTableAttributes(string targetName)
        {
            try
            {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return "{\"error\": \"Table or Transaction not found\"}";

                var result = new JObject();
                result["name"] = obj.Name;
                var attributes = new JArray();

                // Use dynamic to access SDK properties safely without direct type reference dependency
                dynamic dObj = obj;
                
                string typeName = obj.TypeDescriptor.Name;
                if (typeName == "Transaction")
                {
                    try {
                        foreach (dynamic attr in dObj.Structure.Root.Attributes)
                        {
                            attributes.Add(new JObject {
                                ["name"] = attr.Name,
                                ["type"] = attr.Type.ToString()
                            });
                        }
                    } catch (Exception ex) { Logger.Debug("TRN Structure access failed: " + ex.Message); }
                }
                else if (typeName == "Table")
                {
                    try {
                        foreach (dynamic attr in dObj.Attributes)
                        {
                            attributes.Add(new JObject {
                                ["name"] = attr.Name,
                                ["type"] = attr.Type.ToString()
                            });
                        }
                    } catch (Exception ex) { Logger.Debug("Table Attributes access failed: " + ex.Message); }
                }

                result["attributes"] = attributes;
                return result.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error("StructureService Error: " + ex.Message);
                return "{\"error\": \"" + ex.Message + "\"}";
            }
        }
    }
}
