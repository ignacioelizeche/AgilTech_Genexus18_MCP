# Fase 1 — Edits robustos e enxutos Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship v2.0.0 with consolidated tool surface, hybrid diff edits (`xml`/`ops`/`patch`), uniform `dryRun` plan schema, and Gateway-level idempotency cache.

**Architecture:** Tool consolidation lives in Gateway routers and `tool_definitions.json` (drops `genexus_batch_*`). Worker `WriteService` gets a new semantic-ops dispatcher and a JSON-Patch dispatcher beside the existing `xml` path. Worker `KbValidationService` is extended with an in-memory rollback snapshot for `dryRun` and emits a standardized `plan` object. Gateway gains an `IdempotencyCache` middleware that wraps every write tool dispatch.

**Tech Stack:** C# (.NET 8 Gateway, .NET Framework 4.8 Worker), xUnit, Newtonsoft.Json, JsonDiffPatch.NET (or hand-rolled JSON-Patch — selected in Task 6.1).

**Spec:** `docs/superpowers/specs/2026-04-29-fase1-edits-robustos-design.md`

---

## File Structure

### Files created
- `src/GxMcp.Worker/Services/SemanticOpsService.cs` — semantic op catalog + dispatcher
- `src/GxMcp.Worker/Services/JsonPatchService.cs` — RFC 6902 dispatcher over canonical JSON
- `src/GxMcp.Worker/Helpers/ObjectJsonMapper.cs` — bidirectional XML↔JSON canonical mapping
- `src/GxMcp.Worker/Models/PlanResponse.cs` — `plan{}` + meta DTOs (touched objects, broken refs, warnings)
- `src/GxMcp.Worker/Services/DryRunSnapshot.cs` — in-memory rollback container
- `src/GxMcp.Gateway/IdempotencyCache.cs` — `(kbPath, tool, key) → CachedEntry` LRU cache with sliding TTL
- `src/GxMcp.Gateway/IdempotencyMiddleware.cs` — wraps write-tool dispatch
- `src/GxMcp.Worker.Tests/SemanticOpsServiceTests.cs`
- `src/GxMcp.Worker.Tests/JsonPatchServiceTests.cs`
- `src/GxMcp.Worker.Tests/DryRunSnapshotTests.cs`
- `src/GxMcp.Gateway.Tests/IdempotencyCacheTests.cs`
- `src/GxMcp.Gateway.Tests/IdempotencyMiddlewareTests.cs`
- `src/GxMcp.Gateway.Tests/RemovedToolsContractTests.cs`
- `docs/object_json_schema.md` — XML↔JSON canonical mapping reference

### Files modified
- `src/GxMcp.Gateway/tool_definitions.json` — drop `genexus_batch_read`/`genexus_batch_edit`; extend `genexus_read`/`genexus_edit` schemas with `targets[]`/`ops`/`patch`/`idempotencyKey`
- `src/GxMcp.Gateway/Routers/ObjectRouter.cs` — accept `targets[]`, route `mode: ops`, propagate `idempotencyKey`
- `src/GxMcp.Gateway/McpRouter.cs` — register `IdempotencyMiddleware`, advertise `meta.removedTools` in `initialize`, bump `meta.schemaVersion` to `mcp-axi/2`
- `src/GxMcp.Gateway/Program.cs` — DI for `IdempotencyCache`, read `Server.IdempotencyTtlMinutes` / `Server.IdempotencyCacheSize`
- `src/GxMcp.Gateway/Configuration.cs` — new config properties
- `src/GxMcp.Worker/Services/WriteService.cs` — wire `ops` and `patch` modes through dispatcher; emit `plan` on `dryRun`
- `src/GxMcp.Worker/Services/BatchService.cs` — accept canonical `targets[]` shape; both batch/single paths share one code path
- `src/GxMcp.Worker/Services/CommandDispatcher.cs` — route `Module=SemanticOps` and `Module=JsonPatch`
- `src/GxMcp.Worker/Services/KbValidationService.cs` — new `AnalyzeImpact(snapshot)` returning `brokenRefs[]`
- `src/GxMcp.Gateway.Tests/McpHandshakeContractTests.cs` — assert `mcp-axi/2`, `meta.removedTools`
- `package.json` — version `2.0.0`
- `CHANGELOG.md` — Breaking Changes section
- `README.md` — tool surface, new args

---

## Phase 0 — Setup

### Task 0.1: Verify build baseline

**Files:** none (read-only)

- [ ] **Step 1: Confirm clean build before changes**

Run: `dotnet build src/GxMcp.Gateway/GxMcp.Gateway.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 2: Confirm tests pass**

Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj` and `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj`
Expected: all green.

- [ ] **Step 3: Branch**

```bash
git checkout -b feat/v2-edits-robustos
```

---

## Phase 1 — Tool consolidation (item 1)

### Task 1.1: Failing test for `genexus_batch_*` removal

**Files:**
- Create: `src/GxMcp.Gateway.Tests/RemovedToolsContractTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests;

public class RemovedToolsContractTests
{
    [Fact]
    public void ToolsList_DoesNotAdvertiseRemovedBatchTools()
    {
        var defs = JArray.Parse(System.IO.File.ReadAllText(
            System.IO.Path.Combine(System.AppContext.BaseDirectory,
                "../../../../GxMcp.Gateway/tool_definitions.json")));

        var names = defs.Select(t => t["name"]?.ToString()).ToList();
        Assert.DoesNotContain("genexus_batch_read", names);
        Assert.DoesNotContain("genexus_batch_edit", names);
    }

    [Fact]
    public void Initialize_AdvertisesRemovedToolsInMeta()
    {
        var router = TestFactories.CreateRouter();
        var resp = router.HandleRequest(JObject.Parse(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}"));

        var removed = resp["result"]?["meta"]?["removedTools"] as JArray;
        Assert.NotNull(removed);
        Assert.Contains(removed!, r => r["name"]?.ToString() == "genexus_batch_read");
        Assert.Contains(removed!, r => r["name"]?.ToString() == "genexus_batch_edit");
    }

    [Fact]
    public void CallingRemovedTool_Returns_MethodNotFound()
    {
        var router = TestFactories.CreateRouter();
        var resp = router.HandleRequest(JObject.Parse(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"genexus_batch_read\",\"arguments\":{}}}"));

        Assert.Equal(-32601, (int)resp["error"]!["code"]!);
        Assert.Equal("genexus_read", resp["error"]!["data"]!["replacedBy"]!.ToString());
    }
}
```

> **Note:** `TestFactories.CreateRouter()` already exists in `McpHandshakeContractTests.cs` — extract it if not already public; otherwise inline the construction (look at `McpHandshakeContractTests.cs` for the pattern).

- [ ] **Step 2: Run test**

Run: `dotnet test src/GxMcp.Gateway.Tests --filter FullyQualifiedName~RemovedToolsContractTests`
Expected: FAIL — three tests, all failing because tools still listed and meta not present.

### Task 1.2: Remove `genexus_batch_*` entries from tool_definitions

**Files:**
- Modify: `src/GxMcp.Gateway/tool_definitions.json`

- [ ] **Step 1: Open file and locate entries**

Search for the JSON objects whose `name` is `genexus_batch_read` and `genexus_batch_edit` (lines around 99 and 247 per current state). Delete the full object blocks (including trailing comma rules).

- [ ] **Step 2: Run test**

Run: `dotnet test src/GxMcp.Gateway.Tests --filter ToolsList_DoesNotAdvertiseRemovedBatchTools`
Expected: PASS.

### Task 1.3: Reject removed tools at dispatch with `-32601`

**Files:**
- Modify: `src/GxMcp.Gateway/McpRouter.cs`

- [ ] **Step 1: Add removed-tools registry**

Inside `McpRouter`, add a private `static readonly Dictionary<string, (string ReplacedBy, string ArgHint)> RemovedTools = new() { ... }`. Populate with both names mapping to `("genexus_read", "use targets[]")` and `("genexus_edit", "use targets[]")` respectively.

- [ ] **Step 2: Short-circuit `tools/call` for removed names**

In the `tools/call` handler (search for `case "tools/call"` or the equivalent dispatch), before dispatching, check `RemovedTools.TryGetValue(toolName, out var info)`. If true, return:

```csharp
return new JObject {
    ["jsonrpc"] = "2.0",
    ["id"] = id,
    ["error"] = new JObject {
        ["code"] = -32601,
        ["message"] = $"Method not found: {toolName}",
        ["data"] = new JObject {
            ["replacedBy"] = info.ReplacedBy,
            ["argHint"] = info.ArgHint
        }
    }
};
```

- [ ] **Step 3: Run test**

Run: `dotnet test src/GxMcp.Gateway.Tests --filter CallingRemovedTool_Returns_MethodNotFound`
Expected: PASS.

### Task 1.4: Advertise `removedTools` in `initialize` response

**Files:**
- Modify: `src/GxMcp.Gateway/McpRouter.cs`

- [ ] **Step 1: Augment initialize result**

