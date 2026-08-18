# Aprimo DAM → CSV export utility

A C# console utility that pages through the Aprimo DAM REST API and writes record
metadata to CSV. Works against any Aprimo DAM tenant — set `Aprimo.Tenant` and supply a
client-credentials registration.

Built for large exports: controllable request rate, rolling output files, a hard row cap,
resumable runs, incremental (changed-records) exports, and a column list driven by a file
you can edit or generate from an Aprimo Data Exports schema.

One NuGet dependency: `System.Security.Cryptography.ProtectedData`, which backs the
DPAPI-encrypted credential store. `System.Text.Json` ships with the framework and the
CSV writer is hand-rolled, so nothing else is needed.

### Aprimo's Open Source Policy 
This code is provided by Aprimo _as-is_ as an example of how you might solve a specific business problem. It is not intended for direct use in Production without modification.

You are welcome to submit issues or feedback to help us improve visibility into potential bugs or enhancements. Aprimo may, at its discretion, address minor bugs, but does not guarantee fixes, vulnerability remediation or ongoing support.

It is expected that developers who clone or use this code take full responsibility for supporting, maintaining, securing, and vulnerability management of any deployments derived from it.

If you are interested in a production-ready and supported version of this solution, please contact your Aprimo account representative. They can connect you with our technical services team or a partner who may be able to build and support a packaged implementation for you.

Please note: This code may include references to non-Aprimo services or APIs. You are responsible for acquiring any required credentials or API keys to use those services—Aprimo does not provide them.

## Status

Builds clean on .NET SDK 8.0.423 — Debug and Release, **0 warnings, 0 errors**.

```bash
dotnet build src/AprimoExport/AprimoExport.csproj -c Release
```

**196 offline checks, all passing.** The suite lives in
[tests/AprimoExport.Tests](tests/AprimoExport.Tests) and needs nothing but the SDK — no
test framework to restore:

```bash
dotnet run --project tests/AprimoExport.Tests
```

Exit code 0 means everything passed. Fixtures are synthetic and tenant-agnostic, and the
only assertions made about the shipped `appsettings.json` / `fields.txt` are structural, so
editing those for your own tenant will not break the suite. What it covers:

- **Extraction** — nulls falling back to defaults, numbers kept as raw text, multi-value
  list fields, multi-language joins, `||` fallback branches, `@languageId` pinning, and
  field names containing spaces, `?` and `#`
- **Field lists** — every separator, comment handling that survives `#` inside a real
  field name, and an Aprimo Data Exports schema XML parsed to the same result
- **CSV output** — rolling parts, RFC 4180 quoting verified with a quote-aware reader,
  BOM, formula-injection guard that leaves negative numbers alone, and all three
  newline-handling modes
- **Rate limiter** — measured pacing, bursts, disabled mode, `Retry-After` backpressure,
  and cancellation while waiting
- **Credential store** — DPAPI round-trip with a byte-level check that no secret appears
  in plaintext, multi-tenant isolation, and graceful failure on an undecryptable file
- **Delta windows** — half-open and exactly adjacent, the mark advancing to the window end
  rather than to `max(ModifiedOn)`, a changed query starting a fresh lineage, and bad
  `--since` values rejected
- **Guards** — every CLI switch, and validation that rejects placeholder credentials, an
  expression-less Search run, oversized page sizes and duplicate columns

**Confirmed against a live production tenant** via `--sample`:

