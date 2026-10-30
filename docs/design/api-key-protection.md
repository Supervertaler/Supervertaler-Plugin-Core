# API keys locked to the computer

**Status: plan, for Michael's approval. No code yet.** Written 2026-10-30. Covers
Supervertaler for Trados, Supervertaler for memoQ, Sidekick and Workbench (and the
Python core package). Building comes afterwards, one product at a time.

On 30 Oct 2026 Michael chose to lock stored API keys to the computer – Windows
encryption tied to the user's sign-in, kept outside the portable data folder – on
condition that no type of user is worse off. This note shows, user type by user type,
that the condition holds, and settles how each product does it.

---

## For Michael: what changes, user by user

**Today** every product keeps your keys as plain text in your data folder:
`D:\Supervertaler\settings\api-keys.json` (shared by Trados, memoQ and Sidekick) and
`D:\Supervertaler\workbench\settings\settings.json` (Workbench's own copy). Every copy
of that folder – on a sync service, in a backup, in a support zip – carries working keys
with it.

**Afterwards** each computer keeps the keys locked by Windows to your Windows sign-in,
in `C:\Users\mbeijer\AppData\Roaming\Supervertaler\api-keys.locked.json`. The data
folder holds no usable key. On a Mac, Workbench uses the Mac's Keychain.

| Who | What they notice | Worse off? |
|---|---|---|
| **One PC** (most users) | Nothing. Existing keys are moved over by themselves. A new key is pasted once, as now. | No |
| **Office network with roaming profiles** | Nothing. The keys follow you to any office PC, because they sit in the part of your Windows profile that travels with you – the same place Windows keeps the key that unlocks them. | No (to be checked once on a real network, see §8) |
| **Several people sharing one Windows sign-in** | Same as today: anyone signed in as that account can use the keys. | No |
| **Citrix, Remote Desktop, FSLogix** | Works wherever the Windows profile is kept between sessions – which Trados itself already needs. | No |
| **Administrator resets the Windows password of a local account** | The keys can no longer be unlocked. The product says so, names the key, and asks you to paste it again. Never a silent failure, never "wrong key". | One re-paste per key. Windows loses its own saved passwords the same way. |
| **Network (domain) account reset by IT** | Nothing: Windows gets its key back from the network. | No |
| **Microsoft-account or work-account sign-in reset online** | Probably like a local reset (to be checked). If so, the same message and one re-paste. | At worst one re-paste |
| **GroupShare users** | Like their GroupShare password: locked to their Windows sign-in, entered once per computer. Better than that password when something goes wrong – it fails silently today (decision 5). | No |
| **Company hands out one key** | Put the plain `api-keys.json` in the settings folder as now. The next product to start locks the keys and removes the file. | No |
| **Two PCs, a synced data folder, or two Windows accounts on one PC** | Each key is pasted once on each computer (and each Windows account), and a changed key is changed on each. Softened: in step 1 every PC quietly makes its own locked copy from the plain file, so a PC you use at least once in the months before step 2 needs nothing retyped even then. | **Yes – the one real cost** |
| **Workbench on a Mac** | Keys go into the Keychain. macOS may ask "allow?" once, and again after an update (decision 3). | A prompt |

**Two steps, so nobody loses a key while the products update at different times.**
Step 1: every product learns to read and write the locked keys, and keeps the plain
file going exactly as now. Step 2, months later and only after all four have shipped
step 1: the plain copies go. Nothing becomes safer in step 1 – it exists so that step 2
costs nobody a key.

**When to replace a key.** After step 2, if your data folder was ever synced, shared or
handed to someone while it held the plain file, create a new key at the provider, paste
it in, and delete the old one there. Before step 2 there is no point: a new key would go
into the plain file too.

**What it does not do.** A program already running under your own Windows account can
still read your keys – nothing that stores a key on the computer prevents that. And
copies made before step 2 (old backups, sync history, the Recycle Bin) are out of
reach, which is why replacing the key is the advice above.

The decisions only you can make are at the end (§12), to be put to you one at a time.

---

## 1. What exists today

Verified against the code on 30 Oct 2026 (core main 85af3b2, Trados 63c9715, memoQ
348a864, Sidekick 0cf5d4b, Workbench 5002bb3, Python core 411bab2).

### Where keys are stored

| # | Where | Form | Written by | Read by |
|---|---|---|---|---|
| 1 | `<data root>\settings\api-keys.json` | **plain** | core `ApiKeyStore` (Trados, memoQ, memoQ prompt editor); Sidekick `lib\sharedkeys.ahk` | the same; one entry per provider, including Sidekick's machine-translation keys (`deepl`, `google`, `microsoft`, `modernmt`) and `custom_openai` |
| 2 | `<data root>\trados\settings\settings.json`, `aiSettings.apiKeys.*_protected` | DPAPI | Trados, on every settings save (`AiApiKeys` setters) | `LlmClient.ResolveApiKey` as a fallback; copied into #1 by `ApiKeyStore.MigrateFrom` at start-up |
| 3 | same file, `aiSettings.customOpenAiProfiles[].apiKey` | **plain** | Trados AI settings | Trados, for its OpenAI-compatible endpoints |
| 4 | same file, `groupShareServers[].passwordProtected` | DPAPI | Trados GroupShare settings | `ServerTmClient` (not an AI key; see "Found along the way", item 3) |
| 5 | `%LOCALAPPDATA%\Supervertaler.memoQ\shared.txt`, `apikey` and `apikey.memoq` | **plain** | memoQ (`SharedSettings`); `apikey.memoq` is a plain copy of the key memoQ itself keeps encrypted | `ApiKeys.Fallback` |
| 6 | memoQ's own MT settings resource (`SupervertalerSecureSettings.ApiKey`) | encrypted by memoQ | memoQ | `ApiKeys.Fallback`. memoQ's store, not ours; left alone |
| 7 | Sidekick `settings.ini`, section `[Keys]`; legacy `ChatGptAPI.ini` | **plain** | Sidekick providers window (writes #1 and this) | `SK_Key` fallback; `ai.ahk` reads the legacy file. The installer suggests putting Sidekick *inside* the data folder (`<data root>\sidekick\`), so this is usually in the portable folder too |
| 8 | `<data root>\workbench\settings\settings.json`, `api_keys` and `custom_openai_profiles[].api_key` | **plain** | Workbench `save_api_keys`, and every `_save_unified_settings`, which rewrites the whole file | Workbench (four readers, below); core `LlmClient.ResolveApiKey` as its last fallback |
| 9 | `api_keys.txt` in up to five places (data root, `~\Supervertaler`, script folder variants) | **plain** | nothing now | Workbench legacy fallback and migration |

On a Mac, Workbench keeps #8 and #9 under its Mac data root. Workbench never reads #1:
today its keys and the plugins' keys are separate, except that Trados falls back to
Workbench's copy (memoQ does not).

### Who reads them

- **Trados:** core's `LlmClient.ResolveApiKey` – #1, then Trados's own keys (#2), then
  #8. Called from about fifteen places, once per action or batch run, never per segment.
- **memoQ:** its own `ApiKeys.Resolve` – #1 through core's `ApiKeyStore`, then #5, then
  #6 – in both the plugin and the prompt editor.
- **Sidekick:** one funnel, `SK_Key` (#1, then #7), used by `ai.ahk`, `providers.ahk`
  and `quicktrans.ahk`.
- **Workbench:** at least four separate readers – `Supervertaler.py` `load_api_keys`
  (via `_load_settings_section`), `modules/llm_clients.py` `load_api_keys`,
  `modules/batch_offload.py` `_resolve_api_key` (from a `settingsPath`), and a fallback
  in `modules/pdf_rescue_Qt.py`. Plus the copy in Python core,
  `supervertaler_core/llm.py` `load_api_keys`, whose other user, Otto, is retired.
- **The MCP server** (`src/Supervertaler.McpServer`) reads no AI key. It reads only the
  per-session bridge token from the data folder.

### Found along the way

Flagged rather than silently built on:

1. **The doc comments say plain text is deliberate.** `ApiKeyStore` and memoQ's
   `ApiKeys` both argue that a hand-editable file gets rotated and that anyone who can
   read it can read the whole profile. The second half does not hold for a portable data
   folder, which travels where the profile does not. Step 1 rewrites both comments.
2. **`DpapiSecret` says a blob is bound to "the Windows user AND the machine".** It is
   bound to the user's Windows key, which travels with a roaming profile (§2). Wording
   fix in step 1.
3. **The GroupShare password fails silently.** An unreadable one becomes an empty
   password: the settings panel shows a blank box and `ServerTmClient` sends an empty
   password to the server. And it sits inside the data folder, so on a folder shared by
   two PCs each PC's save makes it unreadable on the other. Decision 5.
4. **Trados warns about nothing on a second PC.** `WarnIfKeysUnreadable` checks
   Trados's own locked copies (#2). On a second PC sharing the data folder they are
   always unreadable, even when #1 supplies a working key. Goes away in step 1, which
   asks the new store instead.
5. **Sidekick writes the shared file without the lock core takes**, so a Sidekick save
   at the same moment as a Trados or memoQ save can lose one of them. Step 1 gives
   Sidekick the same lock for both files.
6. **Workbench's error messages still name `api_keys.txt`**, a file it has not used
   since keys moved into `settings.json`.

---

## 2. The rule

> **The data folder never holds a usable key. A key is stored only in a form that works
> for one Windows account (or one Mac login), outside the data folder.**
>
> **A key that cannot be unlocked is reported as such, by name, with what to do. It is
> never shown as an empty box and never sent to a provider as a blank or wrong key.**

**Why outside the data folder, not encrypted where it is.** A locked key can be opened
only by the account that locked it. Inside a data folder shared by two PCs, each PC
would lock the key its own way, the folder would carry that to the other PC, and the
other PC could no longer open it: the two would take turns asking for the key. That is
exactly what the GroupShare password does today (§1, "Found along the way", item 3). Kept in the Windows
profile instead, each PC has its own copy, and a roaming profile carries it to every PC
where the same sign-in works.

**How Windows does it (DPAPI, "current user").** Windows encrypts with a key of the
user's own, which it keeps in `%APPDATA%\Microsoft\Protect\<SID>\`, unlocked by the
user's password. That folder is in the *roaming* part of the profile, which is why the
locked keys go in `%APPDATA%` too: where Windows' key travels, ours travels with it.

- **The user changes their own password:** Windows re-locks its key with the new
  password. Nothing changes.
- **A domain account is reset by IT:** a domain-joined PC backs up the user's key to a
  domain controller, so it can be recovered after a reset (Microsoft's Event 4692
  documentation). Nothing changes.
- **An administrator resets a local account's password:** Windows cannot unlock its key
  without the old password. Everything locked with it, ours included, becomes
  unreadable, with error 0x8009000B, "Key not valid for use in specified state".
- **A Microsoft account or Entra ID (work) account reset online:** not settled by the
  documentation found. To be checked (§8). Either way it lands on the same handled
  path.

**Why a file and not Windows Credential Manager.** Credential Manager is DPAPI
underneath, but it can be restricted by policy on managed PCs, and its structure is
awkward to call from AutoHotkey. A DPAPI file is what the GroupShare password already
uses, it takes two calls from any language, and the products already know how to lock
and replace a key file safely.

**What the rule does not cover.** A program running under the user's own account can
unlock the keys without a prompt, as it can on any desktop. And copies already made –
backups, sync history, the Recycle Bin – are out of reach. §6 covers replacing keys.

This is encryption at rest using the operating system's own facility, which is the
example the EU Cyber Resilience Act gives for protecting stored secrets (obligations
from 11 Dec 2027).

---

## 3. Storage

### Windows: one locked file per Windows account

`%APPDATA%\Supervertaler\api-keys.locked.json` – beside `config.json`, the pointer every
product already reads to find the data root. For Michael:
`C:\Users\mbeijer\AppData\Roaming\Supervertaler\api-keys.locked.json`.

Same flat shape and provider ids as `api-keys.json`, so the existing parsers (core's
regex, Sidekick's `Jxon_Load`, Python's `json`) read it unchanged. Each value is the key
locked by Windows, in base64:

```json
{
  "_comment": "Supervertaler API keys, locked by Windows to this Windows account. They cannot be read on another computer or by another account. Change them in any product's settings.",
  "claude": "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA…",
  "deepl": "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA…",
  "trados:custom_openai:Acme server": "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA…"
}
```

The contract every implementation must keep identical, or a key locked by one product
will not open in another:

- **What is locked:** the key, trimmed, as UTF-8 bytes – no byte-order mark, no
  terminating zero.
- **How:** `CryptProtectData`, current-user scope, flag `CRYPTPROTECT_UI_FORBIDDEN`
  (never a prompt), no description, **no extra entropy**. An entropy value would be
  public in these repositories and would add nothing.
- **Stored as:** standard base64 with padding, no line breaks.
- **Ids:** the provider ids already in `api-keys.json`, with the same aliases
  (`anthropic` → `claude`, `custom` → `custom_openai`). A product's private keys – a named
  OpenAI-compatible profile in Trados or Workbench – get a prefixed id
  (`trados:custom_openai:<profile name>`, `workbench:custom_openai:<profile name>`). The
  existing parser rejects `"` and `\` in an id, so those are dropped from profile names
  when forming one. Ids are case-insensitive, as now.
- **Ids starting with `_` are ignored**, and **every product keeps entries it does not
  know** when it rewrites the file: Trados must not drop Sidekick's DeepL key.
- **No format version.** The file name is the version. A different format later gets a
  different name.

**Writing** follows `ApiKeyStore.Update` exactly: one read-modify-write under an
exclusive open that shares only delete, the new file written beside the old one and
renamed over it (`AtomicFile`), retried for up to a second if another product holds it.
The file is the old keys or the new ones, never neither. Abandoned temporary files are
swept as now.

**Reading** gives each key one of three answers: **present**, **absent**, or
**unreadable** (the file has an entry, but Windows refuses to unlock it). The Windows
error code goes to the log, and the message is the same whatever the cause: the
product cannot reliably tell a password reset from a file copied from elsewhere, and the
user's remedy is the same.

- **An unreadable entry is kept** until the user enters a new key for that provider,
  which replaces it. Some causes are temporary (a profile loaded oddly, a domain
  controller not yet reachable after a reset), and deleting the entry would destroy a key
  that would have come back.
- **A damaged file** (not valid JSON) makes every lookup "unreadable", with a message
  naming the file. At the next write it is renamed aside to
  `api-keys.locked.damaged-<yyyyMMdd-HHmmss>.json`, never overwritten. It holds only
  locked values, so keeping it costs nothing and keeps the evidence.
- **No in-memory cache.** Unlocking one key should take around a millisecond (to be
  measured in step 1), and callers resolve a key once per action or batch run. A cache
  would be state that can go stale when another product changes a key.

**Scale:** one entry per provider and profile – about fifteen today, never thousands –
and one lookup per AI action or batch run. Designed for the same at 100 times the usage:
the lookup count grows with actions, not with segments.

### Mac: the Keychain (Workbench)

Through the `keyring` package (MIT licence; macOS backend only, through Apple's Security
framework): **one** Keychain item, service `Supervertaler`, account `api-keys`, holding
the same JSON map with plain values. One item rather than one per provider, because
macOS asks permission per item: one item means at most one question after each update,
not one per key (decision 3).

Failure path: a frozen PyInstaller build can end up with no working keyring backend
(keyring finds its backends through entry points, which freezing can lose). Workbench
then keeps the plain copy, as today, and says so once (§7, M7). The macOS build must be
checked for this on every release (§8).

### Linux (Workbench run from source)

`keyring`'s Secret Service backend where one is running. Otherwise the plain copy stays
and Workbench says so once, as on a Mac without a Keychain. No Linux build ships, so
nothing more.

---

## 4. How each product does it

### Core (Trados and memoQ)

- `ApiKeyStore` keeps its public surface (`Get`, `Set`, `MigrateFrom`, `CheckShape`,
  `FilePath`), so the fifteen Trados call sites and memoQ's `ApiKeys` do not change. It
  gains `Status(provider)` (present, absent, unreadable) and one method that returns the
  sentence a product shows when a key is missing or unreadable (§7), so every product and
  every call site says the same thing.
- `DpapiSecret` gains a variant of `Unprotect` that reports failure and the Windows
  error code, instead of returning "" for both "nothing" and "refused".
- The locked file's path can be redirected for tests, the way `SupervertalerPaths.Set`
  redirects the data root. **Without that, the existing tests would write the user's
  real locked file**: they redirect only the data root, and this file lives in
  `%APPDATA%`.
- `LlmClient.ResolveApiKey`: locked store per the step rules (§5). Its last fallback,
  Workbench's plain `settings.json`, is removed in step 2.

### Trados

- At start-up (where `SettingsService.Current` calls `MigrateFrom` today): the step-1
  sync, later the step-2 import (§5).
- AI Settings: each provider's key box shows the key, or the unreadable message for that
  provider. `WarnIfKeysUnreadable` goes; its job is done per provider by `Status`.
- OpenAI-compatible profiles: the key is also written to
  `trados:custom_openai:<profile name>` in step 1, and only there in step 2. Renaming a
  profile moves its entry.
- Step 2: stops writing `aiSettings.apiKeys`, clears it after the import, and clears
  `customOpenAiProfiles[].apiKey`.

### memoQ

- The sync or import runs inside `ApiKeys.EnsureMigrated`, which both the plugin and the
  prompt editor already pass through.
- The options form and the prompt editor's settings form show the unreadable message per
  provider. The engine's error (via `EngineContext.ApiKey`) uses core's sentence instead
  of letting a blank key reach the provider.
- Step 2: stops writing `apikey` and seeding `apikey.memoq` in `shared.txt`, and clears
  both. memoQ's own encrypted copy (#6) is imported into the locked file, so the prompt
  editor, which cannot read memoQ's resource, still finds it.
- `tools\apikeys-test.ps1` already asserts that the harness never touches the real key
  file. It gains the same assertion for the locked file.

### Sidekick (AutoHotkey)

`lib\sharedkeys.ahk` gains lock and unlock functions calling Windows directly through
`DllCall`, in `crypt32.dll`: `CryptProtectData` and `CryptUnprotectData`, with
`CryptBinaryToStringW` and `CryptStringToBinaryW` for base64 (flags
`CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF`), so even base64 is Windows' own. Each output
buffer is freed with `LocalFree`. Where it goes wrong if done carelessly:

- **The `DATA_BLOB` layout** is a 4-byte length and a pointer: 16 bytes with padding on
  64-bit AutoHotkey, 8 on 32-bit. Build it with `A_PtrSize`, never a fixed size.
- **The byte count must exclude the terminating zero** that `StrPut` counts. Locking one
  byte too many gives a key with a stray NUL that core reads back as a different key.
  The cross-product test (§8) exists for this.
- **The lock.** Both files are opened the way core opens them – exclusive, sharing only
  delete – for the read-modify-write, retried for up to a second, and replaced by
  rename as core does (try while holding; if the volume refuses, let go and try once
  more). A read that meets a held file waits and retries rather than reporting "absent".
- Messages appear in the providers window and wherever an action needs a key.
- Step 2: blanks the `[Keys]` values in `settings.ini` after importing them, and imports
  and deletes `ChatGptAPI.ini`. Machine-translation keys are covered like the rest:
  they cost money too.

### Workbench (Python)

- **First, one reader.** The four readers (§1) are funnelled through one function in
  `modules/llm_clients.py` before anything else changes. Changing four in step is how
  one gets missed.
- **Windows:** `ctypes` calls to `CryptProtectData` and `CryptUnprotectData` – no new
  dependency, and the same file and contract as the plugins. The lock uses
  `CreateFileW` through `ctypes` with the same sharing as core, because Python's own
  `open` shares read and write and so would not exclude the other products.
- **Mac and Linux:** `keyring`, as §3.
- `_save_unified_settings` rewrites the whole `settings.json`, so in step 2 the
  `api_keys` section is dropped on load and never written back, and
  `custom_openai_profiles[].api_key` moves to `workbench:custom_openai:<profile name>`
  (written there as well from step 1).
- `batch_offload`'s `settingsPath` route reads the locked store through the same
  function. A key passed in a job file as `apiKey` is the caller's choice and stays.
- Workbench **joins the shared keys** (decision 1): one key per provider for all four
  products. At its first step-1 start it adds its own keys where the shared store has
  none, never overwriting – the rule `MigrateFrom` already follows.
- The messages that name `api_keys.txt` are replaced by core's sentences (§7).

### Python core

`supervertaler_core.llm.load_api_keys` is a copy of Workbench's old reader. If nothing
else uses it now that Otto is retired, it is removed rather than ported (`LLMClient`
already takes the key as an argument). Otherwise it calls Workbench's single reader.

---

## 5. Rollout in two steps

**Step 0** is every release in the field today. **Step 1** adds the locked file and keeps
the plain one. **Step 2** removes the plain copies. "Plain file" below is the shared
`<data root>\settings\api-keys.json`. The product-private plain copies (#3, #5, #7, #8,
#9 in §1) follow the same steps inside their own product.

### What each step does with a key X

| | reads X | saves X | removes X |
|---|---|---|---|
| **Step 0** (today) | plain | plain | plain |
| **Step 1** | plain if it has X, else locked | plain, then locked | plain, then locked |
| **Step 2** | locked if it holds a readable X, else plain | removes X from plain if the plain file exists, then locked | plain, then locked |

A save counts as done when the file that decides is written: the plain file in step 1
(a failed locked write is logged, and the next start's sync repeats it), the locked file
in step 2.

**At start-up:**

- **Step 1 sync:** each key in the plain file is copied into the locked file where the
  locked file lacks it or holds something else. It **adds and replaces, never removes**:
  a damaged or half-written plain file must not be able to empty the locked one.
- **Step 2 import:** with the plain file held exclusively throughout, each of its keys is
  locked (replacing what is there – the plain file is something somebody put there, so
  it is newer), read back and compared, and only when **every** key has been locked and
  verified is the plain file deleted, while still held, as `ApiKeyStore` already deletes
  an empty file. If any key fails, the plain file stays, the product keeps reading that
  key from it, and the user is told (§7, M5). Keys are never deleted before they have
  been saved somewhere else.

**One ordering rule: the plain file first, then the locked file.** Each file is locked
only while it is written, and the two are never held together – except by the import,
which takes the plain file first. One order everywhere means no two products can each
wait for the other. And it makes a crash between the two writes harmless:

- Step 1 saving K2 dies after the plain file: plain K2, locked K1. Reads take plain (K2),
  and the next sync copies K2 into the locked file.
- Step 2 saving K2 dies after removing X from the plain file: locked still K1. The save
  is lost, visibly – the box shows K1 – and is redone. Nothing stale overwrites a newer
  key.

### Walking through mixed versions

| Situation | Result |
|---|---|
| Step 1 next to step 1, same PC | Same as today: the plain file decides. |
| Step 0 saves a key, step 1 reads | Step 1 reads plain first: sees it. |
| Step 1 saves, step 0 reads | Step 1 wrote the plain file too: seen. |
| Step 2 has imported (plain file gone), step 1 reads | Plain has nothing, so step 1 reads the locked file, which the import filled. |
| Step 1 saves after the import | It recreates the plain file with that one key. Step 1 reads plain for that key and locked for the rest; step 2 reads locked, which step 1 also wrote. The next step-2 start imports it and deletes the file again. |
| Step 2 saves while a step-1 product's plain file exists | Step 2 removes X from the plain file, so step 1 falls through to the locked file and sees the new key. |
| Two PCs, synced data folder, both step 1 | Plain file decides, as today. Each PC's sync builds its own locked copy: this is what makes step 2 painless for two-PC users. |
| Two PCs, one at step 2 | Its import deletes the plain file; the sync service deletes it on the other PC, whose step-1 products read their locked copy built earlier. |
| **Step 0 next to step 2** | Not covered. A product left at step 0 cannot see keys saved by a step-2 product, and a step-2 product sees a step-0 save only after its next start. This is why step 2 waits until all four products have shipped step 1 and users have had time to update (decision 2). |
| **Downgrade past step 2** | A product rolled back to step 0 after the plain copies are gone finds no key and asks for it. Said in the step-2 release notes. |

**Known, accepted:** a key deleted in a step-0 product while a step-1 product is in use
comes back from the locked file, because the sync never removes. It is rare (deleting a
key in a product you have not updated, during the step-1 months) and visible, and it
is the price of the sync never being able to wipe the locked file.

### What happens to the existing plain copies

| Copy | Step 1 | Step 2 |
|---|---|---|
| `<data root>\settings\api-keys.json` | kept and written, as now | imported, verified, **deleted** |
| Trados `aiSettings.apiKeys` (locked, in the data folder) | kept | imported once, cleared, no longer written |
| Trados `customOpenAiProfiles[].apiKey` | kept and written | moved to the locked file, cleared |
| memoQ `shared.txt` `apikey`, `apikey.memoq` | kept | imported, cleared, no longer written |
| Sidekick `settings.ini` `[Keys]`, `ChatGptAPI.ini` | kept and written | imported; `[Keys]` blanked, `ChatGptAPI.ini` deleted |
| Workbench `settings.json` `api_keys`, profile `api_key` | kept and written | imported, removed from the file |
| Workbench `api_keys.txt` (any of the five places) | read as now | imported, deleted |

Deleted means deleted, not moved to the Recycle Bin, which would keep the plain text, and
not overwritten first, which means nothing on an SSD or in a synced folder's history.
The company-key route is the same import: a plain `api-keys.json` placed in
`<data root>\settings\` at any time after step 2 is picked up at the next start, locked
and removed. So is a key typed into that file by hand, which keeps the "paste a line
into a text file" way of replacing a key open.

### What goes in which release

- **Step 1, per product:** the locked store, the sync, the messages, and the fixes in
  §1 that step 1 touches (doc comments, Sidekick's lock, Workbench's single reader,
  Trados's warning). Order: core first (one core worker), then Trados and memoQ (one
  core version), Sidekick, Workbench for Windows and Mac. The Help pages in the same
  round (§10).
- **Step 2, all products:** the import, the removal of the plain copies, the end of
  core's Workbench fallback. Ships only when decision 2 says so. The step-2 release
  notes carry the replace-your-key advice (§6) and the downgrade note.

The Mac follows the same two steps, although only Workbench runs there: the same rules
everywhere are easier to keep right than a Mac shortcut.

---

## 6. Replacing (rotating) keys

**When.** A key should be replaced if, while it was stored in plain text (Trados since
18.20.187 of 6 Sep 2026, memoQ and Sidekick since they adopted the same file, Workbench
always), the data folder was:

- synced to a cloud or NAS service that anyone else can open, or
- shared with colleagues, or placed on a network drive others can read, or
- copied to anyone else – a support zip, a backup held by someone else, a disposed or
  repaired computer.

A data folder that only ever lived on the user's own computer and own backups needs
nothing.

**Not before step 2.** Until the plain copies go, a new key is written to the plain file
as well and travels the same way. The advice goes out with step 2.

**How.** Create a new key in the provider's console, paste it into any Supervertaler
product, then delete the old key in the provider's console. The last part is what makes
old copies worthless.

**Where users are told:** the step-2 release notes, a Help page on API keys (§10), and
one sentence in the import notice (M4) pointing to it.

---

## 7. Messages users see

Every message names the provider, says what happened in plain words, and says what to
do. `<Settings>` is each product's own place: AI Settings (Trados), Supervertaler
options (memoQ), AI providers & keys (Sidekick), Settings → AI (Workbench).

**M1 – unreadable key, in settings, beside that provider's key box:**
> Your Anthropic key can't be read on this computer – it was saved under another Windows
> sign-in, or your Windows password was reset. Paste the key in again; nothing else is
> affected.

**M2 – unreadable key when an action needs it** (instead of calling the provider):
> Can't use your Anthropic key – it can't be read on this computer (saved under another
> Windows sign-in, or your Windows password was reset). Paste it in again in <Settings>.

**M3 – a fixed line under the key box:**
> Saved on this computer only, locked to your Windows sign-in.

(Mac: "Saved in your Mac's Keychain.")

**M4 – after an import** (once, in the settings window's status line and the log, never
a pop-up – with a product still at step 1 it can recur):
> Locked 2 API keys from D:\Supervertaler\settings\api-keys.json to this computer and
> removed the file. If that folder was ever shared or synced, replace those keys at the
> provider – see Help.

**M5 – an import that failed:**
> Your API keys in D:\Supervertaler\settings\api-keys.json could not be locked to this
> computer (Windows said: <reason>). They are still being used from that file, which has
> been left where it is.

**M6 – a damaged locked file:**
> The file holding your locked API keys was damaged and has been set aside as
> <path>. Please paste your keys in again.

**M7 – no Keychain (Mac) or secret store (Linux):**
> Your Mac's Keychain isn't available to Supervertaler, so your keys stay in
> Supervertaler's settings file as before.

A missing key that was never there keeps today's wording ("No API key set for
Anthropic…"). Only an unreadable one gets M1 or M2. Telling the two apart is the point of
the three answers in §3.

---

## 8. Tests

### Offline, core (`tests\`, run without Trados or memoQ)

All against a temporary data root and a temporary locked-file path; never the user's.

1. **Round trip:** save, read back, and assert the key's text appears nowhere in the
   locked file, in UTF-8 or UTF-16.
2. **Unreadable entry, made for real:** a value locked *with* an entropy value, so
   `CryptUnprotectData` without it refuses. Plus one with a flipped byte. Assert:
   status unreadable, `Get` null, the message is M1 or M2, the entry is still in the
   file afterwards, and saving a new key replaces it.
3. **The state table.** Every combination of plain file (absent, has K1, has K2,
   damaged, empty) and locked file (absent, K1, K2, unreadable entry, damaged), for
   step 1 and for step 2: what a read returns, and what the files hold after the sync
   or import. This one table is the mixed-version test and the crash test – a crash
   between two writes is just one of these states – and it asserts the key each state
   yields, not how many keys there are.
4. **The sync never removes:** a damaged or empty plain file leaves the locked file
   untouched.
5. **Import failure:** the locked file held exclusively by the test so the write fails.
   The plain file must stay, the key must still be read from it, and M5 must be the
   report.
6. **Half-written files:** a truncated locked file is damaged, set aside at the next
   write, and reported with M6; an abandoned temporary file is swept and the real file is
   intact.
7. **Unknown entries survive:** a `deepl` entry and a `workbench:custom_openai:…` entry
   are still there after core rewrites the file.
8. **Two writers at once:** two processes saving different keys at the same moment, both
   kept – as `ApiKeyStoreTests` already does for the plain file, using its child-process
   helper.
9. **No blank key reaches a provider:** `ResolveApiKey` with an unreadable entry gives
   the caller M2, not an empty string.

### Offline, across the three languages

A PowerShell script, run with `-File`, in which Sidekick's functions lock a key into a
temporary locked file, Python's do the same with another, and core reads both – then the
other way round. Each implementation's lock and unlock functions take the file path as a
parameter, so the test needs no switch in any product. This catches the encoding and
`DATA_BLOB` mistakes of §4, which no single-language test can.

### Offline, products

- memoQ `tools\apikeys-test.ps1`: the real locked file is byte-identical before and after
  a harness run, as it already asserts for the plain file.
- Trados: the offline `.dev` tests cover the start-up sync and import against a
  temporary data root.
- Workbench: unit tests for the Windows functions (round trip, unreadable, damaged,
  lock), and `keyring` with its in-memory backend for the Mac logic.

### Once, by hand, on real systems

- **Local password reset** (Michael can do this in ten minutes): create a spare local
  Windows account, sign in, paste a made-up key, reset that account's password from
  Michael's administrator account, sign in again, and see M1 – not an empty box, not a
  401.
- **Roaming profile on a domain:** a tester in an office with roaming profiles enters a
  key on one PC and uses it on another. Before step 2.
- **Citrix or FSLogix:** a tester, same check across two sessions.
- **Microsoft account or Entra ID online reset:** what happens, so the Help page can say
  it plainly.
- **Mac, every release:** the frozen build finds the Keychain backend, and the number of
  "allow?" prompts after updating from the previous build.
- **32- and 64-bit Studio:** a key saved in Studio 2024 is read in Studio 2026. The
  format does not depend on bitness, but both are installed side by side and it costs a
  minute.

---

## 9. Not in this plan

- No setting to turn locking off, and no "export keys" feature. Nobody has asked for
  either, and the import route covers the company key and hand editing.
- Nothing against programs running under the user's own account (§2).
- No change to memoQ's own encrypted key store (#6).
- The GroupShare password is decision 5, not part of this work unless Michael says so.

---

## 10. For the Help pages (later)

Pages that name `api-keys.json` today: `trados\settings\ai-settings.md`,
`memoq\getting-started.md`, `memoq\prompt-editor.md`, `sidekick\installation.md`,
`sidekick\settings.md`, and Workbench's `get-started/api-keys` page. Each needs:

- where keys are kept now, and that the data folder no longer holds them;
- "once per computer and Windows account", and why;
- what the "can't be read" message means and what to do;
- the company-key route (drop `api-keys.json` into the settings folder);
- when to replace a key (§6), in step 2;
- on a Mac, the Keychain question.

---

## 11. Sources

- Microsoft, Event 4692 "Backup of data protection master key was attempted":
  domain-joined PCs back up the user's DPAPI key to a domain controller so secrets
  survive a password reset –
  https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/auditing/event-4692
- ESET knowledge base, error 0x8009000B after an administrator reset a local password
  in Computer Management –
  https://techcenter.eset.nl/en/kb/articles/error-after-resetting-user-password-in-windows-computer-management
- Apple, Keychain Access user guide, on unsigned apps and "Always Allow" –
  https://support.apple.com/guide/keychain-access/kyca17140/mac
- keyring issue #619, a new binary must be allowed again per Keychain item –
  https://github.com/jaraco/keyring/issues/619

---

## 12. Decisions only Michael can make

To be put to him one at a time, each with a recommended answer.

1. **Workbench joins the shared keys?** Then one key per provider for all four products.
   Where Workbench's own key differs from the shared one, the shared one wins, and
   Workbench says once which ones changed.
   *Recommended: yes.* It is what #108 did for the other three, and it is what makes one
   locked file enough. Anyone who deliberately uses a different key in Workbench than in
   the plugins could no longer do so – rare.

2. **How long between step 1 and step 2?** Step 2 only after all four step-1 releases are
   out, plus time for users to update. Trados goes through the RWS App Store, the others
   through GitHub.
   *Recommended: eight weeks after the last step-1 release*, and well before the CRA
   date of 11 Dec 2027 – so step 1 this winter, step 2 next spring.

3. **Mac: the Keychain question after each Workbench update.** macOS recognises an
   unsigned app's new build as a different app and asks again.
   (a) Accept one question per update – one Keychain item keeps it to one.
   (b) Also sign the Mac build with a home-made certificate: free, and the question
   should then stop recurring (to be confirmed on a Mac). Gatekeeper's first-launch
   warning stays.
   (c) An Apple Developer ID ($99 a year): no recurring question and no Gatekeeper
   warning.
   *Recommended: (a) now, (b) if the Mac build can be signed that way.*

4. **How loudly to advise replacing keys?** (a) Release notes and Help page only, or
   (b) also the one sentence in the import notice (M4).
   *Recommended: (b)* – the notice reaches exactly the users who had a plain file, at
   the moment it matters.

5. **The GroupShare password the same way?** Move it out of the data folder into the
   locked file, and give it M1 and M2 instead of failing silently. A small Trados-only
   change after step 1; it also stops two PCs sharing a data folder from taking turns
   asking for it.
   *Recommended: yes, as a separate change.*