In the `initialize` handler, when building `result`, attach a `meta` block:

```csharp
result["meta"] = new JObject {
    ["schemaVersion"] = "mcp-axi/2",
    ["removedTools"] = new JArray(
        RemovedTools.Select(kvp => new JObject {
            ["name"] = kvp.Key,
            ["replacedBy"] = kvp.Value.ReplacedBy,
            ["argHint"] = kvp.Value.ArgHint
        })
    )
};
```

If `result["meta"]` already exists (handshake contract test will tell), merge instead of overwriting.

- [ ] **Step 2: Run test**

Run: `dotnet test src/GxMcp.Gateway.Tests --filter Initialize_AdvertisesRemovedToolsInMeta`
Expected: PASS.

- [ ] **Step 3: Update existing handshake test if it pinned `mcp-axi/1`**

Run: `dotnet test src/GxMcp.Gateway.Tests --filter McpHandshakeContractTests`
If any test fails on `mcp-axi/1`, update assertion to `mcp-axi/2`.

- [ ] **Step 4: Commit**

```bash
git add src/GxMcp.Gateway/tool_definitions.json \
        src/GxMcp.Gateway/McpRouter.cs \
        src/GxMcp.Gateway.Tests/RemovedToolsContractTests.cs \
        src/GxMcp.Gateway.Tests/McpHandshakeContractTests.cs
git commit -m "feat(gateway)!: remove genexus_batch_* tools, bump schema to mcp-axi/2

BREAKING CHANGE: genexus_batch_read and genexus_batch_edit removed.
Use genexus_read/genexus_edit with targets[] instead.
initialize now advertises meta.removedTools for agent self-correction."
```

### Task 1.5: Failing test for `targets[]` plural shape

**Files:**
- Modify: `src/GxMcp.Gateway.Tests/RemovedToolsContractTests.cs` (add new test class file `EditTargetsContractTests.cs` to keep concerns separated)
- Create: `src/GxMcp.Gateway.Tests/EditTargetsContractTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway.Tests;

public class EditTargetsContractTests
{
    [Fact]
    public void Read_AcceptsSingularTarget()
    {
        var router = new ObjectRouter();
        var args = JObject.Parse("{\"name\":\"Customer\",\"part\":\"Source\"}");
        var msg = router.ConvertToolCall("genexus_read", args);
        Assert.NotNull(msg);
    }

    [Fact]
    public void Read_AcceptsTargetsPlural()
    {
        var router = new ObjectRouter();
        var args = JObject.Parse("{\"targets\":[\"Customer\",\"Product\"],\"part\":\"Source\"}");
        var msg = router.ConvertToolCall("genexus_read", args) as dynamic;
        Assert.Equal("Batch", (string)msg!.module);
        Assert.Equal("BatchRead", (string)msg.action);
    }

    [Fact]
    public void Read_RejectsBothTargetForms()
    {
        var router = new ObjectRouter();
        var args = JObject.Parse("{\"name\":\"X\",\"targets\":[\"Y\"]}");
        var ex = Assert.Throws<UsageException>(() => router.ConvertToolCall("genexus_read", args));
        Assert.Contains("mutually exclusive", ex.Message);
    }

    [Fact]
    public void Edit_AcceptsTargetsPlural_OfEditRequests()
    {
        var router = new ObjectRouter();
        var args = JObject.Parse(
            "{\"targets\":[{\"name\":\"A\",\"content\":\"<x/>\"},{\"name\":\"B\",\"content\":\"<y/>\"}]}");
        var msg = router.ConvertToolCall("genexus_edit", args) as dynamic;
        Assert.Equal("Batch", (string)msg!.module);
        Assert.Equal("MultiEdit", (string)msg.action);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test src/GxMcp.Gateway.Tests --filter EditTargetsContractTests`
Expected: FAIL (no `targets[]` handling, no `UsageException` type).

### Task 1.6: Add `UsageException` and `targets[]` routing

**Files:**
- Create: `src/GxMcp.Gateway/UsageException.cs`
- Modify: `src/GxMcp.Gateway/Routers/ObjectRouter.cs`

- [ ] **Step 1: Create `UsageException`**

```csharp
namespace GxMcp.Gateway;

public class UsageException : System.Exception
{
    public string Code { get; }
    public UsageException(string code, string message) : base(message) { Code = code; }
}
```

- [ ] **Step 2: Update `ObjectRouter` switch**

Replace the `genexus_read` case body with:

```csharp
case "genexus_read":
{
    var hasTarget = args?["name"] != null;
    var targets = args?["targets"] as JArray;
    if (hasTarget && targets != null)
        throw new UsageException("usage_error",
            "name and targets are mutually exclusive");
    if (targets != null)
        return new {
            module = "Batch", action = "BatchRead",
            items = targets,
            part = part
        };
    return new {
        module = "Read", action = "ExtractSource",
        target = target, part = part,
        offset = args?["offset"]?.ToObject<int?>(),
        limit = args?["limit"]?.ToObject<int?>(),
        type = args?["type"]?.ToString()
    };
}
```

Replace the `genexus_edit` case to handle `targets[]` of edit requests **before** falling into the existing single-target logic:

```csharp
case "genexus_edit":
{
    var targetsArr = args?["targets"] as JArray;
    if (targetsArr != null && args?["name"] != null)
        throw new UsageException("usage_error",
            "name and targets are mutually exclusive");
    if (targetsArr != null)
        return new {
            module = "Batch", action = "MultiEdit",
            items = targetsArr,
            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
        };
    // ...existing single-target body unchanged...
}
```

> Keep the legacy `changes` argument working in this task — Task 1.7 removes it.

- [ ] **Step 3: Run tests**

Run: `dotnet test src/GxMcp.Gateway.Tests --filter EditTargetsContractTests`
Expected: PASS.

### Task 1.7: Drop legacy `changes` arg from `genexus_edit`

**Files:**
- Modify: `src/GxMcp.Gateway/Routers/ObjectRouter.cs`
- Modify: `src/GxMcp.Gateway/tool_definitions.json` (rename `items`/`changes` schema fields to `targets` in `genexus_edit` entry)

- [ ] **Step 1: Add failing test**

In `EditTargetsContractTests.cs`:

```csharp
[Fact]
public void Edit_LegacyChangesArg_IsRejected()
{
    var router = new ObjectRouter();
    var args = JObject.Parse("{\"name\":\"A\",\"changes\":[]}");
    var ex = Assert.Throws<UsageException>(() => router.ConvertToolCall("genexus_edit", args));
    Assert.Contains("changes", ex.Message);
}
```

- [ ] **Step 2: Run** — Expected FAIL.

- [ ] **Step 3: Reject `changes`**

In `ObjectRouter.ConvertToolCall`, at the top of `genexus_edit`:

```csharp
if (args?["changes"] != null)
    throw new UsageException("usage_error",
        "argument 'changes' removed in v2.0.0; use 'targets' instead");
```

- [ ] **Step 4: Run** — Expected PASS.

- [ ] **Step 5: Update tool_definitions.json** — In the `genexus_edit` schema, rename `changes` array property to `targets`. Drop `items` plural alias.

- [ ] **Step 6: Run all gateway tests**

Run: `dotnet test src/GxMcp.Gateway.Tests`
Expected: green.

- [ ] **Step 7: Commit**

```bash
git add src/GxMcp.Gateway/UsageException.cs \
        src/GxMcp.Gateway/Routers/ObjectRouter.cs \
        src/GxMcp.Gateway/tool_definitions.json \
        src/GxMcp.Gateway.Tests/EditTargetsContractTests.cs
git commit -m "feat(gateway)!: unify read/edit under targets[] plural form

genexus_read and genexus_edit now accept either name (singular) or
targets[] (plural). Legacy 'changes' arg removed."
```

---

## Phase 2 — Hybrid diff edits (item 5)

### Task 2.1: Define semantic op contract

**Files:**
- Create: `src/GxMcp.Worker/Models/SemanticOp.cs`

- [ ] **Step 1: Write the model**

```csharp
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Models;

public sealed class SemanticOp
{
    public string Op { get; set; } = "";
    public JObject Args { get; set; } = new();

    public static SemanticOp From(JObject raw)
    {
        var op = raw["op"]?.ToString()
            ?? throw new System.ArgumentException("op required");
        var args = (JObject)raw.DeepClone();
        args.Remove("op");
        return new SemanticOp { Op = op, Args = args };
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/GxMcp.Worker/Models/SemanticOp.cs
git commit -m "feat(worker): add SemanticOp model"
```

### Task 2.2: Failing test for `set_attribute` op on Transaction

