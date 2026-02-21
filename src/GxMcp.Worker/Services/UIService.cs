using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class UIService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public UIService(KbService kbService, ObjectService objectService)
        {
            _kbService = kbService;
            _objectService = objectService;
        }

        public string GetUIContext(string target)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return "{\"error\": \"Object not found\"}";

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;

                if (obj is WebPanel wbp)
                {
                    // result["controls"] = GetWebFormControls(wbp.Parts.Get<WebFormPart>());
                }
                else if (obj is Transaction trn)
                {
                    // result["controls"] = GetWebFormControls(trn.Parts.Get<WebFormPart>());
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }
    }
}
