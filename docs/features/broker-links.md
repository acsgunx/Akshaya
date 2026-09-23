# Broker links — status, reconnect, pause/resume, and persistence

Everything about a user's link to a broker account: what the Brokers screen shows for each
one, how re-authentication avoids stacking dead links, how pausing works, and why a deploy no
longer signs anyone out of their broker.

**Scope of this document.** The lifecycle of a `BrokerLink` from the moment "Link account" is
pressed to the row's removal — every state it can be in, every action the UI offers, the API
contract behind each action, the storage layer that makes it survive restarts, and the failure
modes each design choice is there for. It is written to be read before touching link code.

> **Verification status.** Every flow described here has been exercised end to end against the
> real API and the real UI (headless Chrome over CDP): link creation, reconnect-replaces,
> unlink, pause, resume, and session survival across an API restart — including the sealed
> session actually driving a connector after the restart, and the degrade-to-sessionless path
> on a tampered record. The Paper connector was the worked example; nothing in the UI or the
> link layer branches on connector id, so behaviour is identical for every broker whose
> manifest is loaded.

---

## 1. The three things that are not the same thing

The whole feature set rests on a distinction the original screen blurred:

| Concept | Type | What it is |
|---|---|---|
| **Connector** | `ConnectorManifest` | A broker *type*: name, icon, auth fields, capabilities. Comes from `GET /api/connectors`. Eight ship today (fyers, ibkr, longbridge, moomoo, mstock, paper, tiger, zerodha); a new one is a deployment, not a release. |
| **Link** | `BrokerLink` | One *specific* broker account a user authenticated. A connector can be linked many times (two mStock accounts, a live and a paper login). Comes from `GET /api/links`. |
| **Usable session** | `BrokerSession` inside the link | The decrypted tokens the connector needs to call the broker. `IsUsable = IsActive && Session != null`. Nothing else about the link matters to order routing. |

`BrokerLink` fields: `Id`, `TenantId`, `UserId`, `ConnectorId` (opaque — never switched on),
`Nickname`, `Session`, `CreatedAt`, `LastAuthenticatedAt`, `IsActive`.

The port that owns them is `IBrokerLinkStore` in the trading module. Its own comment says why
the rest of this document exists: *"link lifecycle (encryption at rest, key rotation,
per-tenant KMS scoping) belongs to the BrokerLink module… Sessions are handed out decrypted
and must never be logged."*

---

## 2. What the Brokers screen shows

Route: `/connectors`. Component: `connector-catalogue.component.ts/.html` in
`libs/account/feature-connectors`.

Each connector card has three parts now: the manifest header (unchanged), a **linked-accounts
block** with one row per link, and a footer whose label depends on whether any link exists.

### 2.1 The four states a link row can show

| State | Condition | Visual | Actions offered |
|---|---|---|---|
| **Connected** | `isActive && hasSession` | Green dot, nickname, session detail line | Pause, Unlink |
| **Session expired** | `hasSession == false` (regardless of `isActive`) | Amber/warning dot | Sign in again, Unlink |
| **Paused** | `!isActive && hasSession` | Neutral/muted dot, "Paused" label | Resume, Unlink |
| **No account** | connector has zero links | no block rendered | footer: "Link account" |