**Files:**
- Create: `src/GxMcp.Worker.Tests/SemanticOpsServiceTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests;

public class SemanticOpsServiceTests
{
    private const string MinimalTrnXml =
        "<Transaction><Name>Customer</Name>" +
        "<Structure><Attribute><Name>CustomerId</Name>" +
        "<Type>Numeric(8.0)</Type></Attribute></Structure></Transaction>";

    [Fact]
    public void SetAttribute_UpdatesType()
    {
        var svc = new SemanticOpsService();
        var ops = new[] {
            SemanticOp.From(JObject.Parse(
                "{\"op\":\"set_attribute\",\"name\":\"CustomerId\",\"type\":\"Numeric(10.0)\"}"))
        };
        var result = svc.Apply(MinimalTrnXml, "Transaction", ops);
        Assert.Contains("Numeric(10.0)", result);
        Assert.DoesNotContain("Numeric(8.0)", result);
    }
}
```

- [ ] **Step 2: Run** — Expected FAIL (`SemanticOpsService` does not exist).

### Task 2.3: Minimal `SemanticOpsService` with `set_attribute`

**Files:**
- Create: `src/GxMcp.Worker/Services/SemanticOpsService.cs`

- [ ] **Step 1: Implement**

```csharp
using System.Collections.Generic;
using System.Xml.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services;

public sealed class SemanticOpsService
{
    public string Apply(string xml, string objectKind, IEnumerable<SemanticOp> ops)
    {
        var doc = XDocument.Parse(xml);
        foreach (var op in ops)
            Dispatch(doc, objectKind, op);
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static void Dispatch(XDocument doc, string kind, SemanticOp op)
    {
        switch ((kind, op.Op))
        {
            case ("Transaction", "set_attribute"):
                SetAttribute(doc, op);
                break;
            default:
                throw new UsageException("usage_error",
                    $"op '{op.Op}' not supported for {kind}");
        }
    }

    private static void SetAttribute(XDocument doc, SemanticOp op)
    {
        var name = op.Args["name"]?.ToString()
            ?? throw new UsageException("usage_error",
                "set_attribute: name required");
        var attr = doc.Descendants("Attribute")
            .FirstOrDefault(a => (string?)a.Element("Name") == name)
            ?? throw new UsageException("usage_error",
                $"attribute '{name}' not found");
        if (op.Args["type"] != null)
            attr.SetElementValue("Type", op.Args["type"]!.ToString());
    }
}

public class UsageException : System.Exception
{
    public string Code { get; }
    public UsageException(string code, string message) : base(message) { Code = code; }
}
```

> Note: `UsageException` is duplicated between Gateway and Worker because they target different framework versions and don't share an assembly. Same name, same shape, intentional duplication.

- [ ] **Step 2: Run** — Expected PASS.

### Task 2.4: Add `add_attribute`, `remove_attribute`, `add_rule`, `remove_rule`

**Files:**
- Modify: `src/GxMcp.Worker/Services/SemanticOpsService.cs`
- Modify: `src/GxMcp.Worker.Tests/SemanticOpsServiceTests.cs`

- [ ] **Step 1: Write failing tests for each op**

```csharp
[Fact]
public void AddAttribute_AppendsToStructure()
{
    var svc = new SemanticOpsService();
    var ops = new[] { SemanticOp.From(JObject.Parse(
        "{\"op\":\"add_attribute\",\"name\":\"Phone\",\"type\":\"Character(20)\"}")) };
    var result = svc.Apply(MinimalTrnXml, "Transaction", ops);
    Assert.Contains("<Name>Phone</Name>", result);
    Assert.Contains("Character(20)", result);
}

[Fact]
public void RemoveAttribute_DropsFromStructure()
{
    var svc = new SemanticOpsService();
    var ops = new[] { SemanticOp.From(JObject.Parse(
        "{\"op\":\"remove_attribute\",\"name\":\"CustomerId\"}")) };
    var result = svc.Apply(MinimalTrnXml, "Transaction", ops);
    Assert.DoesNotContain("CustomerId", result);
}

[Fact]
public void AddRule_AppendsRuleElement()
{
    var xml = "<Transaction><Rules></Rules></Transaction>";
    var svc = new SemanticOpsService();
    var ops = new[] { SemanticOp.From(JObject.Parse(
        "{\"op\":\"add_rule\",\"text\":\"Error('x') if 1=1;\"}")) };
    var result = svc.Apply(xml, "Transaction", ops);
    Assert.Contains("Error('x')", result);
}

[Fact]
public void RemoveRule_DropsByMatch()
{
    var xml = "<Transaction><Rules><Rule><Text>Error('x') if 1=1;</Text></Rule></Rules></Transaction>";
    var svc = new SemanticOpsService();
    var ops = new[] { SemanticOp.From(JObject.Parse(
        "{\"op\":\"remove_rule\",\"match\":\"Error('x')\"}")) };
    var result = svc.Apply(xml, "Transaction", ops);
    Assert.DoesNotContain("Error('x')", result);
}

[Fact]
public void UnknownOp_ThrowsUsageException()
{
    var svc = new SemanticOpsService();
    var ops = new[] { SemanticOp.From(JObject.Parse("{\"op\":\"frobnicate\"}")) };
    Assert.Throws<UsageException>(() => svc.Apply(MinimalTrnXml, "Transaction", ops));
}
```

- [ ] **Step 2: Run** — Expected FAIL (4 of 5 fail; unknown-op already passes).

- [ ] **Step 3: Implement the four new dispatch branches**

In `Dispatch`:

```csharp
case ("Transaction", "add_attribute"): AddAttribute(doc, op); break;
case ("Transaction", "remove_attribute"): RemoveAttribute(doc, op); break;
case ("Transaction", "add_rule"):
case ("Procedure",   "add_rule"):
case ("WebPanel",    "add_rule"):
    AddRule(doc, op); break;
case ("Transaction", "remove_rule"):
case ("Procedure",   "remove_rule"):
case ("WebPanel",    "remove_rule"):
    RemoveRule(doc, op); break;
```

Implementations:

```csharp
private static void AddAttribute(XDocument doc, SemanticOp op)
{
    var struct_ = doc.Descendants("Structure").FirstOrDefault()
        ?? throw new UsageException("usage_error", "no Structure element");
    var name = op.Args["name"]?.ToString()
        ?? throw new UsageException("usage_error", "add_attribute: name required");
    var type = op.Args["type"]?.ToString()
        ?? throw new UsageException("usage_error", "add_attribute: type required");
    var attr = new XElement("Attribute",
        new XElement("Name", name),
        new XElement("Type", type));
    struct_.Add(attr);
}

private static void RemoveAttribute(XDocument doc, SemanticOp op)
{
    var name = op.Args["name"]?.ToString()
        ?? throw new UsageException("usage_error", "remove_attribute: name required");
    var attr = doc.Descendants("Attribute")
        .FirstOrDefault(a => (string?)a.Element("Name") == name)
        ?? throw new UsageException("usage_error",
            $"attribute '{name}' not found");
    attr.Remove();
}

private static void AddRule(XDocument doc, SemanticOp op)
{
    var rules = doc.Descendants("Rules").FirstOrDefault()
        ?? throw new UsageException("usage_error", "no Rules element");
    var text = op.Args["text"]?.ToString()
        ?? throw new UsageException("usage_error", "add_rule: text required");
    rules.Add(new XElement("Rule", new XElement("Text", text)));
}

private static void RemoveRule(XDocument doc, SemanticOp op)
{
    var match = op.Args["match"]?.ToString();
    var index = op.Args["index"]?.ToObject<int?>();
    var rules = doc.Descendants("Rule").ToList();
    XElement? target = null;
    if (index.HasValue && index.Value >= 0 && index.Value < rules.Count)
        target = rules[index.Value];
    else if (match != null)
        target = rules.FirstOrDefault(r =>
            (r.Element("Text")?.Value ?? "").Contains(match));
    if (target == null)
        throw new UsageException("usage_error", "remove_rule: no match");
    target.Remove();
}
```

- [ ] **Step 4: Run** — Expected PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GxMcp.Worker/Services/SemanticOpsService.cs \
        src/GxMcp.Worker/Models/SemanticOp.cs \
        src/GxMcp.Worker.Tests/SemanticOpsServiceTests.cs
