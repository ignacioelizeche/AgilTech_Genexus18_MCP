# Global GeneXus 18 Guidelines

## Security
- Prefer GAM for authentication and authorization.
- Keep sensitive configuration outside source control.
- Avoid direct SQL when a GeneXus navigation or Business Component can express the same behavior safely.

## Data and transactions
- Use Business Components for insert, update, and delete flows that must respect GeneXus rules and referential integrity.
- Avoid `Commit` inside loops.
- Keep transaction rules concise. Move non-trivial logic into procedures.

## Navigation and performance
- Constrain `For Each` with `Where` or `Defined By`.
- Page large grids and expensive reads.
- Validate navigation plans when changing transactions or heavy procedures.

## KB structure
- Keep modules organized. Do not accumulate unrelated objects in root.
- Use clear naming conventions for Procedures, Transactions, Web Panels, Data Providers, and SDTs.
- Prefer declarative GeneXus structures and supported refactors over raw text surgery.

## Anti-patterns
- No hardcoded URLs when a Location or environment setting should own the value.
- No blocking waits in interactive web flows.
- No direct dependence on hidden or deprecated transport contracts outside MCP.