A fifth pseudo-state — **"Checking linked accounts…"** — renders while `BrokerLinksStore` has
not yet answered its first load. The store's `signature` is undefined until then, and showing
"not linked" before the store has spoken would flash a false empty state on every page load
(DESIGN.md's empty-state rule).

### 2.2 The session detail line

Under the nickname, one line of honest timing information, driven by `ClockService` so it ticks:

- **Inside the warning window** (session dies within 15 min — the same rule as
  `ak-connection-status`): `Session expires in m:ss`, rendered in amber. Mid-trade re-auth is
  the worst time to discover a dead session; the countdown exists so the user sees it coming.
- **Otherwise**: `Session until <time>` — but only when the expiry is a real deadline. Brokers
  that report a sentinel expiry (Paper uses year 9999) render `Signed in <date>` instead of a
  misleading "session until" centuries out.
- **No expiry reported**: `Signed in <lastAuthenticatedAt>`.

`linkStatus()` is computed per row from the clock, so the amber countdown and the flip to
"Session expired" happen live without a refresh.

### 2.3 The actions

- **Sign in again** — navigates to `/connectors/:connectorId/link?replace=<linkId>`. The
  wizard detects `replace`, shows "Reconnect \<broker\>" instead of "Link \<broker\>", and
  prefills the nickname field with the old link's name. See §3 for what happens server-side.
- **Pause** — connected rows only. `PATCH /api/links/{id}` `{isActive:false}`. One click, no
  confirmation: it is trivially reversible and the state flip is itself the feedback. The
  button is disabled while the request is in flight.
- **Resume** — paused rows only. Same PATCH with `true`. Because pausing *keeps* the session,
  resuming is instant while the session is still valid — no re-authentication.
- **Unlink** — every row. Opens the shared `ConfirmDialogService` danger dialog; confirming
  issues `DELETE /api/links/{id}`.
- **Footer** — `Link account` (primary button) when the connector has no links; `Link another
  account` (stroked) when it has one or more.

After every successful mutation the catalogue calls `BrokerLinksStore.reload()`, and the
store's `signature`/`revision` propagation marks dependent screens (watchlist, portfolio)
stale automatically.

---

## 3. Reconnect replaces — the `replacesLinkId` contract

### 3.1 The problem it solves

`POST /api/links` always created a *new* link. Re-authenticating an expired session — a daily
ritual on venue-midnight brokers like mStock or FYERS — would therefore leave a dead duplicate
row on the card forever. Each subsequent reconnect would stack another.

### 3.2 The mechanism

`BeginLinkRequestDto.ReplacesLinkId` (nullable) flows:

```
Catalogue "Sign in again" → /connectors/:id/link?replace=<linkId>
  → wizard reads the query param (withComponentInputBinding)
  → BrokerLinkStore.begin → ApiService.beginLink → POST /api/links { replacesLinkId }
  → PendingLinkAuth.ReplacesLinkId (carried across multi-step flows: redirect, challenge, gateway)
  → on AuthStep.Completed: RemoveReplacedAsync()
```

`RemoveReplacedAsync` (in `BrokerLinkEndpoints.cs`) is deliberately careful:

- Runs **only after** the new authentication completes. A failed login never orphans the old
  link — you cannot "reconnect" your way into having no working account.
- Guards **same tenant + same connector** before touching the named link. A `replacesLinkId`
  pointing at someone else's link, or at a link for a different connector, is ignored — it is
  someone else's link id, not a parameter worth trusting.
- Revokes the old session at the broker best-effort before removing the row. Revoke failure
  does not fail the completed login — the link is removed regardless; a dead token left at a
  broker is a leak, not a reason to strand a working new session.

### 3.3 What the user sees

Old row disappears, new row appears "Connected" with the prefilled nickname. One account, one
row, always.

---

## 4. Pause and resume — the `isActive` setter

### 4.1 Why `isActive` exists at all

`IsActive` was already load-bearing before this feature gave it a UI:

- `BrokerLink.IsUsable` requires it — `BrokerLinkResolver.GetLinkAsync` refuses inactive links,
  so **orders, quotes and history all fail-closed** on a paused link.
- `IBrokerLinkStore.ListActiveAsync()` excludes inactive rows — that one query feeds
  `ReconciliationHostedService` (order-book polling), `ConnectorKeepAliveService` (gateway
  keepalives) and `SubscriptionRegistry` (market streams). Pause flips *all* of that off
  through a single flag — no second mechanism, no per-service switch to forget.

What was missing: nothing could set it. `isActive=false` was representable and honoured but
unreachable.

### 4.2 The endpoint

```
PATCH /api/links/{linkId}
{ "isActive": false }
```

Same tenant-hiding lookup as DELETE — a link that isn't yours is a 404, not a 403 (existence
is itself tenant-private). The endpoint builds an updated `BrokerLink` and goes through
`SaveAsync`; it cannot touch the session, the nickname or any other field.

### 4.3 The semantics that matter

- **Pausing preserves the session.** The link stops being polled, streamed and reconciled, but
  the broker session is untouched — resuming is a flag flip, not a login.
- **Resuming reuses the session** if it is still valid. If the session died while paused, the
  row shows "Session expired" with the normal Sign-in-again path — resume does not resurrect
  dead credentials.
- **Paused + no session** is still honest: the UI prioritises the session state, so a paused
  link without a session shows Sign in again, not Resume.

