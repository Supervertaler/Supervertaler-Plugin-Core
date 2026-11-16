# A licence check at an address we own

Status: proposal, 16 November 2026. Nothing here is built or decided.

## Why

Lemon Squeezy stops processing payments on 31 March 2027. Its recommended
successor, Stripe Managed Payments, has no licence keys.

Both plugins call Lemon Squeezy's licence server directly
(`SupervertalerLicence.LicenceServer`, `https://api.lemonsqueezy.com/v1/licenses`):
`/activate` when a key is entered, `/validate` at every start, `/deactivate`
when a computer is released. A licensed plugin that cannot confirm its licence
keeps working for 30 days (`OfflineCacheDays`) and then reads as expired.
Lemon Squeezy has not said whether that server answers after 31 March, so this
design assumes it does not.

The address is compiled into the plugin. Changing it needs a release, RWS
approval for Trados, and every customer updating. That is the real constraint,
and it is why the address should become ours once and never change again,
whoever sells the subscriptions.

## What must stay true

- A customer keeps the key they have. Nobody types a new one.
- A computer that is activated stays activated. Nobody activates again.
- An annual licence paid on Lemon Squeezy runs to the date it was paid to. All
  ten annual subscriptions end between April and November 2027, after the
  shutdown.
- The rules in `SupervertalerLicence` do not change: only a reply the plugin
  understood may change licence state, only a reply that says active renews the
  offline window, and an absence of information never locks anyone out.
- No secret is shipped inside a plugin.

## The design

One host, `licence.supervertaler.com`, answering the three calls the plugin
makes today, in the request and reply shape it already reads:

- `POST /v1/licenses/activate` with `license_key`, `instance_name`
- `POST /v1/licenses/validate` with `license_key`, `instance_id`
- `POST /v1/licenses/deactivate` with `license_key`, `instance_id`

Reply: `valid`, `activated`, `deactivated`, `error`, `license_key.status`,
`license_key.expires_at`, `license_key.activation_limit`,
`license_key.activation_usage`, `meta.store_id`, `meta.variant_name`,
`instance.id`. An unknown activation is HTTP 404 with an `error` and no
`license_key`, as Lemon Squeezy answers today.

Keeping the shape means the plugin change is one constant, and
`ParseLemonSqueezyResponse` and its tests stay as they are.

The host is a Cloudflare Worker. Supervertaler already runs one
(`supervertaler-stats`). To check: that the supervertaler.com DNS zone is in
the same Cloudflare account, so the host name can be attached.

### Step 1: pass-through (now)

The Worker forwards each call to Lemon Squeezy unchanged and returns the reply
unchanged. The plugins change `LicenceServer` to the new host and ship.

Nothing else changes for anyone. What it buys:

- Every plugin version from here on is independent of Lemon Squeezy.
- The Worker can count which keys have validated through it. Before anything
  is switched, that says how many customers have updated and how many have not
  (29 active keys today), without guessing.

### Step 2: our own record of keys (before subscriptions are moved)

The Worker stops forwarding and answers from its own table (Cloudflare D1):

| Table | Columns |
|---|---|
| keys | key, activation limit, status, expires at, plan name, payment reference |
| activations | id, key, instance name, created at, last validated at |

Filled by a one-off import from Lemon Squeezy's API, which lists every key
(full key, limit, status, expiry) and every activation (id and instance name).
Importing the activation ids is what keeps every activated computer activated:
the id a plugin holds today is the id the Worker will know.

Checked read-only on 16 November 2026: the API lists 44 activations, each with
a UUID `identifier` and a 64-character `name`; the activation id and
fingerprint in Michael's own licence file match one of them exactly.

No customer names or email addresses are needed in the table. `instance name`
is the plugin's SHA-256 fingerprint of computer and Windows account, not
readable text. The payment reference points to the subscription at whichever
company takes the payments.

### Step 3: the payment company tells the Worker

