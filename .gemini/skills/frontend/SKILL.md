# Frontend Skills

This skill group coordinates specialized AI skills to enhance and assist in developing frontend applications in GeneXus and modern web environments.

## Available Skills

### 1. UI Creator
- **Use for**: Converting UI screenshots and/or OpenAPI specs into complete web application components with routing, services, and mock servers.
- **Triggers**: Requests to "generate app from image", "generate UI from OpenAPI", or when a screenshot of a design is provided.

### 2. Mercury Design System
- **Use for**: Styling Chameleon-based UIs using Globant's Mercury design system tokens, bundles, and icons.
- **Triggers**: Requests to "style with Mercury", "use Mercury tokens", or when styling a `ch-*` component.

### 3. Design System Builder
- **Use for**: Scaffolding and evolving enterprise CSS Design Systems on top of Chameleon using the ITCSS layered pattern.
- **Triggers**: Requests to "create a design system", "define tokens", or "setup ITCSS".

### 4. Chameleon Controls Library
- **Use for**: Building UIs with 58 Chameleon web components (`ch-*` elements).
- **Triggers**: Mentions of `ch-` components or building custom web components.

## Coordination Logic
1. If the user provides a design image and wants an app, use **UI Creator**.
2. If the user wants a professional enterprise look for existing components, use **Mercury**.
3. If the user wants to build a brand new design system from scratch, use **Design System Builder**.
4. If the user is asking about specific web component properties or behavior, use **Chameleon Controls Library**.