### 4.4 Frontend

`ApiService.setLinkActive(linkId, isActive)` → `PATCH`. The catalogue disables the row's
action buttons while in flight, reloads the store on success, and lets `errorInterceptor`
toast RFC 7807 failures.

---

## 5. Persistence — sessions survive deploys

### 5.1 The problem it solves

`IBrokerLinkStore` had exactly one implementation: `InMemoryBrokerLinkStore`, a
`ConcurrentDictionary`. Every restart wiped every link — on a venue-midnight broker that is
survivable (the session was dying tonight anyway), but on a long-lived session it meant a
deploy signed the user out of their broker. Re-linking mStock means an OTP; re-linking on
every deploy is untenable.

### 5.2 Where links live now

A `broker_links` table in `IdentityDbContext` — the identity store, which is already the
application's encrypted-secrets store. A link's session is precisely that: a broker credential
the user asked the platform to hold. Keeping it under the same master-key envelope encryption
as the saved-credential vault is the architecture doing what it already does, not a new
secret-management scheme.

```
identity.broker_links
  Id                     PK, varchar(64)
  TenantId               varchar(64)
  UserId                 varchar(64)  → users.Id ON DELETE CASCADE
  ConnectorId            varchar(64)
  Nickname               varchar(120) NULL
  SessionKeyId           varchar(64)  NULL  ┐ all three session
  SessionWrappedDataKey  bytea        NULL  ├ columns are null
  SessionPayload         bytea        NULL  ┘ together, or none
  CreatedAt              timestamptz
  LastAuthenticatedAt    timestamptz  NULL
  IsActive               boolean

  indexes: (TenantId, UserId), (UserId), (IsActive)
```

The cascade is the same orphaned-secret rule as `saved_broker_credentials`: deleting an
account takes its links — and the sealed sessions inside them — with it.

### 5.3 How the session is stored

On `SaveAsync`, `EfBrokerLinkStore` serialises the `BrokerSession` to JSON and seals it with
`ICredentialCipher` — the same `AesGcmCredentialCipher` envelope the vault uses:

- A fresh random 256-bit **data key** encrypts the payload (AES-256-GCM).
- The **master key** encrypts the data key; `SessionKeyId` records which one.
- Rotating the master key later means rewrapping 32-byte data keys, not re-encrypting blobs —
  and a session sealed under a dropped key fails to open *cleanly*, which is the degradation
  below.

No broker token ever rests in the database in the clear. The three session columns are null
together or populated together — a save can deliberately strip a session (re-auth restart,
revocation), and a later read must never see half a secret.

### 5.4 Degradation is deliberate

`UnsealSession` failure — wrong/missing master key, altered record, undecodable payload —
produces a link with `Session = null`, **not an error**:

- The row survives; `hasSession: false` in the DTO.
- The UI offers its existing "Sign in again" path.
- A `LogWarning` records the link id and key id (never the secret).
- One bad secret cannot take down `ListActiveAsync`'s sweep of every other tenant's links.

`ListActiveAsync` additionally post-filters links whose session failed to unseal: the port's
contract is "links the platform can poll", and one it cannot decrypt is not one.

### 5.5 The store implementation

`EfBrokerLinkStore` (`Akshaya.Api/Infrastructure/Persistence/`) is a **singleton that opens a
scope per call** to borrow the scoped `IdentityDbContext`. The port is consumed by hosted
services (`ConnectorKeepAliveService`, `ReconciliationHostedService`, `SubscriptionRegistry`)
that outlive any request scope — holding a scoped context would either fail DI validation or
pin a change tracker for the process lifetime.

`SaveAsync` is an upsert mirroring `EfSavedCredentialStore`: find by id, update the mutable
columns, else insert. `RemoveAsync` is `ExecuteDeleteAsync`. `GetAsync` deliberately does not
filter by tenant — endpoints check tenancy after fetch, matching the in-memory semantics.

### 5.6 Registration

In `AddAkshayaPersistence`, for `Sqlite` and `Postgres` modes:

```csharp
services.Replace(ServiceDescriptor.Singleton<IBrokerLinkStore, EfBrokerLinkStore>());
```

`Replace`, not `TryAdd`: `AddDevelopmentTradingStores` already registered the in-memory store,
and the durable one must win unconditionally. **`PersistenceMode.InMemory` keeps the volatile
store** — that mode is deliberately disposable; its links die with the process like everything
else in it.

