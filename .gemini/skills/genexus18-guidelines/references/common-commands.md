# Common Commands Reference

## Loops

### For Each
Used to navigate the database.
~~~
For Each [Order <attr>] [Where <cond>]
    <commands>
EndFor
~~~

### For in
Iterates over collections or SDTs.
~~~
For &Item in &Collection
    <commands>
EndFor
~~~

## Database Operations

### New
Inserts a new database record. Ignores transaction business rules.
~~~
New
    ProductId = &Id
    ProductName = &Name
When duplicate
    msg("Duplicate!")
EndNew
~~~

### Commit / Rollback
- `Commit`: Confirms database changes in the current LUW.
- `Rollback`: Reverts changes in the current LUW.

### Delete
Removes current record from database. Must be used inside `For Each`.

## Subroutines
~~~
Do 'CalculateTotal'

Sub 'CalculateTotal' /* in: &Price, out: &Total */
    &Total = &Price * 1.2
EndSub
~~~

## Constraints
- Variables must be prefixed with `&`.
- Named arguments in `Call` are forbidden; use positional.
- Never use `Do` inside a `For Each` code block if possible; place code directly.
