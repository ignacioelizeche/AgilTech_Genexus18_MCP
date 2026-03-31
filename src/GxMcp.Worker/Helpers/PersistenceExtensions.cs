using System;
using System.Linq;
using System.Text;
using Artech.Architecture.Common.Objects;
using Artech.Common.Diagnostics;

namespace GxMcp.Worker.Helpers
{
    public static class PersistenceExtensions
    {
        public static void EnsureSave(this KBObject obj, bool check = true)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            OutputMessages msgs = new OutputMessages();
            
            if (check)
            {
                bool isValid = obj.Validate(msgs);
                if (!isValid || msgs.HasErrors)
                {
                    string errorText = ExtractErrorText(msgs, obj);
                    throw new Exception($"Validation failed for {obj.TypeDescriptor.Name} '{obj.Name}': {errorText}");
                }
            }

            try 
            {
                var saveMethod = obj.GetType().GetMethod("Save", new Type[] { typeof(bool) });
                if (saveMethod != null)
                    saveMethod.Invoke(obj, new object[] { check });
                else
                    obj.Save();
            }
            catch (Exception ex)
            {
                // Force a second validation pass after failure to capture what went wrong
                obj.Validate(msgs);
                string validationText = ExtractErrorText(msgs, obj);
                string sdkMessages = GetSdkMessages(obj);
                
                if (!string.IsNullOrEmpty(validationText) && !sdkMessages.Contains(validationText))
                    sdkMessages += " [VALIDATION]: " + validationText;

                throw new Exception($"SDK Save Exception for {obj.Name}: {ex.InnerException?.Message ?? ex.Message}. Detailed Messages: {sdkMessages}", ex);
            }
            
            if (msgs.HasErrors || !string.IsNullOrEmpty(GetSdkMessages(obj)))
            {
                string errorText = ExtractErrorText(msgs, obj);
                if (!string.IsNullOrEmpty(errorText))
                    throw new Exception($"Save failed for {obj.TypeDescriptor.Name} '{obj.Name}': {errorText}");
            }
        }

        public static string GetSdkMessages(this object target)
        {
            if (target == null) return string.Empty;
            var sb = new StringBuilder();

            if (target is KBObject obj)
            {
                foreach (var part in obj.Parts)
                {
                    string partMsgs = GetSdkMessages((object)part);
                    if (!string.IsNullOrEmpty(partMsgs))
                    {
                        if (sb.Length > 0) sb.Append(" | ");
                        sb.Append($"[{part.TypeDescriptor?.Name ?? "Part"}]: {partMsgs}");
                    }
                }
            }

            try
            {
                var prop = target.GetType().GetProperty("Messages", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (prop != null)
                {
                    var value = prop.GetValue(target);
                    if (value is System.Collections.IEnumerable list)
                    {
                        foreach (object msg in list)
                        {
                            if (msg == null) continue;
                            if (sb.Length > 0) sb.Append(" | ");
                            sb.Append(msg.ToString());
                        }
                    }
                }
            }
            catch { }
            return sb.ToString();
        }

        private static string ExtractErrorText(OutputMessages msgs, KBObject obj = null)
        {
            StringBuilder sb = new StringBuilder();

            if (msgs != null)
            {
                if (!string.IsNullOrEmpty(msgs.ErrorText)) sb.Append(msgs.ErrorText);
                else if (!string.IsNullOrEmpty(msgs.FullText)) sb.Append(msgs.FullText);
                else
                {
                    var errors = msgs.OnlyMessages
                                     .Where(m => m is OutputError)
                                     .Select(m => m.Text)
                                     .ToList();
                    if (errors.Any()) sb.Append(string.Join(" | ", errors));
                }
            }

            if (obj != null)
            {
                string localMsgs = GetSdkMessages(obj);
                if (!string.IsNullOrEmpty(localMsgs))
                {
                    if (sb.Length > 0) sb.Append(" [SDK-LOCAL]: ");
                    sb.Append(localMsgs);
                }
            }
            
            return sb.ToString();
        }
    }
}
