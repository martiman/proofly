# Proofly Web Application

## Status

Vue 3 + TypeScript + Ant Design Vue + Vite scaffold ([ADR 0003](../../docs/architecture/decisions/0003-web-technology-stack.md)). It currently contains the application shell and navigation for the seven oMVP sections; each screen is a placeholder.

```bash
npm install
npm run dev     # development server
npm run build   # type-check and production build
```

## Responsibility

The web application will implement the proposed operational screens and user workflows. It will present data obtained through the authenticated API, but it will not perform authoritative financial calculations or connect directly to financial source systems.

The initial application should cover:

- Daily Control Room;
- Sources;
- Transactions and Matching;
- Exceptions;
- Safeguarding;
- Sign-off and Evidence; and
- Configuration and Access.

## Design and product sources

- Product behaviour and screen states: [`docs/product/screens/`](../../docs/product/screens/README.md)
- User workflows: [`docs/product/workflows/`](../../docs/product/workflows/README.md)
- Visual design source: [`proofly.pen`](../../proofly.pen)
- API and backend boundaries: [`docs/architecture/overview.md`](../../docs/architecture/overview.md)
