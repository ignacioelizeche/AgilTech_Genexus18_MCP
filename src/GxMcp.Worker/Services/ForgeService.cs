using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
using GxMcp.Worker.Helpers;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using Artech.Genexus.Common;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Attribute = Artech.Genexus.Common.Objects.Attribute;

namespace GxMcp.Worker.Services
{
    public class ForgeService
    {
        private readonly BuildService _buildService;
        private readonly ObjectService _objectService;
        private readonly AnalyzeService _analyzeService;

        public ForgeService(BuildService buildService, ObjectService objectService, AnalyzeService analyzeService)
        {
            _buildService = buildService;
            _objectService = objectService;
            _analyzeService = analyzeService;
        }

        public string CreateObject(string name, string definitionJson)
        {
            try
            {
                // Sanitize Name (remove Trn: prefix if present)
                if (name.Contains(":"))
                    name = name.Split(':')[1];

                // Parse definition
                var def = Newtonsoft.Json.Linq.JObject.Parse(definitionJson);
                var attributes = def["Attributes"] as Newtonsoft.Json.Linq.JArray;
                var structure = def["Structure"]?.ToString();

                // Native Creation Logic
                Logger.Info($"[ForgeService] Starting Native Creation for Transaction '{name}'...");
                CreateTransactionNative(name, attributes, structure);
                Logger.Info($"[ForgeService] Native Transaction '{name}' created successfully.");

                // Invalidate Cache to ensure ReadObject sees new parts
                _objectService.Invalidate(name);

                // Live Indexing: Trigger immediate analysis
                try {
                    Logger.Info($"[ForgeService] Triggering Live Indexing for {name}...");
                    _analyzeService.AnalyzeInternal(name);
                } catch (Exception ex) {
                    Logger.Error($"[ForgeService] Live Indexing Failed: {ex.Message}");
                }

                return _objectService.ReadObject(name);
            }
            catch (Exception ex)
            {
                Logger.Error($"[ForgeService] CreateObject Failed: {ex.Message}\n{ex.StackTrace}");
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private void CreateTransactionNative(string name, Newtonsoft.Json.Linq.JArray attributes, string structure)
        {
            var kb = _objectService.GetKB();
            if (kb == null || kb.DesignModel == null)
                throw new Exception("KB or DesignModel is not available.");

            KBModel model = kb.DesignModel;

            // 1. Create Attributes
            if (attributes != null)
            {
                foreach (var attToken in attributes)
                {
                    string attName = attToken["Name"]?.ToString();
                    if (string.IsNullOrEmpty(attName)) continue;

                    // Check if exists
                    Attribute existing = Attribute.Get(model, attName);
                    if (existing == null)
                    {
                        Logger.Info($"[ForgeService] Creating Attribute '{attName}'...");
                        Attribute att = new Attribute(model);
                        att.Name = attName;
                        att.Description = attName; // Default description
                        
                        string typeStr = attToken["Type"]?.ToString() ?? "VarChar";
                        int len = int.Parse(attToken["Length"]?.ToString() ?? "40");
                        int dec = int.Parse(attToken["Decimals"]?.ToString() ?? "0");

                        att.Type = ParseType(typeStr);
                        att.Length = len;
                        if (att.Type == eDBType.NUMERIC)
                            att.Decimals = dec;

                        att.Save();
                        Logger.Info($"[ForgeService] Attribute '{attName}' saved.");
                    }
                    else
                    {
                        Logger.Info($"[ForgeService] Attribute '{attName}' already exists.");
                    }
                }
            }

            // 2. Create Transaction
            Transaction trn = Transaction.Get(model, new QualifiedName(name));
            if (trn == null)
            {
                Logger.Info($"[ForgeService] Creating Transaction '{name}' via Transaction.Create(model)...");
                trn = Transaction.Create(model);
                if (trn == null) throw new Exception("Failed to create Transaction object via Transaction.Create.");
                
                trn.Name = name;
                trn.Description = name;
                Logger.Info($"[ForgeService] New Transaction Type GUID: {trn.TypeDescriptor.Id}");
            }
            else
            {
                Logger.Info($"[ForgeService] Updating existing Transaction '{name}'...");
            }

            // 2.5 Ensure Parts Exist (Events, Rules, WebForm)
            if (trn.Parts.Get<EventsPart>() == null)
            {
                Logger.Info($"[ForgeService] Adding EventsPart to '{name}'...");
                var p = new EventsPart(trn);
                p.Source = "Event Start\n    // Init\nEndEvent";
                try 
                { 
                    trn.Parts.Add(p.Type, p); 
                    p.Save(); // Explicit Save
                    Logger.Info($"[ForgeService] EventsPart added and saved.");
                } 
                catch (Exception ex) 
                {
                    Logger.Error($"[ForgeService] Failed to add EventsPart: {ex.Message}");
                }
            }

            if (trn.Parts.Get<RulesPart>() == null)
            {
                Logger.Info($"[ForgeService] Adding RulesPart to '{name}'...");
                var p = new RulesPart(trn);
                p.Source = "// Rules";
                try 
                { 
                    trn.Parts.Add(p.Type, p); 
                    p.Save(); // Explicit Save
                } 
                catch (Exception ex)
                {
                    Logger.Error($"[ForgeService] Failed to add RulesPart: {ex.Message}");
                }
            }

            if (trn.Parts.Get<WebFormPart>() == null)
            {
                Logger.Info($"[ForgeService] Adding WebFormPart to '{name}'...");
                var p = new WebFormPart(trn);
                try 
                { 
                    trn.Parts.Add(p.Type, p); 
                    p.Save(); // Explicit Save
                } 
                catch (Exception ex)
                {
                    Logger.Error($"[ForgeService] Failed to add WebFormPart: {ex.Message}");
                }
            }

            // 3. Set Structure
            if (structure != null)
            {
                // Clear existing structure if needed? 
                // For now, let's assume we are appending or defining new.
                // Depending on SDK, modifying structure might be adding levels/attributes.
                
                // We need to access the Root level
                TransactionLevel root = trn.Structure.Root;
                
                foreach (string line in structure.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    string attName = trimmed;
                    bool isKey = false;
                    
                    if (trimmed.EndsWith("*"))
                    {
                        isKey = true;
                        attName = trimmed.TrimEnd('*').Trim();
                    }

                    Attribute att = Attribute.Get(model, attName);
                    if (att != null)
                    {
                        // AddAttribute returns TransactionAttribute or void? Reflection said:
                        // Void AddAttribute(Artech.Genexus.Common.Parts.TransactionAttribute)
                        // Artech.Genexus.Common.Parts.TransactionAttribute AddAttribute(Artech.Genexus.Common.Objects.Attribute)
                        
                        // Safe add
                        bool exists = false;
                        foreach (var ta in root.Attributes)
                        {
                            if (ta.Name == attName) { exists = true; break; }
                        }

                        if (!exists)
                        {
                            try
                            {
                                var trnAtt = root.AddAttribute(att);
                                if (trnAtt != null)
                                {
                                    trnAtt.IsKey = isKey;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Info($"[ForgeService] AddAttribute warning for '{attName}': {ex.Message}");
                            }
                        }
                        else
                        {
                             // Update existing? For now, just ensure key status if needed, but safe to skip.
                        }
                    }
                    else
                    {
                        Logger.Error($"[ForgeService] Attribute '{attName}' not found in KB. Skipping.");
                    }
                }
            }

            trn.Save();
            Logger.Info($"[ForgeService] Transaction '{name}' saved. Final Type GUID: {trn.TypeDescriptor.Id}");
        }

        private eDBType ParseType(string typeStr)
        {
            switch (typeStr.ToLower())
            {
                case "numeric": return eDBType.NUMERIC;
                case "varchar": return eDBType.VARCHAR;
                case "character": return eDBType.CHARACTER;
                case "date": return eDBType.DATE;
                case "datetime": return eDBType.DATETIME;
                case "boolean": return eDBType.Boolean;
                case "longvarchar": return eDBType.LONGVARCHAR;
                default: return eDBType.VARCHAR;
            }
        }
    }
}
