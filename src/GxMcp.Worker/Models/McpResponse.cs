using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Models
{
    public class McpResponse
    {
        public static string Success(string action, string target, JObject data = null)
        {
            var result = new JObject
            {
                ["status"] = "Success",
                ["action"] = action,
                ["target"] = target
            };

            if (data != null)
            {
                foreach (var prop in data.Properties())
                {
                    result[prop.Name] = prop.Value;
                }
            }

            return result.ToString();
        }

        public static string Error(string message, string target = null)
        {
            var err = new JObject
            {
                ["error"] = message
            };
            if (!string.IsNullOrEmpty(target)) err["target"] = target;
            return err.ToString();
        }
    }
}