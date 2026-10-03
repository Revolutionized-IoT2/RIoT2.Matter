Applies to: `Controller/` and `Controller/Ui/`.

# Controller backend and UI

`Controller/` is a standalone Matter controller / commissioner / administrator backend. It discovers
devices, commissions them onto a fabric, persists controller credentials, opens operational CASE
connections and exposes HTTP endpoints used by the Vue UI.

`Controller/Ui/` is a Vue 3 + Vite + Vuetify UI. It must remain a presentation layer over backend
DTOs and endpoints.

## Backend configuration

The backend binds `MatterController` options:

| Setting | Meaning |
| --- | --- |
| `CredentialProtectionSecret` | Stable out-of-band secret used to encrypt credential store material |
| `CredentialStorePath` | Persistent encrypted fabric identity / credential directory or file path |
| `CommissionedNodeRegistryPath` | JSON registry of commissioned nodes |
| `FabricId` | Fabric id used when bootstrapping a new fabric |
| `AdminVendorId` | Administrator vendor id for NOC installation |
| `FabricLabel` | Human-readable fabric label |
| `TrustedPaaCertificates` | Explicit DER PAA trust anchors |
| `TrustedCertificationDeclarationSigners` | Explicit DER CD signer trust anchors |
| `OperationalSessionIdleTimeout` | Idle timeout for cached operational sessions |
| `DiscoverOperationalNodesOnStart` | Whether the background host discovers operational nodes at startup |

Do not copy concrete local secret values from `appsettings.json`. Supply deployment secrets through a
secret provider or environment variables.

## Backend routes

`Controller/Program.cs` maps these primary route groups:

| Route group | Purpose |
| --- | --- |
| `GET /api/discovery/commissionable` | Discover commissionable nodes for a bounded time window |
| `GET /api/discovery/operational` | Discover operational nodes |
| `POST /api/commissioning/commission` | Commission a discovered node with setup passcode and optional network credentials |
| `GET /api/nodes` | List commissioned nodes |
| `GET /api/nodes/{fabricId}/{nodeId}` | Get one commissioned node |
| `DELETE /api/nodes/{fabricId}/{nodeId}` | Remove local node record and disconnect |
| `GET /api/nodes/{fabricId}/{nodeId}/certificate` | Inspect stored NOC |
| `POST /api/nodes/{nodeId}/control/connect` | Establish or refresh an operational CASE connection |
| `POST /api/nodes/{nodeId}/control/disconnect` | Release a cached connection |
| `POST /api/nodes/{nodeId}/control/onoff/*` | On, off and toggle |
| `GET/POST /api/nodes/{nodeId}/control/level` | Read or set level |
| `GET/POST /api/nodes/{nodeId}/control/color/*` | Read or set color controls |
| `GET /api/nodes/{nodeId}/control/subscribe` | Subscribe to state reports |
| `GET /api/nodes/{nodeId}/endpoints` | Inspect endpoint metadata |

`Controller/UiCompat/UiCompatEndpoints.cs` also maps compatibility routes used by the UI, including
`/api/devices`, `/api/commissioning`, `/api/interaction/*`, `/api/fabric/*` and `/api/events`.

## UI structure

| Path | Purpose |
| --- | --- |
| `src/services/backend/` | HTTP and in-memory backend clients plus DTO types |
| `src/services/organization/` | UI-local rooms and layout persistence |
| `src/stores/` | Pinia stores for devices, commissioning, selected device and rooms |
| `src/presentation/views/` | Add-device, home, device detail, rooms and fabric views |
| `src/presentation/components/` | App bar, onboarding form, controls, lists, graph and dialogs |
| `src/router/` | Routes for home, add device, device detail, rooms and fabric |

The UI supports `VITE_BACKEND_MODE=memory|http` and `VITE_BACKEND_URL` through Vite environment
variables.

## Roadmaps

The backend and UI roadmaps remain as subproject-specific planning/status documents:

- [Controller/ROADMAP.md](../Controller/ROADMAP.md)
- [Controller/Ui/ROADMAP.md](../Controller/Ui/ROADMAP.md)

They are retained because their checked items broadly match the current backend and UI file layout.
The canonical repository-wide status summary is [status.md](status.md).