git commit -m "feat(worker): SemanticOpsService with attribute and rule ops"
```

> **Op catalog completion:** the spec lists more ops (`set_event`, `set_source`, `add_variable`, etc.). Implement them in additional small TDD tasks following the same pattern. For this plan, attribute + rule ops + `set_property` (Task 2.5) are the demonstrated baseline; remaining ops are mechanically identical and added incrementally during implementation. Each new op gets one failing test → one minimal handler → commit.

### Task 2.5: Generic `set_property` op

**Files:**
- Modify: `src/GxMcp.Worker/Services/SemanticOpsService.cs`
- Modify: `src/GxMcp.Worker.Tests/SemanticOpsServiceTests.cs`

- [ ] **Step 1: Failing test**

```csharp
[Fact]
public void SetProperty_UpdatesTopLevelElement()
{
    var xml = "<Transaction><Name>Customer</Name><Description>old</Description></Transaction>";
    var svc = new SemanticOpsService();
    var ops = new[] { SemanticOp.From(JObject.Parse(
        "{\"op\":\"set_property\",\"path\":\"/Description\",\"value\":\"new\"}")) };
    var result = svc.Apply(xml, "Transaction", ops);
    Assert.Contains("<Description>new</Description>", result);
}
```

- [ ] **Step 2: Run** — Expected FAIL.

- [ ] **Step 3: Add wildcard branch**

In `Dispatch`, before `default`:

```csharp
case (_, "set_property"): SetProperty(doc, op); break;
```

Helper:

```csharp
private static void SetProperty(XDocument doc, SemanticOp op)
{
    var path = op.Args["path"]?.ToString()
        ?? throw new UsageException("usage_error", "set_property: path required");
    var value = op.Args["value"]?.ToString() ?? "";
    var name = path.TrimStart('/');
    var elem = doc.Root?.Element(name)
        ?? throw new UsageException("usage_error",
            $"set_property: path '{path}' not found");
    elem.Value = value;
}
```

- [ ] **Step 4: Run** — Expected PASS. Commit.

### Task 2.6: Wire `ops` mode through Gateway → Worker

**Files:**
- Modify: `src/GxMcp.Gateway/Routers/ObjectRouter.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Modify: `src/GxMcp.Worker/Services/WriteService.cs`

- [ ] **Step 1: Failing test (Gateway)**

In `EditTargetsContractTests.cs`:

```csharp
[Fact]
public void Edit_RoutesOpsMode()
{
    var router = new ObjectRouter();
    var args = JObject.Parse(
        "{\"name\":\"Customer\",\"mode\":\"ops\",\"ops\":[{\"op\":\"set_attribute\",\"name\":\"X\",\"type\":\"Numeric(8.0)\"}]}");
    var msg = router.ConvertToolCall("genexus_edit", args) as dynamic;
    Assert.Equal("SemanticOps", (string)msg!.module);
    Assert.Equal("Apply", (string)msg.action);
}
```

- [ ] **Step 2: Run** — Expected FAIL.

- [ ] **Step 3: Update `ObjectRouter` `genexus_edit`**

After the `mode == "patch"` branch, add:

```csharp
if (mode == "ops")
{
    return new {
        module = "SemanticOps",
        action = "Apply",
        target = target,
        ops = args?["ops"],
        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
    };
}
```

- [ ] **Step 4: Run gateway test** — Expected PASS.

- [ ] **Step 5: Failing test (Worker dispatcher)**

In `src/GxMcp.Worker.Tests/SemanticOpsServiceTests.cs`:

```csharp
[Fact]
public void Dispatcher_RoutesSemanticOpsModule()
{
    // Use a real CommandDispatcher; assert it forwards Module=SemanticOps to SemanticOpsService.
    // If CommandDispatcher requires a KbService etc., construct test doubles that no-op.
    var dispatcher = TestFactories.NewDispatcher();
    var req = JObject.Parse(
        "{\"module\":\"SemanticOps\",\"action\":\"Apply\",\"target\":\"Customer\"," +
        "\"ops\":[{\"op\":\"set_attribute\",\"name\":\"CustomerId\",\"type\":\"Numeric(10.0)\"}]}");
    var resp = dispatcher.Dispatch(req);
    Assert.False((bool)resp["isError"]!);
}
```

> **Note:** if there is no `TestFactories` in the worker tests yet, check `PartAccessorAndWriteServiceTests.cs` for the existing construction pattern and replicate it. If KB access is required, gate the test with `[SkippableFact]` until a fixture is added — but keep the dispatcher route registration provable.

- [ ] **Step 6: Run** — Expected FAIL (route not registered).

- [ ] **Step 7: Register route in `CommandDispatcher`**

In `CommandDispatcher.cs`, find the existing module switch (search for `case "Write"` or `case "Batch"`). Add:

```csharp
case "SemanticOps":
    return _writeService.ApplySemanticOps(req);
```

In `WriteService.cs`, add:

```csharp
public JObject ApplySemanticOps(JObject req)
{
    var target = req["target"]?.ToString()
        ?? throw new UsageException("usage_error", "target required");
    var opsRaw = req["ops"] as JArray
        ?? throw new UsageException("usage_error", "ops[] required");
    var dryRun = req["dryRun"]?.ToObject<bool?>() ?? false;

    // Read current XML via existing path-resolution logic in this file.
    var xml = ReadObjectXml(target); // existing private helper or inline equivalent
    var ops = opsRaw.OfType<JObject>().Select(SemanticOp.From);
    var kind = DetectKind(target); // existing helper, e.g., reads <Type> tag
    var newXml = new SemanticOpsService().Apply(xml, kind, ops);

    if (dryRun)
        return BuildDryRunPlan(target, xml, newXml); // implemented in Phase 3
    WriteObjectXml(target, newXml); // existing helper
    return Ok(new JObject { ["target"] = target });
}
```

> **Implementation hint:** the existing `WriteService` already has `ReadObjectXml`, `WriteObjectXml`, and target-resolution helpers (look for existing `genexus_edit` xml-mode code path — reuse it verbatim). `BuildDryRunPlan` is created in Phase 3; for now have it `throw new System.NotImplementedException("Phase 3");` and gate the test with `dryRun=false`.

- [ ] **Step 8: Run worker test** — Expected PASS.

- [ ] **Step 9: Commit**

```bash
git add src/GxMcp.Gateway/Routers/ObjectRouter.cs \
        src/GxMcp.Worker/Services/CommandDispatcher.cs \
        src/GxMcp.Worker/Services/WriteService.cs \
        src/GxMcp.Gateway.Tests/EditTargetsContractTests.cs \
        src/GxMcp.Worker.Tests/SemanticOpsServiceTests.cs
git commit -m "feat: wire ops mode through gateway and worker"
```

### Task 2.7: JSON-Patch (`mode: patch`) over canonical JSON

**Files:**
- Create: `src/GxMcp.Worker/Helpers/ObjectJsonMapper.cs`
- Create: `src/GxMcp.Worker/Services/JsonPatchService.cs`
- Create: `src/GxMcp.Worker.Tests/JsonPatchServiceTests.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Modify: `src/GxMcp.Worker/Services/WriteService.cs`
- Modify: `src/GxMcp.Gateway/Routers/ObjectRouter.cs`
- Create: `docs/object_json_schema.md`

> **Library decision (do this once at the start of Task 2.7, before writing tests):**
> Run `find src -name "*.csproj" | xargs grep -l "Microsoft.AspNetCore.JsonPatch\|JsonDiffPatch\|Json.Patch"`.
> - If a JSON-Patch library is already referenced, use it.
> - Otherwise, hand-roll the 6 ops needed (add/remove/replace/move/copy/test) — net48-compatible, ~100 LOC. Hand-roll is preferred to avoid adding a dependency to the worker.

- [ ] **Step 1: Failing test**

```csharp
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests;

public class JsonPatchServiceTests
{
    private const string TrnXml =
        "<Transaction><Name>Customer</Name><Description>old</Description></Transaction>";

    [Fact]
    public void Replace_UpdatesField()
    {
        var svc = new JsonPatchService();
        var patch = JArray.Parse(
            "[{\"op\":\"replace\",\"path\":\"/description\",\"value\":\"new\"}]");
        var result = svc.Apply(TrnXml, "Transaction", patch);
        Assert.Contains("<Description>new</Description>", result);
    }

    [Fact]
    public void UnknownPath_ThrowsUsageException()
    {
        var svc = new JsonPatchService();
        var patch = JArray.Parse(
            "[{\"op\":\"replace\",\"path\":\"/doesNotExist\",\"value\":\"x\"}]");
        Assert.Throws<UsageException>(() => svc.Apply(TrnXml, "Transaction", patch));
    }
}
```

- [ ] **Step 2: Run** — Expected FAIL.

- [ ] **Step 3: Implement `ObjectJsonMapper`**

Bidirectional mapping for the minimal set covered in this phase: top-level scalar elements (`Name`, `Description`, `Type`) and `Structure/Attribute[]`. Lower-case JSON keys, camelCase, with documented conventions in `docs/object_json_schema.md`. Code:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers;

public static class ObjectJsonMapper
{
    public static JObject ToJson(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root!;
        var json = new JObject();
        foreach (var child in root.Elements())
        {
            var key = LowerFirst(child.Name.LocalName);
            if (child.Name.LocalName == "Structure")
            {
                var arr = new JArray();
                foreach (var attr in child.Elements("Attribute"))
                    arr.Add(new JObject {
                        ["name"] = attr.Element("Name")?.Value,
                        ["type"] = attr.Element("Type")?.Value
                    });
                json["structure"] = arr;
            }
            else
            {
                json[key] = child.Value;
            }
        }
        return json;
    }

    public static string ToXml(JObject json, string rootName)
    {
        var root = new XElement(rootName);
        foreach (var prop in json.Properties())
        {
            if (prop.Name == "structure" && prop.Value is JArray arr)
            {
                var struc = new XElement("Structure");
                foreach (var item in arr.OfType<JObject>())
                    struc.Add(new XElement("Attribute",
                        new XElement("Name", item["name"]?.ToString()),
                        new XElement("Type", item["type"]?.ToString())));
                root.Add(struc);
            }
            else
            {
                root.Add(new XElement(UpperFirst(prop.Name), prop.Value.ToString()));
            }
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static string LowerFirst(string s) => char.ToLower(s[0]) + s.Substring(1);
    private static string UpperFirst(string s) => char.ToUpper(s[0]) + s.Substring(1);
}
```