### 5.7 Schema upgrade paths, per mode

| Mode | Mechanism |
|---|---|
| **Postgres** | EF migration `20260923135318_PersistBrokerLinks`, applied by `IdentityStoreInitialiser` via `db.Database.MigrateAsync()` at startup. Reviewed DDL like any other change. |
| **Sqlite (fresh DB)** | `EnsureCreated` builds `broker_links` from the model like every other table. |
| **Sqlite (existing file)** | `EnsureCreated` only builds a *fresh* database — a file that already exists is left as it was. The initialiser follows it with idempotent `CREATE TABLE IF NOT EXISTS`/`CREATE INDEX IF NOT EXISTS` DDL, so a file created before this feature gains the table on first boot. Every statement must be a no-op on a database that already has the shape. |
| **InMemory** | N/A — volatile store. |

There is no `dotnet ef database update` step in any mode; the API creates or migrates its own
schema on startup. The migration was generated with
`Persistence__Mode=Postgres dotnet ef migrations add PersistBrokerLinks` — the mode env var
matters because only the Postgres branch nominates the migrations assembly.

---

## 6. API contract

All endpoints live under `/api/links`, tenant-scoped to the signed-in user. A link that isn't
yours is indistinguishable from one that doesn't exist (404).

| Method & route | Body | Returns | Purpose |
|---|---|---|---|
| `GET /api/links` | — | `BrokerLinkDto[]` | The caller's links (the catalogue's data). |
| `POST /api/links` | `BeginLinkRequestDto` | auth step (`completed`/`redirect`/`challenge`/`gateway`) | Begin a link; may complete immediately (paper, credential brokers) or start a multi-step flow. `replacesLinkId` marks a reconnect (§3). |
| `POST /api/links/{id}/continue` | `ContinueLinkRequestDto` | auth step | Supply the next response in a multi-step flow. |
| `PATCH /api/links/{id}` | `UpdateLinkRequestDto { isActive }` | `BrokerLinkDto` | Pause/resume (§4). The only mutable field; the session is never written here. |
| `DELETE /api/links/{id}` | — | 204 | Unlink; revokes the session best-effort first. |

`BrokerLinkDto` — the browser's view of a link. Note what is *absent*: no token, no session
fields beyond the boolean — broker credential values never leave the server.

```json
{
  "id": "…", "connectorId": "mstock", "nickname": "…",
  "isActive": true,
  "hasSession": true,
  "sessionExpiresAt": "2026-09-23T23:59:59+05:30",
  "createdAt": "…", "lastAuthenticatedAt": "…"
}
```

---

## 7. Frontend architecture

