# RIoT2.Matter Controller UI

Vue 3 + Vite + Vuetify UI for the `RIoT2.Matter.Controller` backend. It lets an operator discover
and commission Matter devices, inspect live device state, invoke common controls, organize devices
into rooms and view the fabric topology.

This project is UI-only. Matter protocol work lives in `Controller/`; the UI consumes backend DTOs
and endpoints through its backend client abstraction.

## Stack

- Vue 3
- Vite
- Vuetify
- Pinia
- Vue Router
- TypeScript
- Vitest + Vue Test Utils

## Project structure

| Path | Contents |
| --- | --- |
| `src/main.ts` | App bootstrap |
| `src/router/` | Home, add-device, detail, rooms and fabric routes |
| `src/services/backend/` | HTTP and in-memory backend clients, DTOs and cluster constants |
| `src/services/organization/` | UI-local room/layout persistence |
| `src/stores/` | Pinia stores for commissioning, devices, selected device and rooms |
| `src/presentation/views/` | Page-level views |
| `src/presentation/components/` | Reusable UI components |
| `src/styles/` | Vuetify/global styles |

## Configuration

Vite environment variables:

| Variable | Meaning |
| --- | --- |
| `VITE_BACKEND_MODE` | `http` for the real backend or `memory` for the in-memory fake |
| `VITE_BACKEND_URL` | Base URL for the controller backend when using HTTP mode |

## Build and test

From `C:\Src\RIoT2\RIoT2.Matter\Controller\Ui`:

```powershell
npm ci --no-audit --no-fund
npm run build
npm test
```

`npm run build` runs TypeScript/Vue type checking and the production Vite build. `npm test` runs
Vitest with jsdom and in-memory backends.

Other useful scripts:

```powershell
npm run dev
npm run preview
npm run lint
npm run format
```

## Related

- Repository README: [../../README.md](../../README.md)
- Controller/backend notes: [../../docs/controller.md](../../docs/controller.md)
- UI roadmap: [ROADMAP.md](ROADMAP.md)
- Agent instructions: [AGENTS.md](AGENTS.md)
