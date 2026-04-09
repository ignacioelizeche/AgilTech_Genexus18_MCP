using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class ListService
    {
        private readonly KbService _kbService;
        private readonly IndexCacheService _indexCacheService;

        public ListService(KbService kbService, IndexCacheService indexCacheService)
        {
            _kbService = kbService;
            _indexCacheService = indexCacheService;
        }

        public string ListObjects(string filter, int limit, int offset, string parentFilter = null, string typeFilter = null, string parentPathFilter = null)
        {
            try
            {
                var array = new JArray();

                // Parse filter: can be a comma-separated list of types or a partial name
                var filterTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string nameFilter = null;

                if (!string.IsNullOrEmpty(filter))
                {
                    if (filter.Contains(","))
                    {
                        foreach (var t in filter.Split(',')) filterTypes.Add(t.Trim());
                    }
                    else if (IsLikelyType(filter))
                    {
                        filterTypes.Add(filter.Trim());
                    }
                    else
                    {
                        nameFilter = filter.Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(typeFilter))
                {
                    foreach (var t in typeFilter.Split(','))
                    {
                        var trimmed = t.Trim();
                        if (!string.IsNullOrEmpty(trimmed)) filterTypes.Add(trimmed);
                    }
                }

                var index = _indexCacheService.GetIndex();
                if (index != null && index.Objects.Count > 0)
                {
                    bool preferIndexedHierarchy =
                        parentPathFilter == null ||
                        index.Objects.Values.Any(e => !string.IsNullOrWhiteSpace(e.ParentPath) || !string.IsNullOrWhiteSpace(e.Path));

                    if (preferIndexedHierarchy)
                    {
                        var entries = index.Objects.Values.AsEnumerable();

                        if (parentPathFilter != null)
                        {
                            entries = entries.Where(e => string.Equals(e.ParentPath ?? string.Empty, parentPathFilter, StringComparison.OrdinalIgnoreCase));
                        }
                        else if (!string.IsNullOrWhiteSpace(parentFilter))
                        {
                            entries = entries.Where(e => string.Equals(e.Parent ?? string.Empty, parentFilter, StringComparison.OrdinalIgnoreCase));
                        }

                        if (filterTypes.Count > 0)
                        {
                            entries = entries.Where(e => filterTypes.Contains(e.Type ?? string.Empty));
                        }

                        if (!string.IsNullOrEmpty(nameFilter))
                        {
                            entries = entries.Where(e => (e.Name ?? string.Empty).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                        }

                        foreach (var entry in entries
                            .OrderBy(e => GetTypeSortBucket(e.Type))
                            .ThenBy(e => e.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(e => e.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                            .Skip(Math.Max(0, offset))
                            .Take(limit <= 0 ? int.MaxValue : limit))
                        {
                            array.Add(BuildItem(
                                entry.Name,
                                entry.Type ?? "Unknown",
                                entry.Description,
                                entry.Parent ?? string.Empty,
                                entry.Module ?? string.Empty,
                                entry.Path ?? string.Empty,
                                entry.ParentPath ?? string.Empty
                            ));
                        }

                        return array.ToString();
                    }
                }

                var kb = _kbService.GetKB();
                if (kb == null) return "{\"error\":\"KB not open\"}";
                if (kb.DesignModel == null) return "{\"error\":\"KB DesignModel is null\"}";
                var objects = kb.DesignModel.Objects;
                if (objects == null) return "{\"error\":\"KB DesignModel.Objects is null\"}";

                var allObjects = ((System.Collections.IEnumerable)objects.GetAll())
                    .Cast<global::Artech.Architecture.Common.Objects.KBObject>();

                var filteredObjects = allObjects
                    .Select(obj => new RuntimeListEntry
                    {
                        Object = obj,
                        Hierarchy = ResolveHierarchy(obj),
                        TypeName = obj.TypeDescriptor?.Name ?? "Unknown",
                    });

                if (filterTypes.Count > 0)
                {
                    filteredObjects = filteredObjects.Where(x => filterTypes.Contains(x.TypeName));
                }

                if (!string.IsNullOrEmpty(nameFilter))
                {
                    filteredObjects = filteredObjects.Where(x => x.Object.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (parentPathFilter != null)
                {
                    filteredObjects = filteredObjects.Where(x => string.Equals(x.Hierarchy.ParentPath, parentPathFilter, StringComparison.OrdinalIgnoreCase));
                }
                else if (!string.IsNullOrWhiteSpace(parentFilter))
                {
                    filteredObjects = filteredObjects.Where(x => string.Equals(x.Hierarchy.ParentName, parentFilter, StringComparison.OrdinalIgnoreCase));
                }

                foreach (var item in filteredObjects
                    .OrderBy(x => GetTypeSortBucket(x.TypeName))
                    .ThenBy(x => x.Object.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.TypeName, StringComparer.OrdinalIgnoreCase)
                    .Skip(Math.Max(0, offset))
                    .Take(limit <= 0 ? int.MaxValue : limit))
                {
                    array.Add(BuildItem(
                        item.Object.Name,
                        item.TypeName,
                        item.Object.Description,
                        item.Hierarchy.ParentName,
                        item.Hierarchy.ModuleName,
                        item.Hierarchy.Path,
                        item.Hierarchy.ParentPath
                    ));
                }

                return array.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private JObject BuildItem(string name, string type, string description, string parent, string module, string path, string parentPath)
        {
            var item = new JObject();
            item["name"] = name;
            item["type"] = type;
            item["description"] = description;
            item["parent"] = parent;
            item["module"] = module;
            item["path"] = path;
            item["parentPath"] = parentPath;
            return item;
        }

        private HierarchyInfo ResolveHierarchy(dynamic obj)
        {
            string parentName = string.Empty;
            string moduleName = null;
            var parentSegments = new List<string>();

            try
            {
                dynamic currentParent = obj.Parent;
                bool isImmediateParent = true;

                while (currentParent != null)
                {
                    try
                    {
                        if (currentParent.Guid == obj.Guid)
                        {
                            break;
                        }
                    }
                    catch
                    {
                    }

                    string parentTypeName = null;
                    try { parentTypeName = currentParent.TypeDescriptor?.Name; } catch { }

                    if (string.Equals(parentTypeName, "DesignModel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isImmediateParent)
                        {
                            parentName = "Root Module";
                        }
                        break;
                    }

                    if (currentParent is global::Artech.Architecture.Common.Objects.Module ||
                        currentParent is global::Artech.Architecture.Common.Objects.Folder)
                    {
                        string currentName = null;
                        try { currentName = currentParent.Name; } catch { }

                        if (!string.IsNullOrWhiteSpace(currentName))
                        {
                            parentSegments.Insert(0, currentName);
                            if (isImmediateParent)
                            {
                                parentName = currentName;
                            }

                            if (moduleName == null &&
                                currentParent is global::Artech.Architecture.Common.Objects.Module)
                            {
                                moduleName = currentName;
                            }
                        }
                    }

                    currentParent = currentParent.Parent;
                    isImmediateParent = false;
                }
            }
            catch { }

            try
            {
                if (moduleName == null && obj.Module != null && obj.Module.Guid != obj.Guid)
                {
                    moduleName = obj.Module.Name;
                }
            }
            catch
            {
            }

            string parentPath = string.Join("/", parentSegments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
            string resolvedPath = string.IsNullOrWhiteSpace(obj.Name)
                ? parentPath
                : string.IsNullOrEmpty(parentPath) ? (string)obj.Name : parentPath + "/" + (string)obj.Name;

            return new HierarchyInfo
            {
                ParentName = parentName,
                ParentPath = parentPath,
                Path = resolvedPath,
                ModuleName = moduleName ?? string.Empty,
            };
        }

        private bool IsLikelyType(string s)
        {
            var types = new[] { "Folder", "Module", "Procedure", "Transaction", "WebPanel", "Attribute", "Table", "DataView", "Domain", "WorkPanel", "ExternalObject", "Menu", "SDPanel", "DataProvider", "SDT", "StructuredDataType", "Image" };
            return types.Any(t => string.Equals(t, s, StringComparison.OrdinalIgnoreCase));
        }

        private int GetTypeSortBucket(string type)
        {
            if (string.Equals(type, "Folder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "Module", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return 1;
        }

        private sealed class RuntimeListEntry
        {
            public global::Artech.Architecture.Common.Objects.KBObject Object { get; set; }
            public HierarchyInfo Hierarchy { get; set; }
            public string TypeName { get; set; }
        }

        private sealed class HierarchyInfo
        {
            public string ParentName { get; set; }
            public string ParentPath { get; set; }
            public string Path { get; set; }
            public string ModuleName { get; set; }
        }
    }
}
