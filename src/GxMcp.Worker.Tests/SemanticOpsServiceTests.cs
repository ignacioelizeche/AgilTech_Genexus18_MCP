using GxMcp.Worker;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SemanticOpsServiceTests
    {
        [Fact]
        public void SetAttribute_UpdatesType()
        {
            string trnXml =
                "<Transaction><Name>Customer</Name>" +
                "<Structure><Attribute><Name>CustomerId</Name>" +
                "<Type>Numeric(8.0)</Type></Attribute></Structure></Transaction>";

            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse(
                    "{\"op\":\"set_attribute\",\"name\":\"CustomerId\",\"type\":\"Numeric(10.0)\"}"))
            };
            var result = svc.Apply(trnXml, "Transaction", ops);
            Assert.Contains("Numeric(10.0)", result);
            Assert.DoesNotContain("Numeric(8.0)", result);
        }

        [Fact]
        public void AddAttribute_AppendsUnderStructure()
        {
            string trnXml =
                "<Transaction><Name>Customer</Name>" +
                "<Structure><Attribute><Name>CustomerId</Name>" +
                "<Type>Numeric(8.0)</Type></Attribute></Structure></Transaction>";

            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse(
                    "{\"op\":\"add_attribute\",\"name\":\"CustomerName\",\"type\":\"Character(40)\"}"))
            };
            var result = svc.Apply(trnXml, "Transaction", ops);
            Assert.Contains("<Name>CustomerName</Name>", result);
            Assert.Contains("<Type>Character(40)</Type>", result);
        }

        [Fact]
        public void RemoveAttribute_DeletesMatchingAttribute()
        {
            string trnXml =
                "<Transaction><Name>Customer</Name>" +
                "<Structure>" +
                "<Attribute><Name>CustomerId</Name><Type>Numeric(8.0)</Type></Attribute>" +
                "<Attribute><Name>CustomerName</Name><Type>Character(40)</Type></Attribute>" +
                "</Structure></Transaction>";

            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse(
                    "{\"op\":\"remove_attribute\",\"name\":\"CustomerName\"}"))
            };
            var result = svc.Apply(trnXml, "Transaction", ops);
            Assert.DoesNotContain("CustomerName", result);
            Assert.Contains("CustomerId", result);
        }

        [Fact]
        public void AddRule_AppendsRuleElement()
        {
            string trnXml =
                "<Transaction><Name>Customer</Name>" +
                "<Rules><Rule><Text>error('x') if true;</Text></Rule></Rules>" +
                "</Transaction>";

            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse(
                    "{\"op\":\"add_rule\",\"text\":\"noaccept(CustomerId);\"}"))
            };
            var result = svc.Apply(trnXml, "Transaction", ops);
            Assert.Contains("<Text>noaccept(CustomerId);</Text>", result);
        }

        [Fact]
        public void RemoveRule_DeletesByMatchSubstring()
        {
            string trnXml =
                "<Transaction><Name>Customer</Name>" +
                "<Rules>" +
                "<Rule><Text>error('a') if true;</Text></Rule>" +
                "<Rule><Text>noaccept(CustomerId);</Text></Rule>" +
                "</Rules></Transaction>";

            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse(
                    "{\"op\":\"remove_rule\",\"match\":\"noaccept\"}"))
            };
            var result = svc.Apply(trnXml, "Transaction", ops);
            Assert.DoesNotContain("noaccept", result);
            Assert.Contains("error('a')", result);
        }

        [Fact]
        public void UnknownOp_Throws()
        {
            string trnXml = "<Transaction><Name>Customer</Name></Transaction>";
            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse("{\"op\":\"frobnicate\",\"x\":1}"))
            };
            var ex = Assert.Throws<UsageException>(() => svc.Apply(trnXml, "Transaction", ops));
            Assert.Equal("usage_error", ex.Code);
        }

        [Fact]
        public void SetProperty_UpdatesTopLevelElement()
        {
            string xml = "<Transaction><Name>Customer</Name><Description>old</Description></Transaction>";
            var svc = new SemanticOpsService();
            var ops = new[] { SemanticOp.From(JObject.Parse(
                "{\"op\":\"set_property\",\"path\":\"/Description\",\"value\":\"new\"}")) };
            var result = svc.Apply(xml, "Transaction", ops);
            Assert.Contains("<Description>new</Description>", result);
        }
    }
}
