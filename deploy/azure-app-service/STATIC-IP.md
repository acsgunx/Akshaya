# A fixed public IP for Azure App Service

Indian brokers accept API **orders** only from an IP address you have registered with them. SEBI's
retail-algo rules require it. This guide gives your App Service app one public IP that never
changes and that no one else uses, so you can register it once.

**Market data is not the problem.** Quotes, charts and mStock's instrument list download fine from
App Service's ordinary shared addresses. A working chart therefore proves nothing about the IP:
only an order does. If charts fail, the cause is something else. See the connector notes in
[`docs/connectors/mstock.md`](../../docs/connectors/mstock.md).

When an order comes from an unregistered address, Akshaya shows:

> mStock only accepts API calls from the IP addresses registered on your API key, and this app is
> connecting from a different one. …
>
> Broker said: "Primary and Secondary IP Address are not matching with current IP address."

Despite what mStock's error type (`APIKeyException`) suggests, **your API key is fine**, and
generating a new one changes nothing. The fix is the IP.

**What you end up with:** every outbound connection from the app, whether HTTP or the WebSocket
price feed and on any number of instances, leaves from one static IPv4 address that belongs to
you.

**What it costs:** about **$37/month** on top of the plan (a NAT gateway plus a static public IP),
and the plan must be **B1 or higher**. The free tier cannot do this. See [What it costs](#what-it-costs).

**How long it takes:** about 15 minutes, all of it in Cloud Shell.

Worked example throughout: app `akshaya-csg`, resource group `akshaya-rg`, region `centralindia`,
the same names as [`RUNBOOK.md`](RUNBOOK.md). Substitute your own. This works the same for Path A
(code deploy) and Path B (container), because both run on the same App Service plan.

---

## Why the app's own outbound IPs will not do

App Service already has outbound addresses. See for yourself:

```bash
az webapp show -g akshaya-rg -n akshaya-csg --query outboundIpAddresses -o tsv
```

```bash
az webapp show -g akshaya-rg -n akshaya-csg --query possibleOutboundIpAddresses -o tsv
```

You will see several addresses, often ten or more in the second list. None of them works for a broker:

- **There are too many.** The app can leave from any address in the list, and mStock takes only
  two (primary and secondary).
- **They are shared.** Every other customer's app on the same App Service scale unit uses the
  same addresses. Registering them would let strangers' servers pass your broker's IP check.
- **They change.** Moving between some pricing tiers, or deleting and recreating the app, gives it
  new outbound addresses without warning.

The fix is to send the app's outbound traffic through a **NAT gateway** that owns one static
public IP.

---

## What you are building

```
  Akshaya (App Service, B1+)
        │  all outbound traffic ("route all")
        ▼
  subnet app-egress  (10.20.0.0/26, delegated to App Service)
        │  in VNet akshaya-vnet
        ▼
  NAT gateway akshaya-nat
        │
        ▼
  public IP akshaya-egress-ip  ──►  api.mstock.trade, ws.mstock…, any broker
  (Standard, static; in its own
   resource group, delete-locked)
```

Five things, created in this order:

| # | Resource | Why |
|---|---|---|
| 1 | Static public IP, in its own resource group with a delete lock | The address you register with the broker. Kept apart so tearing down the app never releases it |
| 2 | Virtual network with one small subnet, delegated to App Service | App Service can only route traffic through a network it is integrated with |
| 3 | NAT gateway, holding the IP, attached to the subnet | Rewrites every outbound connection from the subnet to the static IP |
| 4 | VNet integration on the app | Puts the app's outbound traffic into the subnet |
| 5 | "Route all" on the app | Without it, only private-range traffic goes through the network, and internet traffic (the broker) still uses the shared addresses |

Nothing inbound changes. Your site URL, TLS, SignalR and GitHub deploys all work exactly as before.

---

## Before you start

- Use **Azure Cloud Shell** (the `>_` icon in the portal toolbar), as in the runbook. It is
  already signed in.
- The app must already exist. If it does not, follow [`RUNBOOK.md`](RUNBOOK.md) first.
- You need rights to create network resources in the subscription. **Owner** or
  **Contributor** on the subscription is enough.
- Have your **mStock Trading API portal** login ready for step 9.

---

## 1. Set the names once

Everything below uses these variables. If Cloud Shell times out, paste this block again before you
continue.

```bash
APP=akshaya-csg
RG=akshaya-rg
IP_RG=akshaya-ip-rg          # separate group for the IP, so deleting $RG never releases it
VNET=akshaya-vnet
SUBNET=app-egress
NAT=akshaya-nat
PIP=akshaya-egress-ip

# The app's region, as a name the CLI accepts ("Central India" -> "centralindia").
LOCATION=$(az webapp show -g "$RG" -n "$APP" --query location -o tsv | tr -d ' ' | tr '[:upper:]' '[:lower:]')
PLAN_ID=$(az webapp show -g "$RG" -n "$APP" --query appServicePlanId -o tsv)
echo "app=$APP region=$LOCATION"
```

**What it proves:** the app exists and you are in the right subscription. If `LOCATION` comes back
empty, the app or resource group name is wrong. Check with `az webapp list -o table`.

Every network resource must be in **the same region as the app**. The variables guarantee that.

---

## 2. Move off the Free tier

VNet integration is not available on F1 (Free) or D1 (Shared). Check the plan:

```bash
az appservice plan show --ids "$PLAN_ID" --query sku.name -o tsv
```

If it prints `F1` or `D1`, move to B1 (about $13/month). This takes effect in place, with no
redeploy and no data loss, because `/home` keeps the database:

```bash
az appservice plan update --ids "$PLAN_ID" --sku B1 -o none
```

While you are on B1, turn on the settings Free could not have. This is the same command the
README gives:

```bash
az webapp config set -g "$RG" -n "$APP" --always-on true \
  --generic-configurations '{"healthCheckPath": "/health/ready"}' -o none
```

Always-on also matters for trading: on Free the app idles out and drops the live-price socket.

---

## 3. Reserve the public IP

```bash
az group create -n "$IP_RG" -l "$LOCATION" -o none

az network public-ip create -g "$IP_RG" -n "$PIP" -l "$LOCATION" \
  --sku Standard --allocation-method Static --version IPv4 -o none

az lock create --name keep-egress-ip --lock-type CanNotDelete -g "$IP_RG" -o none

EGRESS_IP=$(az network public-ip show -g "$IP_RG" -n "$PIP" --query ipAddress -o tsv)
echo "Your fixed outbound IP: $EGRESS_IP"
```

**Write that address down.** It is what you register with the broker in step 9.

Why the extra care:

- **Standard SKU, static allocation.** The NAT gateway requires Standard, and static means the
  address is fixed from the moment it is created until the resource is deleted.
- **Its own resource group.** The README's teardown is `az group delete --name akshaya-rg`. If the
  IP lived in `akshaya-rg`, one rebuild would release it forever, and you would have to
  re-register a new address with every broker. Some brokers limit how often you may change it.
- **A delete lock.** A `CanNotDelete` lock on the group stops the IP from being deleted by
  accident, from the portal or the CLI, until someone removes the lock on purpose. It does not
  stop the IP from being attached to the NAT gateway.

---

## 4. Create the network and the integration subnet

```bash
az network vnet create -g "$RG" -n "$VNET" -l "$LOCATION" \
  --address-prefixes 10.20.0.0/24 \
  --subnet-name "$SUBNET" --subnet-prefixes 10.20.0.0/26 -o none

az network vnet subnet update -g "$RG" --vnet-name "$VNET" -n "$SUBNET" \
  --delegations Microsoft.Web/serverFarms -o none
```

- **The address range does not matter** as long as it is private and does not overlap a network
  you might peer with later. `10.20.0.0/24` is arbitrary.
- **`/26` (64 addresses) is deliberate.** App Service takes one address per instance, and more
  during scale operations and platform upgrades. `/28` is the documented minimum, but it runs out
  if you ever scale out. The subnet costs nothing whatever its size.
- **The delegation** hands the subnet to App Service. A delegated subnet can hold nothing else,
  so do not put VMs or other services in it.

The virtual network itself is free.

---

## 5. Create the NAT gateway and attach it

```bash
PIP_ID=$(az network public-ip show -g "$IP_RG" -n "$PIP" --query id -o tsv)

az network nat gateway create -g "$RG" -n "$NAT" -l "$LOCATION" \
  --public-ip-addresses "$PIP_ID" --idle-timeout 10 -o none

az network vnet subnet update -g "$RG" --vnet-name "$VNET" -n "$SUBNET" \
  --nat-gateway "$NAT" -o none
```

Check it is attached:

```bash
az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SUBNET" \
  --query "{natGateway: natGateway.id, delegation: delegations[0].serviceName}" -o table
```

Both columns must be filled in.

- The IP is passed **by id**, because it lives in a different resource group from the gateway.
  That is allowed as long as both are in the same subscription and region.
- **`--idle-timeout 10`** (minutes). The NAT gateway forgets a connection that has been silent
  for this long, and the default is 4 minutes. The price socket sends keep-alives every 30 seconds
  so it would survive the default anyway. The extra headroom costs nothing and covers pooled HTTP
  connections to the broker.

---

## 6. Connect the app to the subnet

```bash
az webapp vnet-integration add -g "$RG" -n "$APP" --vnet "$VNET" --subnet "$SUBNET"
```

```bash
az webapp vnet-integration list -g "$RG" -n "$APP" -o table
```

The list should show `akshaya-vnet` / `app-egress`.

Integration is per **plan**, not just per app: other apps on the same plan can join the same
subnet. That is harmless, and they would share the fixed IP.

---

## 7. Send all outbound traffic through the network

This is the step most often missed. Without it, the app only uses the network for private
addresses (10.x, 172.16–31.x, 192.168.x), and calls to the broker still leave from the shared
addresses. Everything looks set up and nothing changes.

```bash
az webapp config set -g "$RG" -n "$APP" --vnet-route-all-enabled true -o none
az webapp config show -g "$RG" -n "$APP" --query vnetRouteAllEnabled
```

The second command must print `true`. Then restart, so that pooled connections opened before the
change are dropped:

```bash
az webapp restart -g "$RG" -n "$APP"
```

---

## 8. Prove the app leaves from that IP

Don't skip this. It takes a minute and is the only way to know the app, not just Azure's
configuration, is using the new address.

Open a shell **inside the app's container**:

```bash
az webapp ssh -g "$RG" -n "$APP"
```

If that does not connect from Cloud Shell, open
`https://akshaya-csg.scm.azurewebsites.net/webssh/host` in a browser instead (sign in with your
Azure account). Use **SSH**, not the Kudu "Bash" console: on Linux, Kudu runs in a separate
container, and what it reports is not what the app uses.

Inside, ask a public "what is my IP" service:

```bash
curl -s https://api.ipify.org; echo
```

If `curl` is missing, use `wget -qO- https://api.ipify.org; echo` instead.

**It must print exactly the `EGRESS_IP` from step 3.** If it prints anything else, see
[When something goes wrong](#when-something-goes-wrong) before touching the broker. Type `exit`
to leave the shell.

---

## 9. Register the IP with the broker

### mStock

1. Sign in to the **mStock Trading API portal**, the site where you created the API key that
   Akshaya is linked with.
2. Open that API key's settings and find the **Primary IP** and **Secondary IP** fields.
3. Put the address from step 3 in **Primary IP** and save.
4. **Secondary IP** is optional. A useful choice is the public IP of the computer you develop on
   (`curl -s https://api.ipify.org` on that computer), so a local copy of Akshaya works against
   the same key. Home connections usually change their IP from time to time, so expect to
   update it.

Register it on the **same API key** that the broker link in Akshaya uses. If you have more than
one key, check which one it is. Registering the right IP on the wrong key looks exactly like not
registering it.

The mStock portal changes its layout from time to time, and brokers may limit how often the
registered IP can change. The NAT gateway's IP never changes, so you should only have to do this
once.

### Other brokers

The same address works for any broker. Where to register it:

| Broker | Where |
|---|---|
| mStock | Trading API portal → your API key → Primary / Secondary IP |
| Zerodha (Kite Connect) | Kite Connect developer console → your app's IP settings |
| FYERS | FYERS API dashboard → your app's IP settings |

---

## 10. Check it end to end in Akshaya

Only an order exercises the IP check, so an order is the only real test. Charts and quotes worked
before this change and prove nothing.

A test that cannot fill: during market hours, place a **limit buy for 1 share well below the
current price**, for example 20% under, on a liquid stock.

1. **Success looks like** the order being accepted and showing as open in Orders. Cancel it
   straight away.
2. **Failure looks like** the "mStock only accepts API calls from the IP addresses registered…"
   message. Go back to step 8: the app is not leaving from the registered address.

This is a real order at a real broker, so check the price before you confirm it. A limit well
below the market will not fill, but a typo in the price would.

---

## What it costs

Approximate list prices in USD. Indian regions are close, but check the
[pricing calculator](https://azure.microsoft.com/pricing/calculator/) for your region.

| Item | Price | Per month |
|---|---|---|
| NAT gateway | ~$0.045/hour | ~$33 |
| Data through the NAT gateway | ~$0.045/GB | cents: broker API traffic is small, and the instrument list is tens of MB twice a day |
| Standard static public IP | ~$0.005/hour | ~$3.65 |
| Virtual network, subnet, VNet integration | free | $0 |
| App Service plan (B1, required) | | ~$13 |
| **Total** | | **~$50**, compared with $0 on F1 |

The NAT gateway is the expensive part and is billed by the hour whether or not traffic flows.

**Cheaper ways exist, and each has a cost of its own:**

- **Run Akshaya on a small VM with a static IP** instead of App Service (roughly $8 for the VM plus
  $3.65 for the IP). It is cheaper, but you patch, secure and back up the server yourself, and
  none of this repository's App Service deploy path applies.
- **Route the app through a proxy on a small VM.** It is cheaper still, but you run an internet
  proxy that must be locked down, and the app has to be configured to use it for both HTTP and
  WebSocket traffic. This is not covered here.
- **Run Akshaya on your own computer** and register your home IP. It is free, but your home IP
  changes, and the app stops when the computer does.

For something you trade from, the NAT gateway is the option with nothing to maintain.

---

## When something goes wrong

| Symptom | Cause and fix |
|---|---|
| `vnet-integration add` fails mentioning the SKU or tier | The plan is still F1/D1. Do step 2 |
| `vnet-integration add` fails mentioning the subnet or delegation | The subnet has other resources in it, or is not delegated. Re-run the delegation in step 4; the subnet must be empty apart from App Service |
| An error mentioning a **location** or **region** | The VNet, NAT gateway or IP is in a different region from the app. Re-run step 1 and check `$LOCATION`; all resources must match the app |
| Step 8 prints one of the app's old `outboundIpAddresses` | Route all is off. `az webapp config show … --query vnetRouteAllEnabled` must be `true` (step 7), then restart |
| Step 8 prints an address that is neither the old ones nor yours | You are in the Kudu Bash console, not SSH. Use `az webapp ssh` or `/webssh/host` |
| Step 8 still prints the old address after route all is `true` | The NAT gateway is not attached to the subnet. Check with the `subnet show` command in step 5; `natGateway` must be filled in |
| Step 8 is right, but the broker still says the IP does not match | The IP is registered on a different API key, has a typo, or was not saved. mStock may also take a few minutes to apply a change |
| Charts or quotes fail, but not with the IP message | Not an IP problem: mStock does not check the IP for market data. Read the message itself; `az webapp log tail -g "$RG" -n "$APP"` shows the broker's reply |
| `az group delete` on `akshaya-ip-rg` fails with `ScopeLocked` | Working as intended: that is the delete lock from step 3. See [Undoing it](#undoing-it) |
| The live price connects, then goes quiet after a few minutes of no trading | The NAT idle timeout was left at the 4-minute default and keep-alives are not getting through. Set `--idle-timeout 10` with `az network nat gateway update -g "$RG" -n "$NAT" --idle-timeout 10` |

---

## Undoing it

To stop paying for the NAT gateway while **keeping the IP** (so you do not have to re-register it
later):

```bash
az webapp config set -g "$RG" -n "$APP" --vnet-route-all-enabled false -o none
az webapp vnet-integration remove -g "$RG" -n "$APP"
az network vnet delete -g "$RG" -n "$VNET"
az network nat gateway delete -g "$RG" -n "$NAT"
```

The app goes back to its shared outbound addresses, so broker calls start failing the IP check
again. The public IP stays reserved at about $3.65/month. To bring everything back, run steps 4–8;
step 9 is not needed, because the address has not changed.

To **release the IP for good**, which you cannot undo because the same address will not come back:

```bash
az lock delete --name keep-egress-ip -g "$IP_RG"
az group delete --name "$IP_RG" --yes
```

The README's `az group delete --name akshaya-rg` removes the app, the VNet and the NAT gateway,
but leaves the IP alone, because the IP is in a different resource group.

---

## Quick reference

```bash
# The fixed IP
az network public-ip show -g akshaya-ip-rg -n akshaya-egress-ip --query ipAddress -o tsv

# Is the app integrated, and is everything routed through it?
az webapp vnet-integration list -g akshaya-rg -n akshaya-csg -o table
az webapp config show -g akshaya-rg -n akshaya-csg --query vnetRouteAllEnabled

# Is the NAT gateway attached to the subnet?
az network vnet subnet show -g akshaya-rg --vnet-name akshaya-vnet -n app-egress --query natGateway.id

# What the app actually leaves from (run inside `az webapp ssh`)
curl -s https://api.ipify.org; echo
```
