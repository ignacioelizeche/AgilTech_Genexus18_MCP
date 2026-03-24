# Panel Reference

## Definition
A `Panel` object defines a screen for Web, Android, Apple, or Angular environments.
Types: `WebPanel`, `Panel` (SDPanel), `MasterPanel`, `MasterPage`, `WebComponent`, `Stencil`.

## Syntax
~~~
<type> <name>
{
    #Events
        <events>
    #End
    #Layout
        <layout> (GXML)
    #End
    #Variables
        <variables>
    #End
}
~~~

## Layout Patterns
- **Hero + Action + Content**: Title at top, primary CTA in middle, data below.
- **List + Detail**: Split screen (desktop) or stacked (mobile).
- **Form + Live Feedback**: Fields on left/main, preview/validation on right/bottom.

## Event Execution Order
1. `Start`
2. `Refresh`
3. `Load` (one execution per row)
4. User Events (`Click`, `Enter`, etc.)

## Semantic Contract
Panels should use semantic classes styled by a `DesignSystem`:
- `page`, `page-header`, `page-content`, `page-footer`.
- `btn-primary`, `btn-secondary`, `btn-danger`.
- `text-title`, `text-body`, `text-caption`.

## Constraints
- Avoid hardcoded colors or spacing; use `DesignSystem` classes.
- Every action requires an explicit event with user feedback.
- Use `Stencil` for repeated screen parts.
