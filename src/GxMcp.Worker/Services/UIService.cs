using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using GxMcp.Worker.Helpers;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Genexus.Common.Parts.WebForm;
using Artech.Genexus.Common.Controls;
using Artech.Common.Controls.ToolStrip;
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
                if (obj == null) return Models.McpResponse.Error("Object not found", target, "Layout", "The requested object is not available in the active Knowledge Base.");

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;

                if (obj is WebPanel wbp)
                {
                    var part = wbp.Parts.Get<WebFormPart>();
                    if (part != null) {
                        result["html"] = GenerateEnhancedHTML(obj, part);
                        result["structure"] = GetSimplifiedUIStructure(obj, part);
                    }
                }
                else if (obj is Transaction trn)
                {
                    var part = trn.Parts.Get<WebFormPart>();
                    if (part != null) {
                        result["html"] = GenerateEnhancedHTML(obj, part);
                        result["structure"] = GetSimplifiedUIStructure(obj, part);
                    }
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public JObject GetSimplifiedUIStructure(KBObject obj, WebFormPart part = null)
        {
            if (part == null) {
                if (obj is WebPanel wbp) part = wbp.Parts.Get<WebFormPart>();
                else if (obj is Transaction trn) part = trn.Parts.Get<WebFormPart>();
            }

            if (part == null || part.Document == null || part.Document.DocumentElement == null) return new JObject();

            try {
                var tree = GxWebFormHelper.GetWebTagTree(obj, part.Document.DocumentElement);
                if (tree != null && tree.Root != null) {
                    return RenderSimplifiedNode(tree);
                }
            } catch {}
            return new JObject();
        }

        private JObject RenderSimplifiedNode(GxMcp.Worker.Helpers.Tree<IWebTag> node)
        {
            var result = new JObject();
            if (node == null || node.Root == null) return result;

            IWebTag tag = node.Root;
            result["type"] = tag.Type.ToString();
            
            string controlName = tag.ValStr("ControlName");
            if (!string.IsNullOrEmpty(controlName)) result["name"] = controlName;

            string caption = tag.ValStr("Caption") ?? tag.ValStr("CaptionExpression");
            if (!string.IsNullOrEmpty(caption)) result["caption"] = caption;

            string cls = tag.ValStr("Class");
            if (!string.IsNullOrEmpty(cls)) result["class"] = cls;

            string att = tag.ValStr("Attribute") ?? tag.ValStr("Variable");
            if (!string.IsNullOrEmpty(att)) result["dataBinding"] = att;

            if (node.Children != null && node.Children.Count > 0)
            {
                var children = new JArray();
                foreach (var child in node.Children)
                {
                    var childJson = RenderSimplifiedNode(child);
                    if (childJson.Count > 0) children.Add(childJson);
                }
                if (children.Count > 0) result["children"] = children;
            }

            return result;
        }

        private string GenerateEnhancedHTML(KBObject obj, WebFormPart part)
        {
            if (part == null || part.Document == null || part.Document.DocumentElement == null) return "<div>No Layout Available</div>";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='UTF-8'>");
            
            // Syntax Highlighting (Highlight.js)
            sb.AppendLine("<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/vs.min.css'>");
            sb.AppendLine("<script src='https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js'></script>");
            sb.AppendLine("<script src='https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/languages/xml.min.js'></script>");

            sb.AppendLine("<style>");
            
            // Base Styles (GX Design Mode Vibe)
            sb.AppendLine("body, html { font-family: 'Open Sans', 'Segoe UI', Arial, sans-serif; padding: 0; margin: 0; background-color: #fff; color: #000; font-size: 13px; height: 100%; overflow: hidden; }");
            sb.AppendLine(".main-container { display: flex; flex-direction: column; height: 100vh; }");
            
            // Header
            sb.AppendLine(".header-bar { display: flex; justify-content: space-between; align-items: center; background: #fff; padding: 2px 10px; border-bottom: 1px solid #ccc; flex-shrink: 0; }");
            sb.AppendLine(".header-title { font-size: 11px; color: #444; }");
            
            // Design View Styles (Match GeneXus native Feel)
            sb.AppendLine(".gx-design-canvas { background-color: #fff; padding: 25px; min-height: 1000px; min-width: 800px; border-left: 20px solid #f0f0f0; }");
            sb.AppendLine(".Table, .TableContent, .TableData, .Table100Width { border: 1px dotted #ccc; border-collapse: collapse; width: auto; position: relative; }");
            sb.AppendLine(".Table100Width { width: 100%; }");
            sb.AppendLine("td { border: 1px dotted #ccc; padding: 4px 8px; vertical-align: top; min-width: 5px; min-height: 20px; }");
            
            // Controls - Fallback Styles (Overriden by theme)
            sb.AppendLine(".Group { border: 1px solid #c0c0c0; padding: 15px; margin: 5px 0; background: #fff; position: relative; border-radius: 3px; }");
            sb.AppendLine(".GroupTitle, .Group legend, .TextBlockTitle, .TextBlockTitleWWP { font-family: 'Trebuchet MS', 'Open Sans', sans-serif; color: #0f3f76; font-weight: bold; font-size: 13pt; padding: 0 5px; border-bottom: 2px solid #0f3f76; display: block; width: 100%; margin-bottom: 12px; }");
            sb.AppendLine(".gx-label { color: #000; white-space: nowrap; font-size: 13px; cursor: default; }");
            sb.AppendLine(".Attribute, .ReadonlyAttribute { border: 1px solid #7f9db9; padding: 2px; background: #fff; color: #000; font-family: 'Open Sans'; font-size: 13px; width: 100%; box-sizing: border-box; height: 22px; }");
            sb.AppendLine(".design-placeholder { font-style: italic; color: #666 !important; }");
            sb.AppendLine(".ReadonlyAttribute { border-color: transparent; background: transparent; }");
            sb.AppendLine(".ComboBox { border: 1px solid #7f9db9; padding: 0; background: #fff; font-size: 13px; height: 22px; width: 100%; }");
            sb.AppendLine(".Button { background: #044e8d !important; color: #fff !important; border: 1px solid #f0f0f0; padding: 5px 15px; min-width: 80px; font-size: 12px; cursor: default; margin: 1px; border-radius: 3px; font-weight: normal; font-family: 'Open Sans'; }");
            sb.AppendLine(".CheckBox { vertical-align: middle; margin-right: 3px; }");
            sb.AppendLine(".ErrorViewer { color: red; list-style-type: disc; margin: 15px 0; padding-left: 20px; font-weight: normal; font-size: 13px; }");
            
            // Markers
            sb.AppendLine(".required-marker { color: red; font-weight: bold; margin-left: -6px; margin-top: -4px; position: absolute; z-index: 10; font-size: 16px; pointer-events: none; }");

            sb.AppendLine("</style>");
            
            // Layout View Specific Styles
            sb.AppendLine("<style>");
            sb.AppendLine(".footer-tabs { display: flex; background: #f3f3f3; border-top: 1px solid #ccc; height: 30px; position: fixed; bottom: 0; left: 0; right: 0; z-index: 100; flex-shrink: 0; }");
            sb.AppendLine(".tab-button { padding: 5px 20px; font-size: 11px; cursor: pointer; border-right: 1px solid #ddd; background: #f3f3f3; color: #666; display: flex; align-items: center; }");
            sb.AppendLine(".tab-button.active { background: #fff; color: #000; font-weight: bold; border-top: 2px solid #007acc; margin-top: -1px; }");
            sb.AppendLine(".view-content { display: none; height: calc(100vh - 65px); overflow: auto; padding-bottom: 35px; width: 100%; }");
            sb.AppendLine(".view-content.active { display: block; }");
            sb.AppendLine(".xml-view { background: #fafafa; }");
            sb.AppendLine("pre { margin: 0; padding: 20px; font-size: 12px; }");
            sb.AppendLine("</style>");

            // Incorpore o CSS Principal (Tema/Design System) FORA do bloco <style>
            try
            {
                var kb = _kbService.GetKB();
                string kbPath = Path.GetDirectoryName(kb.Location);
                string styleName = null;

                dynamic dObj = obj;
                var styleProp = dObj.Properties.Get("Style") ?? dObj.Properties.Get("DesignSystem");
                if (styleProp != null) styleName = styleProp.ToString();

                if (string.IsNullOrEmpty(styleName))
                {
                    dynamic dKb = kb;
                    var defaultStyle = dKb.DesignModel.Properties.Get("DefaultStyle") ?? dKb.DesignModel.Properties.Get("DefaultDesignSystem");
                    if (defaultStyle != null) styleName = defaultStyle.ToString();
                }

                if (!string.IsNullOrEmpty(styleName))
                {
                    string[] possiblePaths = {
                        Path.Combine(kbPath, styleName + ".css"),
                        Path.Combine(kbPath, "Web", styleName + ".css"),
                        Path.Combine(kbPath, styleName + "Resp" + ".css"), // Custom for UnivaliResp
                        Path.Combine(kbPath, "Desenv\\web\\Resources\\Portuguese", styleName + ".css")
                    };

                    string foundCss = null;
                    foreach (var p in possiblePaths)
                    {
                        if (File.Exists(p)) { foundCss = p; break; }
                    }

                    if (foundCss != null)
                    {
                        string uri = new Uri(foundCss).AbsoluteUri;
                        sb.AppendLine(string.Format("<link rel='stylesheet' href='{0}'>", uri));
                        Logger.Info("CSS Applied: " + foundCss);
                    }
                }
            }
            catch (Exception ex) { Logger.Error("CSS Injection Error: " + ex.Message); }

            sb.AppendLine("</head><body>");
            
            sb.AppendLine("<div class='main-container'>");
            
            // Header bar
            sb.AppendLine("<div class='header-bar'>");
            sb.AppendLine(string.Format("<div class='header-title'>{0}</div>", obj.Name));
            sb.AppendLine("</div>");

            // Views
            sb.AppendLine("<div id='view-design' class='view-content active'>");
            sb.AppendLine("<div class='gx-design-canvas'>");
            
            try {
                var tree = GxWebFormHelper.GetWebTagTree(obj, part.Document.DocumentElement);
                if (tree != null && tree.Root != null) {
                    RenderEnhancedNode(tree, sb, obj);
                } else {
                    sb.AppendLine("<div style='color:orange; padding:20px;'>Layout Tree is empty. Check if the object has controls.</div>");
                }
            } catch (Exception ex) {
                Logger.Error("Render Loop Error: " + ex.ToString());
                sb.AppendLine("<div style='color:red; padding:20px;'>Render Error: " + HttpUtility.HtmlEncode(ex.Message) + "</div>");
            }
            sb.AppendLine("</div></div>");

            sb.AppendLine("<div id='view-html' class='view-content xml-view'>");
            
            // Pretty-print HTML/XML source
            string formattedHtml = "";
            try {
                var xdoc = System.Xml.Linq.XDocument.Parse(part.Document.OuterXml);
                formattedHtml = xdoc.ToString();
            } catch {
                formattedHtml = part.Document.OuterXml; // fallback
            }
            
            sb.AppendLine("<pre><code class='language-xml'>" + HttpUtility.HtmlEncode(formattedHtml) + "</code></pre>");
            sb.AppendLine("</div>");

            // Footer Tabs
            sb.AppendLine("<div class='footer-tabs'>");
            sb.AppendLine("<div class='tab-button active' onclick=\"switchTab('design')\">Design</div>");
            sb.AppendLine("<div class='tab-button' onclick=\"switchTab('html')\">HTML</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("</div>"); // end main-container

            // Script for tabs and syntax highting
            sb.AppendLine("<script>");
            sb.AppendLine("function switchTab(tab) {");
            sb.AppendLine("  document.querySelectorAll('.view-content').forEach(v => v.classList.remove('active'));");
            sb.AppendLine("  document.querySelectorAll('.tab-button').forEach(t => t.classList.remove('active'));");
            sb.AppendLine("  if (tab === 'design') {");
            sb.AppendLine("    document.getElementById('view-design').classList.add('active');");
            sb.AppendLine("    document.querySelector('.tab-button:nth-child(1)').classList.add('active');");
            sb.AppendLine("  } else {");
            sb.AppendLine("    document.getElementById('view-html').classList.add('active');");
            sb.AppendLine("    document.querySelector('.tab-button:nth-child(2)').classList.add('active');");
            sb.AppendLine("    hljs.highlightAll();");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine("window.onload = function() { hljs.highlightAll(); };");
            sb.AppendLine("</script>");

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private void RenderEnhancedNode(GxMcp.Worker.Helpers.Tree<IWebTag> node, System.Text.StringBuilder sb, KBObject kbObj)
        {
            if (node == null) return;
            IWebTag tag = node.Root;
            if (tag == null) {
                // Visit children even if root is null if they exist
                if (node.Children != null) {
                    foreach (var child in node.Children) RenderEnhancedNode(child, sb, kbObj);
                }
                return;
            }

            string caption = tag.ValStr("Caption");
            string cls = tag.ValStr("Class");
            string controlName = tag.ValStr("ControlName");
            string id = tag.ValStr("id");
            string classref = tag.ValStr("classref");
            string captionExpr = tag.ValStr("CaptionExpression");
            string controlType = tag.ValStr("ControlType");

            // Handle Caption Expression (for things like <Tokens>)
            if (string.IsNullOrEmpty(caption) && !string.IsNullOrEmpty(captionExpr))
            {
                caption = captionExpr;
                if (caption.Contains("&lt;") || caption.Contains("&gt;"))
                {
                    caption = caption.Replace("&lt;", "<").Replace("&gt;", ">");
                }
            }

            // Extract essential properties for gxprop
            var props = new List<string>();
            string[] commonPropNames = { "ControlName", "Caption", "Class", "Type", "Attribute", "Variable", "InternalName", "ControlType" };
            foreach (string propName in commonPropNames)
            {
                string val = tag.ValStr(propName);
                if (!string.IsNullOrEmpty(val))
                    props.Add($"{propName}={HttpUtility.HtmlAttributeEncode(val)}");
            }
            string gxProp = string.Join(";", props);
            string commonAttrs = string.Format(" classref='{0}' gxcontrol='{1}' gxprop='{2}'", 
                HttpUtility.HtmlAttributeEncode(classref), 
                HttpUtility.HtmlAttributeEncode(tag.Type.ToString()), 
                gxProp);

            if (!string.IsNullOrEmpty(id)) commonAttrs += $" id='{HttpUtility.HtmlAttributeEncode(id)}'";
            
            switch (tag.Type)
            {
                case WebTagType.Table:
                    string tableClass = "Table " + (cls ?? "");
                    sb.AppendLine(string.Format("<table class='{0}' cellspacing='0' cellpadding='0'{1}><tbody>", tableClass.Trim(), commonAttrs));
                    if (node.Children != null) {
                        foreach (var child in node.Children) RenderEnhancedNode(child, sb, kbObj);
                    }
                    sb.AppendLine("</tbody></table>");
                    break;

                case WebTagType.TableRow:
                    sb.AppendLine("<tr" + commonAttrs + ">");
                    if (node.Children != null) {
                        foreach (var child in node.Children) RenderEnhancedNode(child, sb, kbObj);
                    }
                    sb.AppendLine("</tr>");
                    break;

                case WebTagType.TableCell:
                    int colSpan = tag.ValNum("Colspan");
                    int rowSpan = tag.ValNum("Rowspan");
                    string tdProps = commonAttrs;
                    if (colSpan > 1) tdProps += string.Format(" colspan='{0}'", colSpan);
                    if (rowSpan > 1) tdProps += string.Format(" rowspan='{0}'", rowSpan);
                    
                    sb.AppendLine(string.Format("<td class='{0}'{1}>", string.IsNullOrEmpty(cls) ? "" : cls, tdProps));
                    if (node.Children != null) {
                        foreach (var child in node.Children) RenderEnhancedNode(child, sb, kbObj);
                    }
                    sb.AppendLine("</td>");
                    break;

                case WebTagType.Group:
                    sb.AppendLine(string.Format("<fieldset class='Group {0}'{1}>", string.IsNullOrEmpty(cls) ? "GroupTela" : cls, commonAttrs));
                    if (!string.IsNullOrEmpty(caption))
                        sb.AppendLine(string.Format("<legend class='GroupTitle' contenteditable='false'>{0}</legend>", HttpUtility.HtmlEncode(caption)));
                    if (node.Children != null) {
                        foreach (var child in node.Children) RenderEnhancedNode(child, sb, kbObj);
                    }
                    sb.AppendLine("</fieldset>");
                    break;

                case WebTagType.TextBlock:
                    string textCls = string.IsNullOrEmpty(cls) ? "TextBlock" : cls;
                    // Títulos em GX Design View
                    if (textCls.Contains("Title")) textCls += " TextBlockTitle";

                    sb.AppendLine(string.Format("<div class='gx-label-wrapper' style='display:inline-block;'><span class='gx-label {0}' contenteditable='false'{1}>{2}</span></div>", 
                        textCls, commonAttrs, HttpUtility.HtmlEncode(caption)));
                    break;

                case WebTagType.Button:
                    string btnCls = "Button " + (cls ?? "");
                    sb.AppendLine(string.Format("<div class='gx-button-wrapper' style='display:inline-block;'><input type='button' class='{0}' value=' {1} ' contenteditable='false'{2} /></div>", 
                        btnCls.Trim(), HttpUtility.HtmlAttributeEncode(caption), commonAttrs));
                    break;

                case WebTagType.Attribute:
                    string attName = tag.ValStr("Attribute");
                    if (string.IsNullOrEmpty(attName)) attName = tag.ValStr("Variable");
                    if (string.IsNullOrEmpty(attName)) attName = tag.ValStr("ControlName");
                    
                    // Fallback para aparecer algo se for um controle solto (como em Transaction)
                    if (string.IsNullOrEmpty(attName)) {
                        attName = tag.ValStr("Context") ?? tag.ValStr("InternalName");
                    }
                    
                    bool isReadOnly = tag.ValStr("ReadOnly") == "True";
                    string attCls = isReadOnly ? "ReadonlyAttribute" : (string.IsNullOrEmpty(cls) ? "Attribute" : cls);
                    
                    // Em Design View, mostramos o nome da variável/atributo
                    string displayVal = string.IsNullOrEmpty(attName) ? "Attribute" : attName;
                    string designClass = "design-placeholder";

                    // Verifica se é requerido (aproximação via SDK)
                    bool isRequired = tag.ValStr("IsNullable") == "False" || tag.ValStr("Required") == "True" || (cls != null && cls.Contains("Required"));

                    // Wrapper padrão GX para atributos
                    sb.AppendLine(string.Format("<div class='gx-attribute' style='width:100%; position:relative;'>"));

                    if (isRequired && !isReadOnly) {
                        sb.AppendLine("<span class='required-marker' title='Required'>*</span>");
                    }

                    if (controlType == "Combo Box") {
                        sb.AppendLine(string.Format("<select class='ComboBox {2} {3}' disabled{0} style='padding-left: 10px;'><option>{1}</option></select>", 
                            commonAttrs, HttpUtility.HtmlEncode(displayVal), attCls, designClass));
                    } else if (controlType == "Check Box") {
                        sb.AppendLine(string.Format("<label class='{0} {3}'{1}><input type='checkbox' class='CheckBox' disabled />{2}</label>", 
                            attCls, commonAttrs, HttpUtility.HtmlEncode(displayVal), designClass));
                    } else {
                        sb.AppendLine(string.Format("<input type='text' class='{0} {3}' value='{1}' contenteditable='false'{2} style='width:100%; padding-left: 10px;' />", 
                            attCls, HttpUtility.HtmlAttributeEncode(displayVal), commonAttrs, designClass));
                    }
                    
                    sb.AppendLine("</div>");
                    break;

                case WebTagType.ErrorViewer:
                    sb.AppendLine(string.Format("<span contenteditable='false'{0}>", commonAttrs));
                    sb.AppendLine("<ul class='ErrorViewer' style='BACKGROUND: none transparent scroll repeat 0% 0%'><li>Errorviewer: ErrorViewer</li></ul></span>");
                    break;

                default:
                    if (tag.IsText) {
                        sb.Append(HttpUtility.HtmlEncode(tag.NodeValue ?? ""));
                    } else {
                        if (node.Children != null) {
                            foreach (var child in node.Children) RenderEnhancedNode(child, sb, kbObj);
                        }
                    }
                    break;
            }
        }
    }
}