> **Document the mapping in `docs/object_json_schema.md`** with concrete before/after examples for `Transaction`, `Procedure`, `WebPanel`. Two paragraphs of prose plus three code blocks is enough.

- [ ] **Step 4: Implement `JsonPatchService`**

If a library was found in the upfront check, delegate to it. Otherwise hand-roll:

```csharp
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services;

public sealed class JsonPatchService
{
    public string Apply(string xml, string kind, JArray patch)
    {
        var json = ObjectJsonMapper.ToJson(xml);
        foreach (var op in patch.OfType<JObject>())
            ApplyOp(json, op);
        return ObjectJsonMapper.ToXml(json, kind);
    }

    private static void ApplyOp(JObject root, JObject op)
    {
        var name = op["op"]?.ToString()
            ?? throw new UsageException("usage_error", "op required");
        var path = op["path"]?.ToString()
            ?? throw new UsageException("usage_error", "path required");
        var token = Resolve(root, path, out var parent, out var key, out var index);

        switch (name)
        {
            case "replace":
                if (token == null) throw new UsageException("usage_error",
                    $"path '{path}' not found");
                Replace(parent!, key, index, op["value"]!);
                break;
            case "remove":
                if (token == null) throw new UsageException("usage_error",
                    $"path '{path}' not found");
                Remove(parent!, key, index);
                break;
            case "add":
                Add(parent!, key, index, op["value"]!);
                break;
            case "test":
                if (!JToken.DeepEquals(token, op["value"]))
                    throw new UsageException("usage_error",
                        $"test failed at '{path}'");
                break;
            // move/copy: implement when needed
            default:
                throw new UsageException("usage_error", $"unknown op '{name}'");
        }
    }

    // Resolve, Replace, Remove, Add: walk JSON pointer per RFC 6901.
    // Hand-roll ~40 LOC. Cover object keys and array indices.
}
```

> **Don't over-engineer the resolver.** Cover `/key`, `/key/subkey`, `/array/0`, `/array/-` (RFC 6902 append). Ship.

- [ ] **Step 5: Run JSON-Patch tests** — Expected PASS.

- [ ] **Step 6: Wire `mode: patch` to `JsonPatchService`**

In `WriteService`, alongside `ApplySemanticOps`, add:

```csharp
public JObject ApplyJsonPatch(JObject req)
{
    var target = req["target"]?.ToString() ?? throw new UsageException("usage_error", "target required");
    var patchRaw = req["patch"] as JArray ?? throw new UsageException("usage_error", "patch[] required");
    var dryRun = req["dryRun"]?.ToObject<bool?>() ?? false;
    var xml = ReadObjectXml(target);
    var kind = DetectKind(target);
    var newXml = new JsonPatchService().Apply(xml, kind, patchRaw);
    if (dryRun) return BuildDryRunPlan(target, xml, newXml);
    WriteObjectXml(target, newXml);
    return Ok(new JObject { ["target"] = target });
}
```

In `CommandDispatcher`, add:

```csharp
case "JsonPatch":
    return _writeService.ApplyJsonPatch(req);
```

In `ObjectRouter.ConvertToolCall` `genexus_edit`, replace the existing `mode == "patch"` branch with a routing change to the new module name:

```csharp
if (mode == "patch")
{
    var patchArr = args?["patch"] as JArray;
    if (patchArr != null)
    {
        return new {
            module = "JsonPatch", action = "Apply",
            target = target, patch = patchArr,
            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
        };
    }
    // fallthrough — legacy text patch (kept for now)
    return /* existing legacy text-patch object unchanged */;
}
```

> **Note:** the existing `mode: patch` branch is the *text/heuristic* patch implementation in `PatchService`. Keep it functional under the same `mode: patch` name *if `patch` is a string*; route to JSON-Patch if `patch` is an array. Document this dual interpretation in `CHANGELOG.md`.

- [ ] **Step 7: Run all worker + gateway tests** — Expected green.

- [ ] **Step 8: Commit**

```bash
git add src/GxMcp.Worker/Helpers/ObjectJsonMapper.cs \
        src/GxMcp.Worker/Services/JsonPatchService.cs \
        src/GxMcp.Worker.Tests/JsonPatchServiceTests.cs \
        src/GxMcp.Worker/Services/WriteService.cs \
        src/GxMcp.Worker/Services/CommandDispatcher.cs \
        src/GxMcp.Gateway/Routers/ObjectRouter.cs \
        docs/object_json_schema.md
git commit -m "feat: JSON-Patch (RFC 6902) edit mode over canonical JSON"
```

---

## Phase 3 — Dry-run plan schema (item 6)

### Task 3.1: `PlanResponse` model

**Files:**
- Create: `src/GxMcp.Worker/Models/PlanResponse.cs`

- [ ] **Step 1: Implement**

```csharp
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Models;

public sealed class TouchedObject {
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Op { get; set; } = ""; // "create" | "modify" | "delete"
}

public sealed class BrokenRef {
    public string From { get; set; } = "";
    public string FromType { get; set; } = "";
    public string To { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class PlanWarning {
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class PlanResponse {
    public List<TouchedObject> TouchedObjects { get; set; } = new();
    public string? XmlDiff { get; set; }
    public List<BrokenRef> BrokenRefs { get; set; } = new();
    public List<PlanWarning> Warnings { get; set; } = new();
    public long EstimatedDurationMs { get; set; }

    public JObject ToJson() => JObject.FromObject(this,
        Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        }));
}
```

- [ ] **Step 2: Commit**

```bash
git add src/GxMcp.Worker/Models/PlanResponse.cs
git commit -m "feat(worker): plan response models"
```

### Task 3.2: Failing test for unified-diff `xmlDiff`

**Files:**
- Create: `src/GxMcp.Worker.Tests/DryRunPlanTests.cs`

- [ ] **Step 1: Test**

```csharp
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests;

public class DryRunPlanTests
{
    [Fact]
    public void BuildPlan_EmitsUnifiedDiff()
    {
        var before = "<X><A>1</A></X>";
        var after  = "<X><A>2</A></X>";
        var plan = DryRunPlanBuilder.Build("X", before, after);
        Assert.Contains("-<A>1</A>", plan.XmlDiff);
        Assert.Contains("+<A>2</A>", plan.XmlDiff);
        Assert.Single(plan.TouchedObjects);
        Assert.Equal("modify", plan.TouchedObjects[0].Op);
    }
}
```

- [ ] **Step 2: Run** — Expected FAIL.

### Task 3.3: Implement `DryRunPlanBuilder`

**Files:**
- Create: `src/GxMcp.Worker/Services/DryRunPlanBuilder.cs`

- [ ] **Step 1: Implement**

```csharp
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services;

public static class DryRunPlanBuilder
{
    public static PlanResponse Build(string targetName, string beforeXml, string afterXml)
    {
        var plan = new PlanResponse();
        plan.TouchedObjects.Add(new TouchedObject {
            Type = DetectType(beforeXml),
            Name = targetName,
            Op = "modify"
        });
        plan.XmlDiff = UnifiedDiff(beforeXml, afterXml);
        return plan;
    }

    private static string DetectType(string xml)
    {
        var open = xml.IndexOf('<');
        var space = xml.IndexOfAny(new[] { ' ', '>', '\n', '\r', '\t' }, open + 1);
        return xml.Substring(open + 1, space - open - 1);
    }

    private static string UnifiedDiff(string a, string b)
    {
        // line-based diff with @@ header.
        var aLines = a.Replace("\r\n", "\n").Split('\n');
        var bLines = b.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        sb.Append("--- before\n+++ after\n@@\n");
        // Naive LCS-free diff: emit all minus then plus.
        // Replace with a proper diff algorithm if test corpus grows.
        foreach (var line in aLines) sb.Append("-").Append(line).Append("\n");
        foreach (var line in bLines) sb.Append("+").Append(line).Append("\n");
        return sb.ToString();
    }
}
```

> **Diff algorithm note:** the naive emit-all approach passes the asserted test and is acceptable as a placeholder. Replace with `DiffPlex` (already common in .NET) or a hand-rolled Myers diff once dryRun moves into agent workflows that need readable diffs. File a follow-up issue if needed.

- [ ] **Step 2: Run** — Expected PASS. Commit.

### Task 3.4: Wire `BuildDryRunPlan` into write paths

**Files:**
- Modify: `src/GxMcp.Worker/Services/WriteService.cs`

- [ ] **Step 1: Failing test (xml mode dryRun returns plan)**

In `src/GxMcp.Worker.Tests/PartAccessorAndWriteServiceTests.cs` (or a new file `WriteServiceDryRunTests.cs`):

