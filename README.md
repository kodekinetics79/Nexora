# RFQ Automation Frontend

React frontend for an ERP-style RFQ automation platform. The app gives users a role-aware workspace for leads, RFQs, quotations, orders, shipments, suppliers, customers, inventory, setup data, and security administration.

## Project Overview

This frontend is built with Vite, React 19, TypeScript, Material UI, React Router, TanStack Query, Axios, i18next, Recharts, and XLSX utilities. It talks to the ASP.NET Core backend through REST endpoints under `/api/*`.

The application is designed as an authenticated business dashboard rather than a public landing page. After login, users see navigation and pages filtered by their role permissions.

## Main Capabilities

- Authentication with JWT storage in `localStorage`
- Role and module based route protection through `PermissionGuard`
- Business unit selection and scoped API calls
- Lead management: all leads, outstanding leads, assigned leads, manual upload, folder upload, lead detail review, accept/reject flows
- Procurement RFQ management: all RFQs, drafts, outstanding RFQs, process RFQ, RFQ detail view
- Sales workflow: quotations, quote creation/editing, quote PDF/email actions, orders, order invoices, shipments, shipment invoices
- Supplier management: supplier records, supplier contacts, quoted items, supplier purchase history
- Customer management: customer records and customer details
- Inventory management: products, product details, product categories, product sub-categories, product matching, purchase history
- Setup data: business units, currencies, warehouses, UOM, locations, quote format, price structure
- Security: users, roles, permissions, module-level create/edit/delete access
- Multi-language resources through i18next

## Architecture

```text
src/
  api/
    axiosInstance.ts          Shared Axios client with auth and 401 handling
    services/                 Domain-specific API wrappers
  components/
    common/                   Shared guards, search, branding, upload/export controls
    layout/                   Navbar, sidebar, and main authenticated layout
  context/
    AuthContext.tsx           Token, user profile, business units, permission helper
    ThemeContext.tsx          Theme state
  pages/
    Dashboard/
    Leads/
    Procurement/RFQs/
    Sales/
    Suppliers/
    Customers/
    Inventory/
    Setup/
    Security/
    Login/
  App.tsx                     Application route map
  main.tsx                    Providers and app bootstrap
```

## Backend Pairing

The frontend expects the backend API to run at the URL configured by:

```env
VITE_API_BASE_URL=http://localhost:5192
```

The local backend launch profile uses `http://localhost:5192` by default. Update `.env` when using another backend host.

## Requirements

- Node.js compatible with the checked-in dependencies
- npm
- Running ERP RFQ Automation backend API

## Setup

```powershell
npm install
npm run dev
```

The Vite dev server is configured to use port `3000`.

```text
http://localhost:3000
```

## Build

```powershell
npm run build
```

The production build is emitted to `dist/`.

## Useful Scripts

```powershell
npm run dev       # Start local Vite development server
npm run build     # Type-check and build the app
npm run preview   # Preview the production build
npm run lint      # Run ESLint if the local lint setup is complete
```

On Windows PowerShell, if `npm` is blocked by script execution policy, use `npm.cmd` instead:

```powershell
npm.cmd run build
```

## Authentication and Permissions

`src/api/axiosInstance.ts` attaches the JWT from `localStorage.token` as a bearer token on every request. A `401` response clears stored auth data and redirects to `/login`.

`src/context/AuthContext.tsx` stores user data, business units, and permissions. `hasPermission(moduleName, action)` checks whether the current user can view, create, edit, or delete a module. `Super Admin` is treated as full access.

Routes in `src/App.tsx` are wrapped with `PermissionGuard`, so pages are hidden or redirected when the logged-in user does not have the required module permission.

## Key Routes

- `/login`
- `/dashboard`
- `/procurement/leads/all`
- `/procurement/leads/outstanding`
- `/procurement/leads/assigned`
- `/procurement/leads/manual-upload`
- `/procurement/leads/folder-upload`
- `/procurement/rfqs/all`
- `/procurement/rfqs/draft`
- `/procurement/rfqs/outstanding`
- `/sales/quotes`
- `/sales/orders`
- `/sales/shipments`
- `/suppliers`
- `/customers`
- `/inventory/products`
- `/inventory/categories`
- `/security/users`
- `/security/roles`
- `/setup/currency`
- `/setup/warehouse`
- `/setup/uom`
- `/setup/locations`
- `/setup/quote-format`
- `/setup/business-unit`

## Service Layer

Each domain has a small service wrapper in `src/api/services/`. These wrappers keep pages away from raw endpoint strings and centralize response types, form uploads, blob downloads, and query parameters.

Important service files include:

- `leadService.ts`
- `rfqService.ts`
- `quoteService.ts`
- `orderService.ts`
- `shipmentService.ts`
- `productService.ts`
- `supplierService.ts`
- `customerService.ts`
- `rolePermissionService.ts`
- `userService.ts`
- setup services for currency, warehouse, UOM, location, business units, modules, and quote configuration

## Notes for Contributors

- Keep API calls inside `src/api/services/`.
- Wrap protected page routes with `PermissionGuard`.
- Keep module names aligned with backend `Module` and `RolePermission` data.
- For file uploads, use `FormData` and let Axios send `multipart/form-data`.
- Keep `.env` values environment-specific. Do not commit production API URLs or secrets.

## Verification

Current local check:

```powershell
npm.cmd run build
```

Result: passed. Vite reported a large JavaScript chunk warning, so future optimization should consider route-level lazy loading or build output chunking.
