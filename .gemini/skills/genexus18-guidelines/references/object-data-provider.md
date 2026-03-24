# Data Provider Reference

## Definition
A `DataProvider` object (or `DP`) is a declarative construction used to define hierarchical structured data output. It focuses on the output structure rather than retrieval logic.

## Syntax
~~~
DataProvider <name>
{
    <group>
    {
        <assignments>
        <item>
        <clauses>
        {
            <elements>
        }
    }
    #Rules
        parm(<parameters>);
    #End
}
~~~

## Clauses
Clauses control data retrieval and must follow a strict order:
`Output` -> `From` -> `Order` -> `Unique` -> `Using` -> `Input` -> `Where`

- `From`: Specifies base Transactions or Levels.
- `Order`: Sorting criteria.
- `Unique`: Enforces uniqueness on attributes.
- `Where`: Filtering conditions.

## Constraints
- Groups must correspond to existing SDT or BC structures.
- Elements must belong to SDT or BC members.
- Clauses must appear immediately after the group name.
- Recursion is allowed if the SDT/BC is recursive.
