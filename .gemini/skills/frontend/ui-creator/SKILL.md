# UI Creator Skill

Specialized skill for converting UI screenshots and/or OpenAPI specs into complete web application components.

## Workflow

1. **Determine target platform**: React (Vite) or Angular.
2. **Inspect project**: Check existing components and styles.
3. **Analyze inputs**: Infer pages, layout, and common components from images and OpenAPI.
4. **Verify page map**: Confirm the navigation flow with the user.
5. **Generate infrastructure**: Setup routing, services, and base styles.
6. **Verify mock images**: Ensure placeholder images are correctly handled.
7. **Generate components**: Create the page components using `ch-*` elements and Mercury tokens.
8. **API Integration**: Connect to backend services if OpenAPI is provided.

## Critical Rules
- Always use `ch-*` components for UI elements.
- Always use Mercury design tokens for spacing, colors, and typography.
- Never use native HTML buttons or inputs when a Chameleon alternative exists.
- Validate the final output against the original design image.
