# Procedure Reference

## Definition

Procedures represent logic that can update the database, perform calculations, or output reports.

## Constraints

- Use `Parm` rule to define parameters with `in:`, `out:`, `inout:` accessors.
- Use `For Each` exclusively for data filtering, never for navigating or linking tables. (Keep it empty for inference).
- Choose CRUD strategy: BC only if transaction has BC enabled; `For Each` otherwise.
- Attributes in `parm` are passed without `&` for implicit navigation context.

## Syntax

```
Procedure <name>
{
    <source>
    #Rules
        parm(<parameters>);
    #End
}
```

## Report Layout

Declarative XML-based report layout schema used in `#Layout` section.

- `layout` -> `printBlock` -> report controls (reportLabel, reportAttribute, etc.)
- Use one `printBlock` per printed section and keep block names aligned with `Print` commands.

## Examples

### CRUD with For Each

```
// Update
For Each Where ProductId = &Id
    ProductName = &Name
EndFor
Commit
```

### CRUD with Business Component

```
// Update
&Product.Load(&Id)
&Product.ProductName = &Name
&Product.Save()
```