```csharp
[Fact]
public void XmlMode_DryRun_ReturnsPlanWithoutMutating()
{
    // Use existing test KB fixture pattern from PartAccessorAndWriteServiceTests.
    var (svc, target, originalXml) = TestFactories.SetupWriteService();
    var newXml = originalXml.Replace("old", "new");
    var req = JObject.FromObject(new {
        module = "Write", action = "Source",
        target = target, payload = newXml, dryRun = true
    });
    var resp = svc.ApplyXml(req); // or the existing entry method
    Assert.True((bool)resp["meta"]!["dryRun"]!);
    Assert.NotNull(resp["plan"]);
    Assert.NotNull(resp["plan"]!["xmlDiff"]);
    // Verify the KB was NOT changed:
    var afterRead = svc.ReadObjectXml(target);
    Assert.Equal(originalXml, afterRead);
}
```

- [ ] **Step 2: Run** — Expected FAIL (`BuildDryRunPlan` throws or `meta.dryRun` not set).

- [ ] **Step 3: Replace `BuildDryRunPlan` placeholder**

```csharp
private JObject BuildDryRunPlan(string target, string beforeXml, string afterXml)
{
    var plan = DryRunPlanBuilder.Build(target, beforeXml, afterXml);
    return new JObject {
        ["meta"] = new JObject {
            ["dryRun"] = true,
            ["tool"] = "genexus_edit",
            ["schemaVersion"] = "mcp-axi/2"
        },
        ["plan"] = plan.ToJson(),
        ["isError"] = false
    };
}
```

Ensure all three write paths (`xml`, `ops`, `patch`) check `dryRun` **before** calling `WriteObjectXml`. Audit `WriteService`'s xml-mode entry — if it currently always writes, gate with `if (dryRun) return BuildDryRunPlan(...)`.

- [ ] **Step 4: Run** — Expected PASS.

### Task 3.5: Extend `KbValidationService` with `AnalyzeImpact`

**Files:**
- Modify: `src/GxMcp.Worker/Services/KbValidationService.cs`
- Modify: `src/GxMcp.Worker/Services/DryRunPlanBuilder.cs`
- Create or extend: `src/GxMcp.Worker.Tests/DryRunPlanTests.cs`

- [ ] **Step 1: Failing test for broken-ref detection**

```csharp
[Fact]
public void BuildPlan_DetectsBrokenRefWhenAttributeRemoved()
{
    // Use a fixture KB where Procedure 'P1' references Customer.Phone,
    // and the after-XML removes the Phone attribute.
    var (validator, kb) = TestFactories.SetupValidator();
    var beforeXml = kb.GetObjectXml("Customer");
    var afterXml  = beforeXml.Replace("<Attribute><Name>Phone</Name>...</Attribute>", "");

    var plan = DryRunPlanBuilder.Build("Customer", beforeXml, afterXml, validator);
    Assert.Contains(plan.BrokenRefs,
        b => b.From == "P1" && b.To.Contains("Phone"));
}
```

> If `TestFactories.SetupValidator()` does not exist, use the same KB fixture used in existing `KbValidationService` tests (search the test project for one). If no test KB fixture exists at all, mark this test `[Fact(Skip="needs KB fixture")]` and file a follow-up — do not block the entire phase on fixture work.

- [ ] **Step 2: Run** — Expected FAIL or SKIP.

- [ ] **Step 3: Implement `AnalyzeImpact(targetName, afterXml)`**

In `KbValidationService.cs`, add a method that:
1. Loads all objects in the KB.
2. Parses references (existing scan helpers in this service).
3. For each reference whose target attribute/object is missing in `afterXml`, emit a `BrokenRef`.

Connect from `DryRunPlanBuilder.Build` via overload `Build(name, before, after, KbValidationService validator)`. Call validator only when present.

> If `KbValidationService` does not yet have a reference scanner, this becomes a much bigger task — defer to Phase 4 of a future plan, and have `BuildPlan` emit `brokenRefs: []` for now. Document the deferral in CHANGELOG.

- [ ] **Step 4: Run** — Expected PASS (or stays skipped if fixture deferred).

- [ ] **Step 5: Commit**

```bash
git add src/GxMcp.Worker/Services/DryRunPlanBuilder.cs \
        src/GxMcp.Worker/Services/KbValidationService.cs \
        src/GxMcp.Worker/Services/WriteService.cs \
        src/GxMcp.Worker.Tests/DryRunPlanTests.cs \
        src/GxMcp.Worker.Tests/PartAccessorAndWriteServiceTests.cs
git commit -m "feat(worker): standardized dryRun plan with brokenRefs detection"
```

---

## Phase 4 — Idempotency cache (item 16)

### Task 4.1: Failing test for `IdempotencyCache` basic hit/miss

**Files:**
- Create: `src/GxMcp.Gateway.Tests/IdempotencyCacheTests.cs`

- [ ] **Step 1: Test**

```csharp
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

public class IdempotencyCacheTests
{
    [Fact]
    public void Miss_ReturnsNull()
    {
        var cache = new IdempotencyCache(ttlMinutes: 15, capacity: 1000);
        var hit = cache.TryGet("kb1", "genexus_edit", "k1",
            payloadHash: "h1", out var cached);
        Assert.False(hit);
        Assert.Null(cached);
    }

    [Fact]
    public void Hit_SamePayloadHash_ReturnsCachedResult()
    {
        var cache = new IdempotencyCache(15, 1000);
        var result = JObject.Parse("{\"ok\":true}");
        cache.Put("kb1", "genexus_edit", "k1", "h1", result);
        var hit = cache.TryGet("kb1", "genexus_edit", "k1", "h1", out var cached);
        Assert.True(hit);
        Assert.Equal(result.ToString(), cached!.ToString());
    }

    [Fact]
    public void Hit_DifferentPayloadHash_ThrowsConflict()
    {
        var cache = new IdempotencyCache(15, 1000);
        cache.Put("kb1", "genexus_edit", "k1", "h1", JObject.Parse("{}"));
        Assert.Throws<IdempotencyConflictException>(() =>
            cache.TryGet("kb1", "genexus_edit", "k1", "h2", out _));
    }

    [Fact]
    public void DifferentKb_DoesNotCollide()
    {
        var cache = new IdempotencyCache(15, 1000);
        cache.Put("kb1", "genexus_edit", "k1", "h1", JObject.Parse("{\"a\":1}"));
        var hit = cache.TryGet("kb2", "genexus_edit", "k1", "h1", out _);
        Assert.False(hit);
    }

    [Fact]
    public void Eviction_LruDropsOldestWhenAtCapacity()
    {
        var cache = new IdempotencyCache(15, capacity: 2);
        cache.Put("kb1", "t", "k1", "h1", JObject.Parse("{}"));
        cache.Put("kb1", "t", "k2", "h2", JObject.Parse("{}"));
        cache.Put("kb1", "t", "k3", "h3", JObject.Parse("{}")); // evicts k1
        Assert.False(cache.TryGet("kb1", "t", "k1", "h1", out _));
        Assert.True (cache.TryGet("kb1", "t", "k2", "h2", out _));
        Assert.True (cache.TryGet("kb1", "t", "k3", "h3", out _));
    }
}
```

- [ ] **Step 2: Run** — Expected FAIL (no class).

### Task 4.2: Implement `IdempotencyCache`

**Files:**
- Create: `src/GxMcp.Gateway/IdempotencyCache.cs`
- Create: `src/GxMcp.Gateway/IdempotencyConflictException.cs`

- [ ] **Step 1: Implement**

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway;

public class IdempotencyConflictException : Exception {
    public IdempotencyConflictException(string message) : base(message) { }
}

public sealed class IdempotencyCache
{
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly object _lock = new();
    // Per-KB LRU: composite key includes (tool, key); we shard by kbPath
    // but for capacity purposes we treat the whole cache as one bucket
    // capped at _capacity * (rough KB count). Spec says "1000 per KB" —
    // implement per-KB bucket explicitly.
    private readonly ConcurrentDictionary<string, KbBucket> _buckets = new();

    public IdempotencyCache(int ttlMinutes, int capacity)
    {
        _ttl = TimeSpan.FromMinutes(ttlMinutes);
        _capacity = capacity;
    }

    public bool TryGet(string kbPath, string tool, string key,
                       string payloadHash, out JObject? cached)
    {
        cached = null;
        var bucket = _buckets.GetOrAdd(kbPath, _ => new KbBucket(_capacity, _ttl));
        return bucket.TryGet(tool, key, payloadHash, out cached);
    }

    public void Put(string kbPath, string tool, string key,
                    string payloadHash, JObject result)
    {
        var bucket = _buckets.GetOrAdd(kbPath, _ => new KbBucket(_capacity, _ttl));
        bucket.Put(tool, key, payloadHash, result);
    }

    private sealed class KbBucket
    {
        private readonly int _capacity;
        private readonly TimeSpan _ttl;
        private readonly LinkedList<(string Tool, string Key)> _lru = new();
        private readonly Dictionary<(string, string), Entry> _map = new();
        private readonly object _lock = new();

