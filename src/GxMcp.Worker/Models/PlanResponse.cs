using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace GxMcp.Worker.Models
{
    public sealed class TouchedObject
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public string Op   { get; set; } = ""; // "create" | "modify" | "delete"
    }

    public sealed class BrokenRef
    {
        public string From     { get; set; } = "";
        public string FromType { get; set; } = "";
        public string To       { get; set; } = "";
        public string Reason   { get; set; } = "";
    }

    public sealed class PlanWarning
    {
        public string Code    { get; set; } = "";
        public string Message { get; set; } = "";
        public string Path    { get; set; } = "";
    }

    public sealed class PlanResponse
    {
        public List<TouchedObject> TouchedObjects { get; set; } = new List<TouchedObject>();
        public string XmlDiff   { get; set; } = null;
        public List<BrokenRef>   BrokenRefs { get; set; } = new List<BrokenRef>();
        public List<PlanWarning> Warnings   { get; set; } = new List<PlanWarning>();
        public long EstimatedDurationMs { get; set; } = 0;

        public JObject ToJson()
        {
            var settings = new JsonSerializerSettings {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };
            return JObject.FromObject(this, JsonSerializer.Create(settings));
        }
    }
}
