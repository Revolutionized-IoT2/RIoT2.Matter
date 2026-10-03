# AGENTS.md — RIoT2.Matter.Controller.UI

Applies to: `Controller/Ui/`. Start with the repository root [AGENTS.md](../../AGENTS.md) and the
controller backend [AGENTS.md](../AGENTS.md); this file adds only UI guidance.

## What this is

The Vue 3 + Vite + Vuetify user interface for the Matter controller backend. It is a presentation
layer over backend DTOs and endpoints for commissioning, device control, room organization and fabric
visualization.

## Commands

Run from `C:\Src\RIoT2\RIoT2.Matter\Controller\Ui`:

```powershell
npm ci --no-audit --no-fund
npm run build
npm test
```

Optional local development:

```powershell
npm run dev
npm run lint
```

## Layout

| Path | Contents |
|---|---|
| `src/services/backend/` | HTTP/in-memory clients, backend DTOs and cluster constants |
| `src/services/organization/` | UI-local rooms and layout storage |
| `src/stores/` | Pinia state for commissioning, devices and rooms |
| `src/presentation/views/` | Route views |
| `src/presentation/components/` | Reusable Vuetify components |
| `src/router/` | Client-side routes |

## Contracts consumed here

The UI consumes the controller backend's `/api` routes from `Controller/Program.cs` and
`Controller/UiCompat/UiCompatEndpoints.cs`. These are repository-local APIs, not the platform
orchestrator routes in the hub `http-api.md`.

## Rules

- Do not add Matter protocol logic, TLV parsing, Secure Channel, discovery or credential code here.
- Use backend clients and DTOs from `src/services/backend/`; keep real and in-memory clients
  swappable.
- UI-owned concepts such as rooms, device-to-room assignments and display preferences stay in the
  UI-local organization store.
- Displayed device state must derive from backend reads/subscriptions/events, not optimistic-only UI
  state.
- Keep code TypeScript-first and follow existing Vue/Pinia/Vuetify patterns.

## Pitfalls

- `npm run build` already runs `vue-tsc --build --force`; type errors fail the build before Vite
  emits production assets.
- The UI has `VITE_BACKEND_MODE=memory|http`. Tests generally use the in-memory backend.
- Backend compatibility routes under `/api` are intentionally present; coordinate route changes with
  `Controller/UiCompat`.

## Related work

- [ROADMAP.md](ROADMAP.md)
- [../../docs/controller.md](../../docs/controller.md)
- [../../docs/status.md](../../docs/status.md)