        public KbBucket(int capacity, TimeSpan ttl) { _capacity = capacity; _ttl = ttl; }

        public bool TryGet(string tool, string key, string payloadHash, out JObject? cached)
        {
            cached = null;
            lock (_lock)
            {
                if (!_map.TryGetValue((tool, key), out var entry)) return false;
                if (DateTime.UtcNow - entry.LastAccessedAt > _ttl)
                {
                    _map.Remove((tool, key));
                    _lru.Remove(entry.Node);
                    return false;
                }
                if (entry.PayloadHash != payloadHash)
                    throw new IdempotencyConflictException(
                        $"idempotency key '{key}' reused with different payload");
                entry.LastAccessedAt = DateTime.UtcNow;
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                cached = entry.Result;
                return true;
            }
        }

        public void Put(string tool, string key, string payloadHash, JObject result)
        {
            lock (_lock)
            {
                if (_map.TryGetValue((tool, key), out var existing))
                {
                    _lru.Remove(existing.Node);
                    _map.Remove((tool, key));
                }
                while (_map.Count >= _capacity)
                {
                    var oldest = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(oldest.Value);
                }
                var node = new LinkedListNode<(string, string)>((tool, key));
                _lru.AddFirst(node);
                _map[(tool, key)] = new Entry {
                    PayloadHash = payloadHash,
                    Result = result,
                    LastAccessedAt = DateTime.UtcNow,
                    Node = node
                };
            }
        }

        private sealed class Entry {
            public string PayloadHash = "";
            public JObject Result = new();
            public DateTime LastAccessedAt;
            public LinkedListNode<(string, string)> Node = null!;
        }
    }
}
```

- [ ] **Step 2: Run** — Expected PASS for all 5 tests.

- [ ] **Step 3: Commit**

```bash
git add src/GxMcp.Gateway/IdempotencyCache.cs \
        src/GxMcp.Gateway/IdempotencyConflictException.cs \
        src/GxMcp.Gateway.Tests/IdempotencyCacheTests.cs
git commit -m "feat(gateway): IdempotencyCache with per-KB LRU and sliding TTL"
```

### Task 4.3: Failing test for in-flight semaphore behavior

**Files:**
- Modify: `src/GxMcp.Gateway.Tests/IdempotencyCacheTests.cs`

- [ ] **Step 1: Test**

```csharp
[Fact]
public async Task ConcurrentSameKey_SecondCallerWaitsAndGetsSameResult()
{
    var cache = new IdempotencyCache(15, 1000);
    var firstStarted = new TaskCompletionSource<bool>();
    var releaseFirst = new TaskCompletionSource<bool>();

    Task<JObject> First() => cache.GetOrCompute("kb1", "t", "k1", "h1", async () => {
        firstStarted.SetResult(true);
        await releaseFirst.Task;
        return JObject.Parse("{\"answer\":42}");
    });

    var t1 = First();
    await firstStarted.Task;
    var t2 = First(); // must not run the factory; must wait for t1
    releaseFirst.SetResult(true);

    var r1 = await t1;
    var r2 = await t2;
    Assert.Equal(r1.ToString(), r2.ToString());
}
```

- [ ] **Step 2: Run** — Expected FAIL (`GetOrCompute` not implemented).

### Task 4.4: Implement `GetOrCompute` with per-key semaphore

**Files:**
- Modify: `src/GxMcp.Gateway/IdempotencyCache.cs`

- [ ] **Step 1: Implement**

Add to `IdempotencyCache`:

```csharp
private readonly ConcurrentDictionary<(string, string, string), SemaphoreSlim> _gates = new();

