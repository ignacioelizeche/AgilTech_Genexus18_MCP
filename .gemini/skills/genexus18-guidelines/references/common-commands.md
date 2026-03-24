# Common Commands Reference

## Loops

### For Each
Used to navigate the database.
~~~
For Each [TransactionName | LevelName] [Order <attr>] [Where <cond>]
    <commands>
EndFor
~~~

> [!IMPORTANT]
> **Error SRC0310:** Occurs when specifying a name in `For Each` that is not a Transaction. In many KBs, table levels have the same name as the table but are not standalone transactions.
> **Best Practice:** Prefer an **empty `For Each`** (omitting the name) to let GeneXus infer the base table automatically from the attributes used within the block. This avoids ambiguity and SRC0310 errors.

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