- OAuth client-credentials authentication, and the token endpoint derived from the tenant
  name (cross-checked against the tenant's OpenID discovery document)
- `select-record` **is** honoured on the collection endpoints — `fields`,
  `masterFileLatestVersion`, `createdBy` and `modifiedBy` all came back populated on 5/5
  records, so no per-record hydration is needed. `classifications`, `files`,
  `permissions` and `locks` are null as expected.
- The `CreatedBy` / `ModifiedBy` fallback chains resolve to real user names
- A 154-column mapping generated from a production Aprimo Data Exports schema, including
  field names containing spaces, `?` and `#`
- Scale: tens of millions of records, at which the notes under
  [Full backfill](#full-backfill) apply

## Full backfill

Deltas stay small, but a full export is a different job. Work out your own numbers from
`--sample`, which reports the approximate total:

| Records | `--rps 5` | `--rps 8` | Files at 1M/file |
|---|---|---|---|
| 1 M | ~3 min | ~2 min | 1 |
| 10 M | ~33 min | ~21 min | 10 |
| 50 M | ~2.8 h | ~1.7 h | 50 |

Throughput is `rps × PageSize` records/sec, so 5 × 1000 ≈ 5,000/sec. Budget roughly
300–500 bytes per row for a wide (150+ column) mapping — 50 M rows is tens of GB.

**Use `--paging Keyset` for any full backfill of more than a few hundred thousand
records.** `Offset` paging has to send ever-growing `skip` values, and
Elasticsearch-backed search normally refuses deep paging well before the millions. This
is the case the mode was built for:

```bash
aprimo-export.exe --paging Keyset --rps 8 --prefix dam-full
```

Keyset needs no expression: it seeds an open `CreatedOn >= 1900-01-01` lower bound on the
first page, then advances the watermark. It stays in Search mode because it rewrites the
expression each page.

Ctrl+C checkpoints, and `--resume` continues — worth knowing on a multi-hour run.

**Alternative: chunk by date.** If keyset misbehaves on a given tenant, the delta
machinery is a robust fallback — walk the history a month at a time, so no single window
is deep enough for `Offset` paging to struggle:

```bash
aprimo-export.exe --since 2019-01-01 --until 2019-02-01 --prefix dam-2019-01
```

Each chunk is independently resumable and identifiable, and chunks can run on separate
days without holding a multi-hour job open.

Still unverified end to end: `Keyset` paging at depth, and the keyset watermark date
format in a search expression. See [Open items](#open-items).

## How to run it

A published build sits in [dist/](dist/) — `aprimo-export.exe` plus the config it
reads. It works from any directory, and treats the `appsettings.json` **next to the
exe** as its config. Edit `dist/appsettings.json` for real runs; edit
`src/AprimoExport/appsettings.json` if you want the change to survive a re-publish.

Three steps, in order:

```bash
cd dist
```

```bash
aprimo-export.exe --login --validate-only
```

```bash
aprimo-export.exe --login --sample 5
```

Set `Aprimo.Tenant` in `dist/appsettings.json` first — it is the subdomain of your Aprimo
DAM URL, and it is the only value that must be configured before the first run. `--login`
prompts for the client ID and secret with the secret masked, and writes nothing to disk.

### Not retyping credentials every run

**Best option — save them encrypted, once:**

```bash
aprimo-export.exe --save-credentials
```

Prompts for the ID and secret, **verifies them against the token endpoint**, and only
then stores them encrypted with Windows DPAPI under
`%LOCALAPPDATA%\AprimoExport\credentials.dat`. A mistyped secret is never persisted.
After that, no credentials are needed at all:

```bash
aprimo-export.exe --sample 3
```

DPAPI protection is scoped to your Windows account on this machine — nobody else can
decrypt the file, and copying it elsewhere yields nothing. There is no plaintext secret
anywhere on disk, and the file lives outside the project tree so it cannot be committed.
Entries are keyed by tenant, so several tenants can coexist.

```bash
aprimo-export.exe --list-credentials
```

```bash
aprimo-export.exe --clear-credentials
```

`--list-credentials` never prints secrets. `--clear-credentials` removes the current
tenant's entry; add `--all` to wipe the store.

The store only ever **fills gaps** — a value from the config file, environment or
command line always wins, so a saved credential can never silently shadow an explicit one.

**Alternative — environment variables**, if you prefer them or need a non-Windows host:

> Substitute your real values below. Pasting these lines unchanged sets the credential
> to the literal text `<your-client-id>`, which then **silently overrides the config
> file** — the exact trap described in the precedence callout. The tool now rejects
> known placeholders on sight, but the cleanest fix is not to create them.

```bash
setx APRIMO_CLIENT_ID <your-client-id>
```

```bash
setx APRIMO_CLIENT_SECRET <your-client-secret>
```

`setx` does not affect the session you run it in — **open a new terminal**. Then drop
`--login` entirely and just run `aprimo-export.exe --sample 3`.

To check or clear what is actually set:

```bash
[Environment]::GetEnvironmentVariable('APRIMO_CLIENT_ID','User')
```

```bash
[Environment]::SetEnvironmentVariable('APRIMO_CLIENT_ID',$null,'User')
```

**Or the config file**, `dist/appsettings.json` — again, real values, not these:

```json
"ClientId": "<your-client-id>",
"ClientSecret": "<your-client-secret>",
```

This works identically, but it puts the secret in plaintext inside the project folder,
where it would be committed the moment that folder becomes a git repo. Prefer the
environment variables.

> **Precedence trap.** Order is file → environment → command line, so the environment
> **silently overrides** the config file. A stale or placeholder `APRIMO_CLIENT_ID` wins
> over whatever is in `appsettings.json` and produces `invalid_client` with no obvious
> cause. It is doubly quiet because the interactive prompt only asks for values that are
> *missing* — so a wrong ID already in the environment rides along while you type the
> real secret. Check with
> `[Environment]::GetEnvironmentVariable('APRIMO_CLIENT_ID','User')` before trusting the
> config file. Known placeholder values are now rejected during validation.

Neither of these two stores the secret encrypted: `setx` writes to `HKCU\Environment`
and the config file is plaintext, both readable by anything running as you. Use
`--save-credentials` if that matters.

`--config <path>` points at a different config file.

Rebuild and re-publish after editing source:

```bash
dotnet publish src/AprimoExport -c Release -o dist
```

## Quick start (from source)

Set your tenant in [appsettings.json](src/AprimoExport/appsettings.json) — the subdomain
of your Aprimo DAM URL:

```json
"Tenant": "your-tenant-here"
```

Then save the credentials once, encrypted:

```bash
dotnet run --project src/AprimoExport -- --save-credentials
```

[fields.txt](src/AprimoExport/fields.txt) ships with the universal record properties and
master-file details already mapped, plus commented examples showing how to add your own
metadata fields. Point `FieldsFile` at an Aprimo Data Exports schema XML instead to derive
every column automatically.

Check connectivity:

```bash
dotnet run --project src/AprimoExport -- --validate-only
```

Then **run the sample diagnostic before any real export** — one request, no CSV
written. It reports which language IDs your tenant returns and which of the 154
columns actually resolve, so a wrong field name surfaces in seconds rather than
mid-export:

```bash
dotnet run --project src/AprimoExport -- --sample 5
```

A 500-row demo at 2 requests/second:

```bash
dotnet run --project src/AprimoExport -- --max-rows 500 --rps 2 --overwrite
```

Full export, 250k rows per file:

```bash
dotnet run --project src/AprimoExport -- --paging Keyset --max-per-file 250000 --rps 8
```

## The five requested features

| # | Feature | Where |
|---|---|---|
| 1 | Rate limiting, requests/second | `Throttle.RequestsPerSecond` / `--rps` |
| 2 | Client-credentials OAuth with token rolling | `Aprimo.*` / [OAuthTokenProvider.cs](src/AprimoExport/Auth/OAuthTokenProvider.cs) |
| 3 | Configurable metadata fields | `FieldsFile` / `--fields-file` / [fields.txt](dist/fields.txt) |
| 4 | Max records per file | `Limits.MaxRecordsPerFile` / `--max-per-file` |
| 5 | Total exported row cap | `Limits.MaxTotalRecords` / `--max-rows` |

### 1. Rate limiting

Three settings under `Throttle`, all editable in `appsettings.json`:

| Setting | Default | CLI | Meaning |
|---|---|---|---|
| `RequestsPerSecond` | `5.0` | `--rps` | Sustained rate. Fractional allowed (`0.5` = one request every two seconds). `0` disables throttling. |
| `Burst` | `1` | `--burst` | Token-bucket depth: how many requests may fire back-to-back after an idle period. `1` gives strictly even spacing, which is kindest to a shared tenant. |
| `MaxConcurrentRequests` | `1` | — | Cap on in-flight requests. Config-only; the page loop is sequential, so `1` is correct today. |

```bash
aprimo-export.exe --rps 2 --burst 1 --sample 3
```

Measured in the test suite: 10 rps paced 5 requests to 436 ms (expected ~400); 4 rps
spaced consecutive requests 254 ms apart; `Burst 5` let 5 through in 0 ms; `rps 0` ran
50 requests in 0 ms; a 400 ms `Retry-After` held the next request 405 ms.

Three behaviours that are easy to get wrong elsewhere:

- **Retries pass through the same limiter**, so backoff can never push the emitted rate
  above the ceiling — a retry storm still respects `--rps`.
- **A 429 or 503 holds back every caller**, not just the throttled request:
  `PenalizeAsync` empties the bucket for the `Retry-After` interval.
- **Token requests deliberately bypass the limiter** (separate `HttpClient`), so
  credential renewal cannot be starved by the export on a long run.

Retry behaviour is separately tunable under `Source.Retry` — `MaxAttempts`,
`InitialBackoffSeconds`, `MaxBackoffSeconds`, `BackoffMultiplier`, `RespectRetryAfter`.
Config-file only, no CLI flags.

**Throughput** is roughly `RequestsPerSecond × Paging.PageSize` records/sec. At the
defaults that is 5 × 1000 ≈ 5,000 records/sec, so a million rows is a few minutes of
wall clock plus API response time.

The OpenAPI spec documents no server-side rate limits and no 429 response, so there is
no published ceiling to aim at: this client-side control is the real throttle and the
429 handling is defensive. If you do not know what the tenant tolerates, start at
`--rps 2` and watch the log for retries.

### 2. OAuth client credentials, rolling

`POST https://{tenant}.aprimo.com/login/connect/token`, `grant_type=client_credentials`,
scope `api`. Note the token host has **no `.dam`** segment — the API host does.
Both are derived from `Aprimo.Tenant`.

Worth verifying per tenant against its OpenID discovery document, which is public and
unauthenticated:

```
https://{tenant}.aprimo.com/login/.well-known/openid-configuration
```

On a tenant checked during development it confirmed the derived `token_endpoint`, that
`client_credentials` is a supported grant, that the `api` scope exists, and that both
`client_secret_basic` and `client_secret_post` are accepted. The same path on the `.dam`
host returns 404, so the identity server is genuinely not there.

The registration must have its **OAuth Flow Type set to Client Credential** — a PKCE
registration is a public client with no secret, so presenting one is rejected as
`invalid_client` with no more specific explanation.

- Cached until within `RefreshSkewSeconds` (default 120s) of expiry, then renewed.
- Concurrent callers during a renewal are single-flighted — one token request, not many.
- An unexpected 401 invalidates the cache and retries once, covering revocation and
  tenant-side rotation mid-export.
- Token failures map to actionable messages (`invalid_client` points at
  Administration > Integration > Registrations, including the up-to-15-minute
  propagation delay).

Four ways to supply the client ID and secret, in ascending precedence:

| Source | How | Notes |
|---|---|---|
| Encrypted store | `--save-credentials` once, then nothing | **Recommended.** DPAPI, current user + machine. Fills gaps only. |
| Config file | `Aprimo.ClientId` / `Aprimo.ClientSecret` | Plaintext in the project folder — commit risk. |
| Environment | `APRIMO_CLIENT_ID` / `APRIMO_CLIENT_SECRET` | Good for unattended runs. Values are trimmed. |
| Command line | `--client-id` / `--client-secret` | Visible in process list and shell history. |
| Interactive prompt | automatic when missing, or `--prompt-credentials` / `--login` | Nothing persisted. |

Explicit sources override earlier ones, which means **the environment silently wins over
the config file** — see the precedence trap above. The encrypted store is the exception:
it only supplies values nothing else provided, so it can never shadow an explicit one.

The store is written with write-then-replace, so an interrupted save cannot corrupt an
existing store. An undecryptable file (copied from another machine or account) is
reported with an explanation rather than crashing.

The prompt masks the secret, supports backspace, cancels on Escape, and **saves
nothing** — values live in process memory only. It writes to stderr so stdout stays
clean. It is skipped entirely when stdin is redirected (CI, scheduled tasks, pipes),
so an unattended run fails fast instead of hanging on an invisible prompt.

```bash
dotnet run --project src/AprimoExport -- --prompt-credentials --validate-only
```

Note that command-line flags are visible in the process list and shell history —
prefer the prompt or environment variables for the secret.

### 3. Configurable metadata fields

Each CSV column is a `{ Column, Path }` pair. Path grammar:

| Syntax | Meaning |
|---|---|
| `a.b` | walk objects |
| `[0]` | array index |
| `[*]` | all elements, joined by `MultiValueSeparator` |
| `[key=value]` | array elements whose property matches |
| `field:Name` | shorthand for `fields.items[fieldName=Name].localizedValues[*].value` |
| `field:Name@1033` | pin one language |
| `field:Name@neutral` | the language-neutral ID (`0000…0000`) |
| `a \|\| b \|\| c` | fallback branches — first one that yields a value wins |

### The field list file

The column list lives in [dist/fields.txt](dist/fields.txt), not in `appsettings.json`
— set by `FieldsFile`, overridable per run with `--fields-file` / `-f`. Edit that file
to change what the CSV contains; delete a line to drop a column.

Field names are separated by **newlines, commas, semicolons or tabs**, mixed freely, so
a list pasted from a spreadsheet cell works as well as one-per-line:

```
Brand, Alt Text, Product Family
Keywords
RecordId => id
Owner => createdBy.name || createdBy.userName
```

| Form | Result |
|---|---|
| `Brand` | column `Brand`, path `field:Brand` |
| `Alt Text` | column `Alt_Text`, path `field:Alt Text` |
| `Approved?` | column `Approved`, path `field:Approved?` |
| `Column => path` | explicit mapping, for record properties or custom paths |
| `#` or `//` | comment — **only** at line start or after whitespace |

Column headers follow Aprimo's own `outputName` rule: whitespace becomes `_`, and
anything outside `[A-Za-z0-9_]` is dropped. Verified against a production 148-field export
schema, reproducing every `outputName` exactly.

Pipe is deliberately **not** a separator, because `||` denotes fallback branches inside
a path.

The comment rule is narrow for a reason: real field names contain `#`. A set like
`Photo#1` through `Photo#4` would otherwise all truncate to `Photo` and collapse into
duplicate columns.

An **Aprimo Data Exports schema XML** file is also accepted directly — point
`FieldsFile` at the exported `<exportSourceConfiguration>` file and it reads
`outputName` / `fieldName` / `propertyPath`
with no conversion step. Both paths are verified to produce identical columns.

Resolution order for a relative `FieldsFile`: current directory, then beside the config
file, then beside the executable.

`CreatedBy` and `ModifiedBy` use fallback branches — the OpenAPI spec types them as
bare `object` with no properties defined, so the real shape is unknown until we see a
live response:

```
createdBy.name || createdBy.fullName || createdBy.userName || createdBy.id
```

`--sample` shows which branch actually hits; pin it to the single correct path
afterwards.

```json
{ "Column": "FileName", "Path": "masterFileLatestVersion.fileName" },
{ "Column": "Brand",    "Path": "field:Brand" },
{ "Column": "AllFieldNames", "Path": "fields.items[*].fieldName" }
```

`GET /fielddefinitions` lists the field names available in your tenant.

### Testing a mapping against one known record

`--record <id>` fetches exactly one record via `GET /record/{id}` — no expression, filter
or paging involved. The fastest way to check how a specific field actually comes out:

```bash
aprimo-export.exe --record a23bf4d7-3f3c-490c-8ed0-a9d8004d0f9e --sample
```

That prints the per-column resolution report for that one record. Drop `--sample` to write
it as a single-row CSV instead, which is the way to see the exact cell contents:

```bash
aprimo-export.exe --record a23bf4d7-3f3c-490c-8ed0-a9d8004d0f9e --prefix one-record --overwrite
```

Hyphenated GUIDs are normalised automatically. Aprimo returns and expects bare
32-character hex (`a23bf4d73f3c490c8ed0a9d8004d0f9e`), but the same ID is usually
*displayed* hyphenated — passing that through unchanged would 404 with no clue why.
Braces and whitespace are stripped too.

This is also the endpoint where the `select-record` headers are officially documented, so
it is the most reliable place to confirm which sub-resources a record exposes.

### Multi-value fields

A list field (`ListString`, `OptionList`, `ClassificationList`, …) returns several entries
under `localizedValues`. All of them are exported, joined by `Output.MultiValueSeparator`
(default `|`) into one cell:

| Path | Result for a 3-value `AssetCategory` |
|---|---|
| `field:AssetCategory` | `Appliances\|Kitchen\|Refrigerators` |
| `fields.items[fieldName=AssetCategory].localizedValues[0].value` | `Appliances` |
| `fields.items[fieldName=AssetCategory].localizedValues[1].value` | `Kitchen` |

So you can have one joined column, or split the first N values across separate columns —
whatever the downstream consumer expects. Values are never dropped or truncated.

**Separator collisions are reported.** If a value itself contains `|`, the joined cell
cannot be split reliably. The value is still exported in full, and the export summary
names the affected columns:

```
Warning: 2 multi-value column(s) contained a value that itself includes '|',
so those cells cannot be split reliably: DirtyList, Keywords
```

Change `Output.MultiValueSeparator` to something the data does not use if that happens.

Note that Aprimo often pairs a list-type field with an `…AsText` companion
(`KeywordsAsText`, `StyleAsText`, `SubjectDetailsAsTextList`) — Aprimo's own flattened
string form, which may suit a CSV better than the raw list.

### The `select-record` headers

Metadata only comes back if these are sent, so every page request carries them
(see `ApplySelectHeaders` in [PagedRecordReader.cs](src/AprimoExport/Export/PagedRecordReader.cs)).
The spec spells them **singular** — `select-record`, not `select-records`.

| Header | Config | Default |
|---|---|---|
| `select-record` | `Source.SelectRecord` | `fields,masterfilelatestversion` |
| `select-record-fields` | `Source.SelectRecordFields` | auto-derived from the loaded column list |
| `select-record-fieldgroups` | `Source.SelectRecordFieldGroups` | not sent |
| any cascade header | `Source.AdditionalSelectHeaders` | none |

`select-record-fields` is filled automatically from the field names your mappings
reference, so the API returns only the metadata you actually use — a large bandwidth
saving on a wide schema. It disables itself if any mapping enumerates fields
dynamically (`fields.items[*]`), since narrowing would then drop data. Set the value
explicitly to override.

Cascade headers go in `AdditionalSelectHeaders` — e.g. to reach into the embedded
file version:

```json
"AdditionalSelectHeaders": { "select-fileversion": "renditions" }
```

The API caps any single resource type at 2 occurrences in an embedding chain.

### 4. Max records per file

Output rolls to `{prefix}_0001.csv`, `_0002.csv`, … every `MaxRecordsPerFile` rows.
The header is repeated in every part so each file stands alone. `0` = never roll.

Writes are buffered (1 MB by default) and flushed at page boundaries.

### 5. Total row cap

`MaxTotalRecords` stops the export at exactly N rows. The final request also shrinks
its `take` to the remaining budget, so a `--max-rows 500` demo fetches 500 records
rather than a full 1000-record page and discarding half.

## CSV details

- RFC 4180 quoting; values containing the delimiter, quotes, newlines, or edge
  whitespace are quoted, and embedded quotes doubled.
- UTF-8 with BOM by default so Excel detects the encoding on double-click.
- **Formula injection guard**: values starting with `= + - @` get a leading apostrophe
  so spreadsheets do not evaluate them. Numbers are exempt, so `-42` stays numeric.
- Objects or arrays landing at a leaf are emitted as raw JSON in the cell.

## Paging: pick the right mode for the volume

| Mode | Mechanism | Use when |
|---|---|---|
| `Offset` | `skip` / `take` | Default. Simple, fine to moderate depth. |
| `PageNumber` | `page` / `pageSize` | Equivalent to Offset. |
| `Keyset` | ascending watermark in the search expression, `skip` always 0 | **Millions of rows.** |

Page size is capped at **1000** by the API (`RecordCollection.limit` has
`maximum: 1000`).

Default sort is `ModifiedOn` ascending (`Source.Sort`); prefix with `-` for descending.
Descending is riskier mid-export: freshly modified records jump to page 1 and shift
every offset behind them.

**Keyset watermark is `CreatedOn`, on purpose.** In Keyset mode `KeysetProperty`
overrides `Source.Sort`, and a watermark has to be immutable — if a record is edited
mid-export, a `ModifiedOn` watermark moves past itself and that record is exported
twice. `CreatedOn` never moves. Use `ModifiedOn` there only for a delta export against
a quiet tenant.

**Why Keyset matters.** The spec says `totalCount` is "approximated due to two-step
Elasticsearch + database querying". Elasticsearch-backed search normally refuses deep
`skip` values (commonly past ~10,000). The spec documents no ceiling, so this is
unconfirmed — but if an Offset export dies or slows sharply at a consistent depth,
that is the cause. Keyset sorts ascending on `CreatedOn` (configurable) and advances
a `>=` watermark in the expression each page, so the offset never grows.

Keyset handles ties on the watermark by tracking record IDs sitting exactly on it and
filtering the overlap. If more records share a single timestamp than fit in one page
it cannot advance, and it fails with an explicit message rather than looping — switch
to a higher-cardinality watermark or a larger page size.

Keyset requires `Source.Mode = Search`, since it rewrites the search expression.

## Resume

With `Limits.EnableCheckpoint`, a checkpoint is written after each completed page
(atomically, via write-then-replace). `--resume` continues from it.

- Resume is page-granular, and checkpoints are written only after a page is fully
  written and flushed — so no duplicate and no dropped rows.
- Resumed rows start a **new** part file rather than appending to a partial one, so
  parts may be uneven after a resume.
- The checkpoint records a fingerprint of the query, paging settings, and column
  mappings. Change any of them and the checkpoint is refused rather than producing a
  mixed output set.

Ctrl+C stops after the current page and flushes; a second Ctrl+C aborts immediately.

## Layout

```
src/AprimoExport/
  Program.cs                        CLI, plan banner, summary
  appsettings.json                  annotated configuration
  fields.txt                        the CSV column list
  Configuration/
    ExportConfig.cs                 config model + validation
    ConfigLoader.cs                 file < env < CLI, arg parsing, --help
    CredentialPrompt.cs             masked interactive credential entry
    FieldListLoader.cs              delimited field list + Aprimo schema XML
  Auth/
    OAuthTokenProvider.cs           client credentials, caching, single-flight renewal
    CredentialStore.cs              DPAPI-encrypted credential store, keyed by tenant
  Http/
    RateLimiter.cs                  token bucket + concurrency cap + 429 backpressure
    ApiClient.cs                    auth injection, retry/backoff, error messages
  Export/
    FieldExtractor.cs               path grammar, compilation, projection
    CsvRollingWriter.cs             RFC 4180, rolling parts, injection guard
    PagedRecordReader.cs            Offset / PageNumber / Keyset paging
    DeltaWindow.cs                  incremental windows + persisted high-water mark
    SearchExpressions.cs            shared expression formatting and composition
    ExportRunner.cs                 orchestration, caps, progress, checkpoints
docs/api-notes.md                   what the OpenAPI spec actually says
tests/AprimoExport.Tests/           self-contained test runner, no framework needed
```

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Runtime failure (auth, API, I/O) |
| 2 | Invalid configuration or arguments |
| 3 | Cancelled — re-run with `--resume` |

## Open items

1. **Scope the tenant first.** `--sample` reports the approximate total. Anything past a
   few hundred thousand records needs `--paging Keyset` for a full export — see
   [Full backfill](#full-backfill). Daily deltas are unaffected by size.
2. **`CreatedBy` / `ModifiedBy` branch not pinned.** The OpenAPI spec types both as bare
   objects with no properties, so the shipped mapping tries
   `name || fullName || userName || id` and takes the first that resolves. It works, but
   which branch hit is not reported. Pinning it to the single correct path for your tenant
   would make a future API change surface as an empty column rather than silently sliding
   to another property.
3. **Sample widely before trusting a field list.** A 5-record sample will leave most
   columns blank simply because those records have no value there — it does not
   distinguish "genuinely empty" from "wrong field name". `--sample 1000` is still one
   request and covers far more.
4. **Keyset watermark formatting is unverified.** The spec example shows dates
   unquoted (`CreatedOn >= 2025-01-01`); we emit a full unquoted ISO-8601 timestamp.
   Confirm against a live tenant before relying on a keyset backfill.
5. **`totalCount` is approximate** — the spec says so explicitly — and is used only for
   ETA. Do not reconcile row counts against it.
6. **Classifications are out of scope.** `classifications`, `files`, `permissions` and
   `locks` come back null from the collection endpoints. Some tenants expose a
   `Classification Path` metadata field, which avoids needing them; otherwise this would
   require a per-record `GET /record/{id}` call.

## Incremental (delta) export

Aprimo's own Data Exports have a `ChangedRecordsExportSource`, so this mirrors that
pattern. The standing daily run is:

```bash
aprimo-export.exe --daily --prefix dam-daily
```

`--daily` is shorthand for `--since last`: export everything changed since the previous
successful run. The first run has no mark to resume from, so seed it once:

```bash
aprimo-export.exe --since 1d --prefix dam-daily
```

| `--since` / `--until` | Meaning |
|---|---|
| `last` | Resume from the saved mark (`--daily`) |
| `yesterday` | The whole previous UTC day |
| `today` | UTC midnight to now |
| `1d`, `36h`, `90m` | Rolling span back from now |
| `2026-08-05` | That single UTC day |
| `2026-08-05T04:00:00Z` | Explicit instant — use this for local-midnight boundaries |
| `now` | Valid for `--until` |

`--until` defaults to this run's start time. `--reset-delta` forgets the saved mark.

### How it avoids gaps and duplicates

Windows are **half-open `[since, until)` and chained** — the next run starts exactly
where the previous one ended. That is gap-free by construction and immune to records
being edited mid-run: those land in the following window. Deriving the next start from
the highest `ModifiedOn` actually seen would instead lose anything stamped below that
maximum while the run was in flight.

**The mark advances only after a run that completes without cancellation and without
hitting the row cap.** A partial run leaves the mark alone, so re-running finishes the
window rather than skipping the remainder. Cancel a delta run with Ctrl+C and the next
`--daily` picks up the same window.

`Source.Delta.OverlapMinutes` (default 5) re-reads a little of the previous window on
resume. Chaining is exact against *our* clock; this covers the remaining risk that the
API stamps `ModifiedOn` from a clock slightly behind ours, which would otherwise drop
records into a window already closed. It costs a few duplicate rows per run — set it to
`0` for exact adjacency if both clocks are known to be tight. **Duplicates are possible
by design; deduplicate on `RecordId` downstream if that matters.**

State lives in `delta-state.json` in the output directory, keyed by tenant + query, so
changing the search expression starts a fresh lineage instead of silently reusing an
unrelated mark. It records the mark, run count and row totals.

### Timezone

All boundaries are **UTC**, because the API's `ModifiedOn` is UTC — Aprimo's export schema
maps it from `ModifiedOnUtc`. So `--since yesterday` is the UTC day, which in a UTC-5 zone
runs 7pm to 7pm local.

If the downstream consumer thinks in local time, pass explicit instants offset by your own
zone rather than relying on `yesterday`:

```bash
aprimo-export.exe --since 2026-08-05T04:00:00Z --until 2026-08-06T04:00:00Z
```

Decide this before the job becomes recurring — the two interpretations differ by several
hours of records, and switching later leaves a gap or an overlap.