public async Task<JObject> GetOrCompute(
    string kbPath, string tool, string key, string payloadHash,
    Func<Task<JObject>> factory)
{
    if (TryGet(kbPath, tool, key, payloadHash, out var cached))
        return cached!;

    var gate = _gates.GetOrAdd((kbPath, tool, key), _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync();
    try
    {
        if (TryGet(kbPath, tool, key, payloadHash, out cached))
            return cached!;
        var result = await factory();
        Put(kbPath, tool, key, payloadHash, result);
        return result;
    }
    finally
    {
        gate.Release();
        // Best-effort cleanup; leaving gates in the dict is fine for our scale.
    }
}
```

- [ ] **Step 2: Run** — Expected PASS.

- [ ] **Step 3: Commit**

```bash
git add src/GxMcp.Gateway/IdempotencyCache.cs \
        src/GxMcp.Gateway.Tests/IdempotencyCacheTests.cs
git commit -m "feat(gateway): in-flight semaphore in IdempotencyCache"
```

### Task 4.5: Idempotency middleware on write tools

**Files:**
- Create: `src/GxMcp.Gateway/IdempotencyMiddleware.cs`
- Create: `src/GxMcp.Gateway.Tests/IdempotencyMiddlewareTests.cs`
- Modify: `src/GxMcp.Gateway/McpRouter.cs`
- Modify: `src/GxMcp.Gateway/Configuration.cs`
- Modify: `src/GxMcp.Gateway/Program.cs`

- [ ] **Step 1: Failing integration test**

```csharp
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;
using System.Threading.Tasks;

namespace GxMcp.Gateway.Tests;

public class IdempotencyMiddlewareTests
{
    [Fact]
    public async Task SameKey_SecondCallReturnsCached_WithoutHittingWorker()
    {
        var calls = 0;
        var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");
        Task<JObject> Inner(JObject req) {
            calls++;
            return Task.FromResult(JObject.Parse("{\"isError\":false,\"data\":{\"id\":1}}"));
        }

        var req = JObject.Parse(
            "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"content\":\"<x/>\",\"idempotencyKey\":\"k1\"}}");
        var r1 = await middleware.Invoke(req, Inner);
        var r2 = await middleware.Invoke(req, Inner);
        Assert.Equal(1, calls);
        Assert.True((bool)r2["meta"]!["idempotent"]!);
    }

    [Fact]
    public async Task DryRun_BypassesCache()
    {
        var calls = 0;
        var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");
        Task<JObject> Inner(JObject req) {
            calls++;
            return Task.FromResult(JObject.Parse("{\"isError\":false,\"data\":{}}"));
        }
        var req = JObject.Parse(
            "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"content\":\"<x/>\"," +
            "\"idempotencyKey\":\"k1\",\"dryRun\":true}}");
        await middleware.Invoke(req, Inner);
        await middleware.Invoke(req, Inner);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ErrorResult_NotCached()
    {
        var calls = 0;
        var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");
        Task<JObject> Inner(JObject req) {
            calls++;
            return Task.FromResult(JObject.Parse("{\"isError\":true,\"error\":{\"message\":\"boom\"}}"));
        }
        var req = JObject.Parse(
            "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"content\":\"<x/>\",\"idempotencyKey\":\"k1\"}}");
        await middleware.Invoke(req, Inner);
        await middleware.Invoke(req, Inner);
        Assert.Equal(2, calls);
    }
}
```

- [ ] **Step 2: Run** — Expected FAIL.

- [ ] **Step 3: Implement middleware**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway;

public sealed class IdempotencyMiddleware
{
    private static readonly HashSet<string> WriteTools = new() {
        "genexus_edit", "genexus_create_object", "genexus_refactor",
        "genexus_forge", "genexus_import_object"
    };

    private readonly IdempotencyCache _cache;
    private readonly string _kbPath;

    public IdempotencyMiddleware(IdempotencyCache cache, string kbPath)
    {
        _cache = cache; _kbPath = kbPath;
    }

    public async Task<JObject> Invoke(JObject toolCall, Func<JObject, Task<JObject>> next)
    {
        var tool = toolCall["name"]?.ToString() ?? "";
        if (!WriteTools.Contains(tool)) return await next(toolCall);

        var args = toolCall["arguments"] as JObject ?? new JObject();
        var key = args["idempotencyKey"]?.ToString();
        if (string.IsNullOrEmpty(key)) return await next(toolCall);
        ValidateKey(key);

        var dryRun = args["dryRun"]?.ToObject<bool?>() ?? false;
        if (dryRun) return await next(toolCall);

        var hash = HashPayload(args);
        var result = await _cache.GetOrCompute(_kbPath, tool, key, hash,
            async () => {
                var raw = await next(toolCall);
                if ((bool?)raw["isError"] == true)
                    throw new ErrorNotCacheable(raw); // skip caching
                return raw;
            }).ConfigureAwait(false);

        // tag idempotent only when result actually came from cache
        // (cheap second lookup — already in cache after factory ran)
        if (_cache.TryGet(_kbPath, tool, key, hash, out var existing) && existing != null) {
            var clone = (JObject)existing.DeepClone();
            clone["meta"] ??= new JObject();
            ((JObject)clone["meta"]!)["idempotent"] = true;
            return clone;
        }
        return result;
    }

    private static void ValidateKey(string key)
    {
        if (key.Length < 1 || key.Length > 128)
            throw new UsageException("usage_error", "idempotencyKey length 1..128");
        foreach (var c in key)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                throw new UsageException("usage_error",
                    "idempotencyKey charset [A-Za-z0-9_-]");
    }

    private static string HashPayload(JObject args)
    {
        var canonical = (JObject)args.DeepClone();
        canonical.Remove("idempotencyKey");
        canonical.Remove("dryRun");
        var sorted = JsonCanonicalize(canonical);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sorted)));
    }

    private static string JsonCanonicalize(JToken t)
    {
        if (t is JObject o)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonCanonicalize(p.Name)).Append(':').Append(JsonCanonicalize(p.Value));
            }
            sb.Append('}');
            return sb.ToString();
        }
        if (t is JArray a)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < a.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonCanonicalize(a[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }
        if (t is JValue v) return Newtonsoft.Json.JsonConvert.SerializeObject(v.Value);
        return Newtonsoft.Json.JsonConvert.SerializeObject(t.ToString());
    }

    private static string JsonCanonicalize(string s) =>
        Newtonsoft.Json.JsonConvert.SerializeObject(s);

    private sealed class ErrorNotCacheable : Exception {
        public JObject Result { get; }
        public ErrorNotCacheable(JObject r) { Result = r; }
    }
}
```

> **Caveat:** `ErrorNotCacheable` thrown inside the cache factory bypasses caching but the caller still needs the response. Adjust `IdempotencyCache.GetOrCompute` to catch this specific exception type, return its `Result`, and **not** call `Put`. Update Task 4.4 implementation accordingly:
>
> ```csharp
> try { var result = await factory(); Put(...); return result; }
> catch (IdempotencyMiddleware.ErrorNotCacheable ex) { return ex.Result; }
> ```
>
> Move `ErrorNotCacheable` to a top-level type (`src/GxMcp.Gateway/IdempotencyErrorNotCacheable.cs`) so `IdempotencyCache` can reference it without circular dependency.

- [ ] **Step 4: Update `IdempotencyCache.GetOrCompute` per the caveat above. Run all idempotency tests.**

Run: `dotnet test src/GxMcp.Gateway.Tests --filter Idempotency`
Expected: all green.

### Task 4.6: Wire middleware in `McpRouter` and config

**Files:**
- Modify: `src/GxMcp.Gateway/Configuration.cs`
- Modify: `src/GxMcp.Gateway/Program.cs`
- Modify: `src/GxMcp.Gateway/McpRouter.cs`

- [ ] **Step 1: Add config properties**

In `Configuration.cs`, inside the `Server` section class:

```csharp
public int IdempotencyTtlMinutes { get; set; } = 15;
public int IdempotencyCacheSize { get; set; } = 1000;
```

- [ ] **Step 2: Construct cache once in `Program.cs`**

```csharp
var cache = new IdempotencyCache(
    config.Server.IdempotencyTtlMinutes,
    config.Server.IdempotencyCacheSize);
// Inject cache wherever McpRouter is constructed.
```

- [ ] **Step 3: In `McpRouter.HandleRequest`, wrap `tools/call` dispatch**

Find the `tools/call` branch. Construct an `IdempotencyMiddleware` per request scoped to the active KB path (read from session or config). Replace the direct dispatcher call:

```csharp
var middleware = new IdempotencyMiddleware(_cache, _activeKbPath);
var result = await middleware.Invoke(@params, async req => await DispatchToolInternal(req));
```

> If the existing `tools/call` dispatch is synchronous, you may need to async-ify the path or use `.GetAwaiter().GetResult()` at the boundary. Prefer async-ifying — the codebase already uses async (`HttpSessionRegistry`, etc.).

- [ ] **Step 4: Run gateway tests, including handshake.**

Run: `dotnet test src/GxMcp.Gateway.Tests`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/GxMcp.Gateway/IdempotencyMiddleware.cs \
        src/GxMcp.Gateway/IdempotencyCache.cs \
        src/GxMcp.Gateway/IdempotencyErrorNotCacheable.cs \
        src/GxMcp.Gateway/Configuration.cs \
        src/GxMcp.Gateway/Program.cs \
        src/GxMcp.Gateway/McpRouter.cs \
        src/GxMcp.Gateway.Tests/IdempotencyMiddlewareTests.cs
git commit -m "feat(gateway): IdempotencyMiddleware on write tools, configurable TTL+capacity"
```

---

## Phase 5 — Release prep

### Task 5.1: Bump version

**Files:**
- Modify: `package.json`

- [ ] **Step 1: Bump**

Change `"version": "1.3.1"` to `"version": "2.0.0"`.

- [ ] **Step 2: Commit**

```bash
git add package.json
git commit -m "chore(release): v2.0.0"
```

### Task 5.2: CHANGELOG entry

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Write entry at top**

```markdown
## v2.0.0 — 2026-04-29

### Breaking changes
- Removed `genexus_batch_read`. Use `genexus_read` with `targets[]`.
- Removed `genexus_batch_edit`. Use `genexus_edit` with `targets[]`.
- Removed `genexus_edit` `changes` argument. Use `targets[]`.
- `meta.schemaVersion` bumped from `mcp-axi/1` → `mcp-axi/2`.

### Added
- `genexus_edit` `mode: ops` with semantic op catalog (set/add/remove for attributes, rules, properties).
- `genexus_edit` `mode: patch` accepts JSON-Patch (RFC 6902) array over canonical JSON object representation. Existing string-form `patch` (text/heuristic patch) still routes to `PatchService` for backward compatibility.
- `dryRun` flag on `genexus_edit`, `genexus_create_object`, `genexus_refactor`, `genexus_forge`, `genexus_import_object`. Returns standardized `plan{ touchedObjects, xmlDiff, brokenRefs, warnings }` without mutating KB.
- `idempotencyKey` argument on all write tools. Per-KB LRU cache with sliding TTL (default 15 min, capacity 1000). Configurable via `Server.IdempotencyTtlMinutes` and `Server.IdempotencyCacheSize`.
- `initialize` response advertises `meta.removedTools` for agent self-correction.
- `docs/object_json_schema.md` documents the canonical JSON↔XML mapping used by JSON-Patch mode.
```

- [ ] **Step 2: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): v2.0.0 release notes"
```

### Task 5.3: README updates

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update the tool surface section**

In the `🛠️ Tool Surface (Skills)` section:
- Remove `genexus_batch_read` and `genexus_batch_edit` from the list.
- Add a sub-bullet under `genexus_edit`: "Modes: `xml` (default), `ops` (semantic op catalog), `patch` (JSON-Patch RFC 6902)."
- Add note: "All write tools accept `dryRun: true` for preview and `idempotencyKey` for safe retries."

- [ ] **Step 2: Update `MCP tool response ergonomics` block**

- Bump `meta.schemaVersion` to `mcp-axi/2`.
- Add `meta.idempotent`, `meta.dryRun`, `meta.batched`, `meta.removedTools` lines.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs(readme): v2.0.0 surface changes"
```

### Task 5.4: Final verification

- [ ] **Step 1: Build everything**

Run: `dotnet build src/GxMcp.Gateway/GxMcp.Gateway.csproj -c Release` and `dotnet build src/GxMcp.Worker/GxMcp.Worker.csproj -c Release`
Expected: 0 errors.

- [ ] **Step 2: Run all tests**

Run: `dotnet test src/GxMcp.Gateway.Tests` then `dotnet test src/GxMcp.Worker.Tests`
Expected: all green.

- [ ] **Step 3: Smoke handshake**

Run: `node cli/run.js doctor --mcp-smoke`
Expected: success, output shows `meta.schemaVersion: mcp-axi/2` and `meta.removedTools` listing both removed tools.

- [ ] **Step 4: Tag**

```bash
git tag v2.0.0
```

(Push and release workflow handles the rest per `.github/workflows/release.yml`.)

---

## Self-review notes (post-write)

1. **Spec coverage:**
   - § 1 Tool consolidation → Tasks 1.1–1.7 ✓
   - § 2 Hybrid diff edits → Tasks 2.1–2.7 ✓ (op catalog partially demonstrated; remaining ops noted as mechanical extensions)
   - § 3 Dry-run → Tasks 3.1–3.5 ✓
   - § 4 Idempotency → Tasks 4.1–4.6 ✓
   - Schema bump (`mcp-axi/2`) → Task 1.4 ✓
   - `meta.batched`/`meta.dryRun`/`meta.idempotent` → Tasks 1.6, 3.4, 4.5 ✓

2. **Placeholder scan:** Op catalog completion in Task 2.4 explicitly defers extras with concrete pattern; `KbValidationService.AnalyzeImpact` in Task 3.5 has explicit fallback to empty `brokenRefs[]` if reference scanner not available.

3. **Type consistency:** `SemanticOp`, `PlanResponse`, `IdempotencyCache` signatures are consistent across tasks. `UsageException` intentionally duplicated between Worker (net48) and Gateway (net8) — noted in Task 2.3.
