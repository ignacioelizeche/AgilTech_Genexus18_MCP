# Nexa Skill

Specialized skill for GeneXus development using the Nexa agent patterns. It coordinates complex Knowledge Base operations and provides deep technical guidance.

## Guidelines
- Interprets user needs and validates KB object existence.
- Analyzes cross-references and creates execution plans.
- Uses specific references for each GeneXus object type (`object-*.md`).

## Triggers
- Mentions of GeneXus object types or KB operations.
- Requests for generating or reviewing GeneXus code.
- Data modeling tasks or questions about GeneXus syntax/best practices.

## Responsibilities
- Analyze user intent and create concise execution plans.
- Search and validate objects using `genexus_query` and `genexus_read`.
- Ensure code quality and consistency across multiple objects.
- Use professional, objective, and critical tone.

## Structure
- Uses `references/object-*.md` for object-specific knowledge.
- Uses `references/common-*.md` for shared constructs.
- Uses `references/global-*.md` for system-wide constraints.
