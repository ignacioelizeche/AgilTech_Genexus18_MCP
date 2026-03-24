# Transaction Reference

## Definition
A `Transaction` object (or `TRN`) represents real-world entities mapping to database tables. GeneXus automatically normalizes to third normal form (3NF). Each `Transaction` maps to one or more Table objects.

## Syntax
~~~
Transaction <name>
{
    <attributes>
    #Rules
        <rules>
    #End
    #Events
        <events>
    #End
    #Variables
        <variables>
    #End
    #Properties
        <properties>
    #End
}
~~~

## Relationships

### STRONG Relationship
Separate transactions reference each other via Foreign Key (FK).
- Use when related entities have independent lifecycle.
- Model 1:N with FK on the N-side Transaction using `[]` for inferred FK and extended attributes.

### WEAK Relationship
Sublevel (subordinate entity) inside transaction.
- Use when subordinate entity lifecycle depends on parent Transaction.
- Model 1:N by defining subordinate `level` inside parent Transaction body.

## Constraints
- Every attribute must have `DataType` property or empty brackets `[]`.
- Primary key required (at least one attribute with `*`).
- Only one description attribute (`!`) allowed.
- FK attributes declared with empty brackets `[]`.
- Determine STRONG vs WEAK relationship based on entity independence.

## Conventions
- Transaction name: PascalCase singular noun.
- Attribute name: PascalCase, descriptive.
- PK attributes first, then FK, then description, then others.
