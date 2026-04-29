# GeneXus Object Canonical JSON Schema (v2.0.0)

`mode:patch` with an array payload applies RFC 6902 JSON-Patch ops over a _canonical JSON_ view of the part XML produced by `PartAccessor.SerializeToXml()`. The conversion is handled by `ObjectJsonMapper` in `GxMcp.Worker.Helpers`.

## Mapping convention

- XML root element name is preserved as the `rootName` parameter to `ObjectJsonMapper.ToXml`.
- Each top-level child element becomes a JSON key using **lower-camel-case** (first letter lowercased, rest unchanged). `<Description>` → `"description"`.
- Scalar elements become JSON strings: `<Name>Customer</Name>` → `"name": "Customer"`.
- The `<Structure>` element is mapped to a `"structure"` JSON array. Each `<Attribute>` child becomes an object with `"name"` and `"type"` string fields.
- Round-trip fidelity is guaranteed only for the shapes listed above. Attributes with additional sub-elements, comments, XML namespaces, or ordering constraints are **not** preserved — use `mode:xml` or `mode:ops` for those parts.

## Example: Transaction with one attribute

**XML (part XML from GeneXus)**

```xml
<Transaction>
  <Name>Customer</Name>
  <Description>Customer master</Description>
  <Structure>
    <Attribute>
      <Name>CustomerId</Name>
      <Type>Numeric(8.0)</Type>
    </Attribute>
  </Structure>
</Transaction>
```

**Canonical JSON (produced by `ObjectJsonMapper.ToJson`)**

```json
{
  "name": "Customer",
  "description": "Customer master",
  "structure": [
    { "name": "CustomerId", "type": "Numeric(8.0)" }
  ]
}
```

**RFC 6902 patch to add a second attribute**

```json
[
  { "op": "add", "path": "/structure/-", "value": { "name": "CustomerName", "type": "Character(40)" } }
]
```

## Coverage caveat

Only `Transaction.Structure` (and top-level scalar elements) are covered by `ObjectJsonMapper` in v2.0.0. Parts with Procedure source, WebPanel layout XML, or PatternInstance descriptors are **not** supported through `mode:patch`; use `mode:ops` for structural mutations or `mode:xml` for full-overwrite edits on those shapes.