| Piece | Location | Role |
|---|---|---|
| `BrokerLinksStore` | `libs/shared/data-access` | Root store; the source of truth for `BrokerLink[]`. `signature` is undefined until the first successful load (the catalogue's "Checking…" gate); `revision` bumps on meaningful changes so dependent screens go stale-on-change. |
| `ConnectorStore` | root store | Connector manifests only — deliberately separate from link state. |
| `ConnectorCatalogueComponent` | `libs/account/feature-connectors` | Renders cards + link rows; `linkStatus()` computed from `ClockService`; owns pause/resume/unlink/sign-in-again actions. |
| `BrokerLinkWizardComponent` | `libs/account/feature-broker-link` | Manifest-driven auth steps; reads `?replace=` and prefills nickname; title flips to "Reconnect \<broker\>". |
| `BrokerLinkStore` (feature) | `libs/account/feature-broker-link` | Wizard-scoped flow state; forwards `replacesLinkId` inside the begin request. |
| `ApiService` | `libs/shared/data-access` | `beginLink`, `setLinkActive`, `unlink`, `listLinks`. |
| `ConfirmDialogService` | shared UI | Danger dialog for unlink. |
| `errorInterceptor` | shared | RFC 7807 → snackbar; rethrows for stores. |

Design rules honoured throughout: no code branches on connector id anywhere in this path —
names, icons, auth fields and capabilities all come from the manifest; real `<button>`s with
visible labels/tooltips and `aria-label`s; no new global CSS (Angular Material + Tailwind in
templates, per `apps/web/src/styles/DESIGN.md`).

---

## 8. Security notes

- **Sessions at rest are sealed** with AES-256-GCM envelope encryption — same cipher, same key
  model, same rotation story as the saved-credential vault. The database holds ciphertext.
- **Sessions never cross the wire.** `BrokerLinkDto` exposes `hasSession` and an expiry
  timestamp, never token material. Credential *values* are likewise never returned to the
  browser (only field metadata).
- **Tenant hiding**: cross-tenant link ids 404 on every verb, including PATCH and DELETE.
- **`replacesLinkId` is guarded** same-tenant + same-connector and only acted on after the new
  login succeeds — it cannot be aimed at someone else's link and cannot destroy a link on a
  failed login.
- **Key rotation**: deployments must keep the `CredentialProtection:Keys` entry a session was
  sealed under for as long as that session matters. Dropping a key does not corrupt records —
  affected links degrade to sessionless and the user re-authenticates. The throwaway dev key
  in `appsettings.Development.json` is for local only; deployments supply
  `CredentialProtection__ActiveKeyId` / `CredentialProtection__Keys__<id>`.

---

## 9. Operational notes

- **Deploys preserve sessions** — that is the point of the feature — *provided the same master
  keys are configured*. A deployment that changes `CredentialProtection` keys without keeping
  the old ids turns every stored link sessionless on first read.
- **SQLite path discipline still applies**: Azure App Service needs
  `Persistence__SqlitePath=/home/data/akshaya-identity.db`; MonsterASP relies on
  `skip-directory-paths: App_Data`. Both were already load-bearing for accounts; they now also
  protect broker links (see CLAUDE.md / deploy READMEs).
- **Single-replica assumption**: `ListActiveAsync` sweeps links across *all* tenants for
  reconciliation/keepalive. Two API replicas against one database would double-poll brokers
  and double-send keepalives. The current single-container deploy targets are fine; horizontal
  scaling needs a leader-election or per-tenant sharding decision first.
- **Failed unseals are loud in logs** (`LogWarning` with link id + key id, never the secret)
  and quiet in the UI — by design, a user sees "Sign in again", not a cryptographic postmortem.

---

## 10. File map

**Backend**

| File | Change |
|---|---|
| `src/Akshaya.Api/Contracts/LinkContracts.cs` | `ReplacesLinkId` on `BeginLinkRequestDto`; `UpdateLinkRequestDto { isActive }` |
| `src/Akshaya.Api/Endpoints/BrokerLinkEndpoints.cs` | `PendingLinkAuth.ReplacesLinkId`; `RemoveReplacedAsync` on completion; `PATCH /{id}` |
| `src/Modules/Identity/Infrastructure/Ef/IdentityDbContext.cs` | `BrokerLinkRow` + `broker_links` table config (indexes, cascade FK) |
| `src/Akshaya.Api/Infrastructure/Persistence/EfBrokerLinkStore.cs` | New durable `IBrokerLinkStore`; seal/unseal via `ICredentialCipher` |
| `src/Akshaya.Api/Infrastructure/Persistence/PersistenceServiceCollectionExtensions.cs` | `Replace` registration for durable modes |
| `src/Akshaya.Api/Infrastructure/Persistence/IdentityStoreInitialiser.cs` | Idempotent additive DDL for existing Sqlite files |
| `src/Akshaya.Api/Infrastructure/Migrations/20260923135318_PersistBrokerLinks*.cs` | Postgres migration |

**Frontend**

| File | Change |
|---|---|
| `apps/web/libs/account/feature-connectors/src/lib/connector-catalogue.component.{ts,html}` | Link rows, status computation, all actions |
| `apps/web/libs/account/feature-broker-link/src/lib/broker-link-wizard.component.{ts,html}` | `?replace=` handling, nickname prefill, "Reconnect" title |
| `apps/web/libs/account/feature-broker-link/src/lib/broker-link.store.ts` | forwards `replacesLinkId` |
| `apps/web/libs/shared/data-access/src/lib/api.service.ts` | `setLinkActive`; `beginLink` gains `replacesLinkId` |

**History**

| Change | Where it landed |
|---|---|
| Linked-account status UX + reconnect-replaces + unlink | PR #66 (merged 2026-09-23) |
| Pause/resume | `906be28` on main |
| Session persistence across deploys | `d6f86a5` on main |

*(The last two predate the repo's PR-everything rule, added in PR #67.)*
