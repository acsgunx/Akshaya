# ADR 0008 — Operator-run gateways, host keepalive, and calendars for the new venues

- **Status:** Accepted
- **Date:** 2026-09-11
- **Amends:** [ADR 0006](0006-three-hosting-models.md) (gateway hosting is now built, for operator-run daemons)

## Context

Adding moomoo, Longbridge, IBKR and Tiger was meant to be a connector-only change. Three gaps outside
`src/connectors/` made that impossible, and each one would have shipped a connector that loads,
validates, and then cannot do its job:

1. **Gateway hosting could not produce a working connector.** `ConnectorFactory.CreateAsync` asks the
   `GatewaySupervisor` for a healthy gateway before activating a `Gateway`-hosted connector, and the
   only `IGatewayRuntime` was `NullGatewayRuntime`, which fails every request by design. moomoo
   (OpenD) and IBKR (Client Portal Gateway) could therefore never be bound to a session. Even with a
   runtime, nothing carried the gateway's address to the connector: the factory discarded the
   endpoint the supervisor returned.
2. **Nothing called `IConnectorAuth.KeepAliveAsync`.** The contract says "the host calls this on the
   interval declared in the manifest". No host code did. The Client Portal Gateway signs a brokerage
   session out after a few idle minutes, so an IBKR link would have died between a trader's orders.
3. **The risk gate treats a venue with no calendar as closed.** The dev calendar set covered NSE,
   BSE, SGX, Nasdaq and NYSE. HKEX was missing, so every Hong Kong order from three of the four new
   brokers would be refused. So were the listing venues of most US ETFs (NYSE Arca, Cboe BZX, NYSE
   American) — SPY is an NYSE Arca listing.

A fourth gap was found while wiring the first gateway connector: **the link wizard's gateway step
could never succeed.** Its "I've started it — check again" button deliberately sends `continue` with
an empty response, and `ContinueLinkRequestDtoValidator` rejected an empty response with a 400. No
connector had ever returned `AuthStep.GatewayRequired`, so nobody had pressed it.

The FYERS and Zerodha connectors met the same kind of gap (MCX has no calendar) and answered it by
not claiming the venue. That is not available here: these brokers have no other market to fall back
to.

## Decision

### Gateways are run by the operator and probed by the host

`ConfiguredGatewayRuntime` implements `IGatewayRuntime` for daemons the operator runs — a developer's
own OpenD or Client Portal Gateway, or a sidecar next to the API. It starts nothing. It resolves each
gateway's address from `Connectors:Gateways` and proves something accepts a TCP connection there.

```json
"Connectors": {
  "Gateways": {
    "moomoo-opend": { "Host": "127.0.0.1", "Port": 11111 },
    "ibkr-cpgw":    { "Host": "cpgw.internal", "Port": 5000,
                      "Credentials": { "U1234567": { "Host": "cpgw-2.internal" } } }
  }
}
```

Resolution is most-specific-first: a per-credential override (only for per-credential gateways), the
gateway's configured address, then **loopback on the manifest's declared port**. That last default is
what makes a local OpenD work with no configuration; in a deployment it fails the probe with a message
naming the address it tried.

The resolved address reaches the connector through a new, additive property,
`ConnectorActivationContext.Gateway` (`GatewayAddress(Host, Port)`), set by `ConnectorFactory`:

- For a **bound session**, it is the endpoint the supervisor has just probed. The connector never
  works an address out for itself, so the supervisor and the connector cannot be talking about
  different daemons.
- For the **unauthenticated handshake instance**, it is the gateway's default address, unprobed. The
  handshake runs before an account id exists, so there is no credential to supervise. A gateway
  connector's auth facet reports `GatewayUnavailable` itself when nothing answers.

The probe is a TCP connect and nothing more. Whether the daemon is logged in, unlocked or pointed at
the right account is the connector's business: it knows the protocol and reports it through its auth
and health facets. A host that parsed one vendor's status page would be the first broker special case
in it.

### The host keeps sessions alive

`ConnectorKeepAliveService` (a hosted service in the API) sweeps active links every 15 seconds and
calls `Auth.KeepAliveAsync` for each session whose manifest declares `keepAliveInterval`, once per
interval. It reads only the manifest. The last-sent time is stamped before the call, so a dead gateway
is retried on its interval rather than hammered on every sweep.

### An empty continue response is valid

`ContinueLinkRequestDtoValidator` now requires the response to be present, not non-empty. Connectors
that need a real answer already refuse a blank one with `ChallengeFailed` (mStock and Zerodha both
do), and a gateway connector treats an empty response as "check the gateway again".

### Calendars for the venues the new connectors claim

The dev calendar set gains **XHKG** (09:30–12:00 and 13:00–16:00 Hong Kong time, with the lunch break
modelled as a `Break` session) and **ARCX**, **BATS** and **XASE** on US core hours. They are the same
approximations as the existing rows: no holidays, no pre- or post-market.

### Per-connector settings are bound from configuration

`ConnectorHostOptions.Settings` reached every connector through its activation context but was never
populated: nothing bound it, so a connector's settings — a request timeout, a subscription quota, a
regional endpoint — could only be changed in code. The API now binds
`Connectors:Settings:<connectorId>:<name>` into it, as flat strings, which is the shape the activation
context already promised. Credentials never go here; they arrive through the link flow.

## Why

- **An operator-run runtime first, not a container-per-credential one.** It is how these daemons are
  run today. Both vendors expect a person to install and sign in to the daemon — OpenD logs in with the
  user's own moomoo account, and the Client Portal Gateway needs a browser login with two-factor
  approval — so a runtime that launches them unattended still needs a human in the loop. The seam is
  unchanged; a Docker or Kubernetes runtime can replace one DI registration later.
- **A typed property rather than magic keys in `Settings`.** `ConnectorActivationContext` is a record
  precisely so it can grow without breaking third-party connectors, and a typed address cannot be
  misspelled by one connector and not another.
- **Keepalive in the host, not in each connector.** Connectors are request-scoped; nothing inside one
  lives long enough to run a timer. The contract already assigned the job to the host.
- **Calendar rows, not a calendar service.** This is still dev reference data, and it is the smallest
  change that stops the risk gate refusing every order on these venues.

## Consequences and limits

- **Multi-user deployments with per-credential gateways link against the default address.** The
  handshake cannot see an account id it has not obtained yet. One user per default gateway works;
  assigning gateways by *platform user* needs a user id the `IGatewayRuntime` seam does not carry
  today, and that is a further contract change.
- **The per-user cost in ADR 0006 is still real** — it has moved from the platform's bill to the
  operator's machine.
- **`ConnectorActivationContext` grew a property.** It is additive: existing connectors compile and
  behave unchanged, and `Gateway` is null for every non-gateway connector.
- **Keepalives run sequentially.** One hung gateway delays the rest of that sweep by up to the probe
  and request timeouts. Acceptable at the number of gateway sessions a deployment realistically has.
- **The new calendars are approximations**, like the rows they sit next to. No holidays means the risk
  gate will allow an order on a Hong Kong or US market holiday, and the venue will reject it. Seeding
  real calendars from reference data is still open.
- `NullGatewayRuntime` is kept. It is still the right runtime for a host that must refuse gateway
  brokers outright.