When a subscription is renewed, cancelled or ends, the payment company calls
a Worker address (a webhook, checked with the company's own signature library)
and the Worker sets the key's status and expiry. A new purchase creates a key.

This is the only part that depends on which company is chosen, and it lives in
the Worker, not in the plugins.

Keys that are not tied to a payment (invoice-paid, complimentary) are created
by a small script with an admin secret held in Cloudflare. That replaces today's
workaround of a 100%-off checkout on a separate product.

## Alternatives considered

- **A licence service behind the Worker instead of our own table** (Keygen is
  the one that accepts existing key strings; free to 100 active licences, then
  $99 a month). The Worker would still be needed to keep the address and the
  reply shape, plus a table mapping today's activation ids to Keygen's. More
  parts, a monthly fee past 100 licences, and another company that can stop.
  Our own table is two tables and three handlers.
- **A payment company with built-in keys, called directly by the plugin**
  (Dodo Payments, Polar, Freemius). This repeats today's mistake: the plugin
  would again depend on one company's address. None of them imports
  activations, so every customer would activate again.
- **Keep calling Lemon Squeezy and hope.** If the server stops, every licence
  expires 30 days later.

## What can go wrong

- **The Worker or its database is down.** The plugin already treats that as
  "not reached": the cached state stands and the 30-day window runs. A new
  key cannot be activated until it is back. The Worker must answer an internal
  error with HTTP 5xx and no `license_key` block, never with a status, so a
  fault can never read as "disabled".
- **Two computers take the last activation at the same moment.** The count
  check and the insert are one database statement, not a read followed by a
  write.
- **Step 1 adds a hop.** If the Worker fails while Lemon Squeezy is fine, the
  plugin is offline for licence purposes. No fallback to Lemon Squeezy's own
  address is proposed: it would be a second path to test, for a few months.
- **Plugins that are never updated** (207 and earlier) keep calling Lemon
  Squeezy. They cannot be fixed from here. They stop 30 days after that server
  goes, or at once if Lemon Squeezy marks keys expired when a subscription is
  moved or the store closes. What Lemon Squeezy does to a key when its
  subscription is migrated is NOT known and must be asked or tested before
  any subscription is moved.
- **Guessing keys.** The endpoints are public, as Lemon Squeezy's are. Keys
  are long random strings; the Worker rate-limits by address.
- **Import drift.** A key sold on Lemon Squeezy after the import is missing
  from the table. Step 2 goes live only together with the move of sales, and
  the import is re-run just before.

## Order of work

1. Worker in pass-through, and `LicenceServer` changed in core. Ship in the
   next Trados release (208) and the next memoQ release.
2. 2 December: Lemon Squeezy publishes its migration tools and its guidance
   for licence-key sellers. Choose the payment company.
3. Build the table, the import and the three handlers. Test against a copy.
4. Watch the Worker's count of keys seen. Michael writes to customers still
   on an old version.
5. Switch the Worker to its own table, connect the payment company's webhook,
   then move the subscriptions.
6. Done before 31 March 2027. Aim for the plugin release by the end of
   January, so customers have two months to update.

## Also affected

- `LicensePanel.cs` in the Trados plugin holds the Lemon Squeezy checkout and
  billing addresses (`PurchaseUrl`, `ManageUrl`). Point both at
  supervertaler.com pages that redirect, in the same release, for the same
  reason.
- The memoQ plugin's licence screen, the website's buy buttons and the Help
  pages that mention Lemon Squeezy.
- The privacy text: licence checks go to supervertaler.com (Cloudflare), not
  Lemon Squeezy.
- `supervertaler-stats` (subscriber tiles), `.dev/ls-subscribers.py`,
  `.dev/ls-invoice-key.py` and the invoice-paid procedure all read Lemon
  Squeezy.
- The store id check (`SupervertalerStoreId`, 307062): the Worker keeps
  sending it, so no change.

## Scale

Today: 29 active keys, about 25 subscribers, one validation per product start.
Designed for tens of thousands of keys: every call is a lookup by key, and the
import is the only step that reads the whole table. Not tested at any size
yet; nothing is built.

## Open questions

1. Our own table, or Keygen behind the Worker? Recommended: our own table.
2. Does step 1 go into 208?
3. What does Lemon Squeezy do to a key when its subscription is migrated or
   the store closes? Only their support can say.
4. Which payment company (after 2 December). Whether a UK sole trader passes
   Stripe's review is still unconfirmed.
