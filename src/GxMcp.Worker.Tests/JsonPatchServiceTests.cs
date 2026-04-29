using GxMcp.Worker;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class JsonPatchServiceTests
    {
        private const string TrnXml =
            "<Transaction>" +
            "<Name>Customer</Name>" +
            "<Description>old</Description>" +
            "<Structure>" +
            "<Attribute><Name>CustomerId</Name><Type>Numeric(8.0)</Type></Attribute>" +
            "</Structure>" +
            "</Transaction>";

        private static JArray Patch(string json) => JArray.Parse(json);

        // ── replace ──────────────────────────────────────────────────────────

        [Fact]
        public void Replace_TopLevelScalar_UpdatesValue()
        {
            var svc = new JsonPatchService();
            string result = svc.Apply(TrnXml, "Transaction",
                Patch("[{\"op\":\"replace\",\"path\":\"/description\",\"value\":\"new desc\"}]"));
            Assert.Contains("<Description>new desc</Description>", result);
            Assert.DoesNotContain("old", result);
        }

        [Fact]
        public void Replace_ArrayItem_UpdatesType()
        {
            var svc = new JsonPatchService();
            string result = svc.Apply(TrnXml, "Transaction",
                Patch("[{\"op\":\"replace\",\"path\":\"/structure/0/type\",\"value\":\"Numeric(10.0)\"}]"));
            Assert.Contains("Numeric(10.0)", result);
            Assert.DoesNotContain("Numeric(8.0)", result);
        }

        [Fact]
        public void Replace_NonExistentPath_Throws()
        {
            var svc = new JsonPatchService();
            var ex = Assert.Throws<UsageException>(() =>
                svc.Apply(TrnXml, "Transaction",
                    Patch("[{\"op\":\"replace\",\"path\":\"/noSuchKey\",\"value\":\"x\"}]")));
            Assert.Equal("usage_error", ex.Code);
        }

        // ── remove ───────────────────────────────────────────────────────────

        [Fact]
        public void Remove_TopLevelScalar_DropsElement()
        {
            var svc = new JsonPatchService();
            string result = svc.Apply(TrnXml, "Transaction",
                Patch("[{\"op\":\"remove\",\"path\":\"/description\"}]"));
            Assert.DoesNotContain("Description", result);
            Assert.DoesNotContain("old", result);
        }

        [Fact]
        public void Remove_ArrayItem_RemovesEntry()
        {
            const string xml =
                "<Transaction><Name>Customer</Name>" +
                "<Structure>" +
                "<Attribute><Name>CustomerId</Name><Type>Numeric(8.0)</Type></Attribute>" +
                "<Attribute><Name>CustomerName</Name><Type>Character(40)</Type></Attribute>" +
                "</Structure></Transaction>";
            var svc = new JsonPatchService();
            string result = svc.Apply(xml, "Transaction",
                Patch("[{\"op\":\"remove\",\"path\":\"/structure/1\"}]"));
            Assert.DoesNotContain("CustomerName", result);
            Assert.Contains("CustomerId", result);
        }

        // ── add ──────────────────────────────────────────────────────────────

        [Fact]
        public void Add_AppendToArray_AddsNewItem()
        {
            var svc = new JsonPatchService();
            var newAttr = "{\"name\":\"CustomerName\",\"type\":\"Character(40)\"}";
            string result = svc.Apply(TrnXml, "Transaction",
                Patch("[{\"op\":\"add\",\"path\":\"/structure/-\",\"value\":" + newAttr + "}]"));
            Assert.Contains("CustomerName", result);
            Assert.Contains("Character(40)", result);
            Assert.Contains("CustomerId", result);
        }

        [Fact]
        public void Add_InsertAtIndex_InsertsItem()
        {
            var svc = new JsonPatchService();
            var newAttr = "{\"name\":\"Prefix\",\"type\":\"Character(1)\"}";
            string result = svc.Apply(TrnXml, "Transaction",
                Patch("[{\"op\":\"add\",\"path\":\"/structure/0\",\"value\":" + newAttr + "}]"));
            Assert.Contains("Prefix", result);
            Assert.Contains("CustomerId", result);
        }

        [Fact]
        public void Add_NewTopLevelKey_AddsElement()
        {
            var svc = new JsonPatchService();
            string result = svc.Apply(TrnXml, "Transaction",
                Patch("[{\"op\":\"add\",\"path\":\"/newProp\",\"value\":\"hello\"}]"));
            Assert.Contains("<NewProp>hello</NewProp>", result);
        }

        // ── test ─────────────────────────────────────────────────────────────

        [Fact]
        public void Test_Match_DoesNotThrow()
        {
            var svc = new JsonPatchService();
            // Should complete without exception
            svc.Apply(TrnXml, "Transaction",
                Patch("[{\"op\":\"test\",\"path\":\"/description\",\"value\":\"old\"}]"));
        }

        [Fact]
        public void Test_Mismatch_Throws()
        {
            var svc = new JsonPatchService();
            var ex = Assert.Throws<UsageException>(() =>
                svc.Apply(TrnXml, "Transaction",
                    Patch("[{\"op\":\"test\",\"path\":\"/description\",\"value\":\"wrong\"}]")));
            Assert.Equal("usage_error", ex.Code);
            Assert.Contains("test failed", ex.Message);
        }

        // ── error cases ──────────────────────────────────────────────────────

        [Fact]
        public void UnknownOp_Throws()
        {
            var svc = new JsonPatchService();
            var ex = Assert.Throws<UsageException>(() =>
                svc.Apply(TrnXml, "Transaction",
                    Patch("[{\"op\":\"move\",\"path\":\"/description\",\"from\":\"/name\"}]")));
            Assert.Equal("usage_error", ex.Code);
            Assert.Contains("unknown op", ex.Message);
        }

        [Fact]
        public void MissingPath_Throws()
        {
            var svc = new JsonPatchService();
            var ex = Assert.Throws<UsageException>(() =>
                svc.Apply(TrnXml, "Transaction",
                    Patch("[{\"op\":\"replace\",\"value\":\"x\"}]")));
            Assert.Equal("usage_error", ex.Code);
            Assert.Contains("path", ex.Message);
        }

        [Fact]
        public void MissingOp_Throws()
        {
            var svc = new JsonPatchService();
            var ex = Assert.Throws<UsageException>(() =>
                svc.Apply(TrnXml, "Transaction",
                    Patch("[{\"path\":\"/description\",\"value\":\"x\"}]")));
            Assert.Equal("usage_error", ex.Code);
            Assert.Contains("op", ex.Message);
        }
    }
}
