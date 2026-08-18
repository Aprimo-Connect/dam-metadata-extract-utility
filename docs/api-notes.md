# Aprimo DAM REST API — facts driving this utility

Extracted from the Aprimo DAM REST API OpenAPI document (v1, OpenAPI 3.0.1).

> Note: the copy this was taken from had three trailing lines of Adobe Acrobat UI text
> ("Adobe Acrobat", "Summarize this", "Ask AI Assistant") after the closing `}`
> at line 6580. That is copy/paste noise, not part of the spec; it breaks a
> strict JSON parse. Strip it if you re-parse the file.

## Hosts (two different ones — easy to get wrong)

| Purpose | URL |
|---|---|
| API | `https://{tenant}.dam.aprimo.com/api/core` |
| OAuth token | `https://{tenant}.aprimo.com/login/connect/token` |

The token host has **no** `.dam` segment.

## Auth

- `clientCredentials` flow, scope **`api`**.
- Register under Administration > Integration > Registrations.
- Spec warns: registration changes can take **up to 15 minutes** to take effect.
- Access token is a JWT, sent as `Authorization: Bearer <token>`.

## Required / notable headers

| Header | Required | Notes |
|---|---|---|
| `API-VERSION` | **yes** | Must be `1`. |
| `Authorization` | yes | `Bearer <jwt>` |
| `Accept` | recommended | `application/json` for flat JSON. `application/hal+json` returns HAL with `_links`/`_embedded` — we use flat JSON. |
| `languages` | no | Single language ID, comma-separated list, or `*` for all. Controls localized field values. |

## Sub-resource embedding (`select-*`)

`select-record: fields,masterfilelatestversion` embeds sub-resources inline;
without it those properties come back `null`. Related narrowing headers:

- `select-record-fields` — comma-separated field names/IDs to return. **Big payload win on large exports.**
- `select-record-fieldgroups` — restrict to field groups.
- `select-fileversion`, `select-file`, `select-classification`, `select-user` — cascade into embedded objects.
- The API limits any single resource type to **2 occurrences** in an embedding chain (recursion guard).

**Caveat:** the spec declares `select-*` headers only on the single-resource
`GET /record/{recordId}`, *not* on `GET /records` or `POST /search/records`.
However the documented 200 examples for **both** collection endpoints show
`fields.items[]` and `masterFileLatestVersion` fully populated, while
`classifications`, `files`, `permissions`, and `locks` are `null`. So metadata
fields do come back from collection calls. We send the `select-*` headers on
collection requests anyway — harmless if ignored, and correct if honoured.
`Source.VerifyFieldsOnFirstPage` fails fast if `fields` comes back null while
field mappings need it.

## Endpoints used

### `POST /search/records` → `SearchResponse` (default)
Body `RecordSearchRequest`: `searchExpression`, `facets` (max 12), `logRequest`, `userQuery`.
Query: `page`, `pageSize`, `skip`, `take`, `sort`.

Search expression language (`SearchExpression.expression`):
- Operators `=`, `<>`, `>`, `>=`, `<`, `<=`, `CONTAINS`, `in`; logical `AND`, `OR`, `NOT`.
- Reference metadata via `FieldName("Brand")` or `FieldId("guid")`.
- `?` positional params (`parameters`), `@name` named params (`namedParameters`).
- `supportWildcards` enables `*` / `?` inside quoted strings.
- Examples: `Status = 'Released'`, `Title CONTAINS 'umbrella'`, `FieldName("Brand") = 'Acme' AND CreatedOn >= 2025-01-01`.

### `GET /records` → `RecordCollection`
Query: `page`, `pageSize`, `skip`, `take`, `filter`, `sort`.

### Paging
Two interchangeable styles on both endpoints: `page`+`pageSize` (1-based) or `skip`+`take`.
**`RecordCollection.limit` has `maximum: 1000`** → page size ceiling is 1000.
`sort` takes a field name, `-` prefix for descending.

### `totalCount` is approximate
Both schemas say so explicitly — "approximated due to two-step Elasticsearch +
database querying". Treat it as an ETA input only, never as a loop bound or a
reconciliation target.

## Record shape (what the CSV maps from)

```
id, status (Active|Released|Archived), contentType, title, tag, textContent,
aiInfluenced (No|Yes|Unknown), hasImageOverlay, createdOn, modifiedOn,
createdBy{}, modifiedBy{},
fields.items[] -> RecordField
masterFileLatestVersion -> FileVersion
masterFileLatestPublishedVersion -> FileVersion
classifications{}, files{}, permissions{}, locks{}
preview{size,width,height,extension,uri}    <- select-record: preview
thumbnail{size,width,height,extension,uri}  <- select-record: thumbnail
```

`preview.uri` / `thumbnail.uri` are **time-limited signed URLs (~30 h)** — fine
for a CSV snapshot, but they expire.

### `RecordField`
`id`, `fieldName` (internal name used in API calls), `label`, `dataType`
(SingleLineText, MultiLineText, DateTime, Numeric, Boolean, ListString, …),
`localizedValues[] -> LocalizedValue`, `inheritanceState`, `inheritable`.

### `LocalizedValue`
`value`, `languageId`, `aiInfluenced`, `readOnly`, `modifiedOn`.
Language-neutral fields use languageId `00000000000000000000000000000000`.

### `FileVersion`
`id`, `versionLabel`, `versionNumber`, `fileName`, `fileSize` (bytes),
`fileExtension`, `fileCreatedOn`, `fileModifiedOn`, `isLatest`,
`fileProcessingState` (Open|Processing|Failed), `fileState` (Available|Deleted|Archived),
`aiInfluenced`, `preventDownload`, `watermarkId`, `watermarkType`, `crc32`,
`comment`, `createdOn`.

## Not in the spec

- **No documented rate limits, quotas, or 429 responses.** Declared responses are
  200/201/401/404 only. Client-side throttling is therefore our control; we still
  handle 429 + `Retry-After` defensively.
- **No documented deep-paging ceiling.** Elasticsearch-backed search usually caps
  `skip` depth (commonly ~10k), and the approximate `totalCount` hints at ES
  involvement. This is the main open risk for multi-million-row exports — hence
  the `Keyset` paging mode. Verify against a real tenant.
