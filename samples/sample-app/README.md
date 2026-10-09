# Sample app

The application the MVP deploys and qualifies: an ASP.NET Core minimal API that reaches Cosmos DB **only through
its managed identity** (`DefaultAzureCredential`; no key, no connection string) and whose `/health` endpoint is the
operational-qualification probe.

| Endpoint | Behaviour |
|---|---|
| `GET /health` | 200 only if the app read its Cosmos container with its identity (bounded by a 5 s timeout). Otherwise 503 with a `reason`, e.g. `Cosmos DB denied access (403): the identity has no grant yet`. That is how a deploy tells "grants not yet propagated" from "working". |
| `POST /items` | Body `{"text": "..."}`; stores a document, returns 201 and its location. |
| `GET /items/{id}` | Returns the document, or 404. |

**Startup contract ([ADR 0012](../../docs/decisions/0012-system-assigned-identity.md)).** The identity has no access
when the process starts. The app starts anyway and answers 503 until the grant lands; it never crashes on an auth
failure and caches no failure: after any error the Cosmos client is dropped and the next request starts clean.

## Configuration

Environment variables only. The workload definition ([sample-app.yaml](../workloads/sample-app.yaml)) supplies the first
three from the `database` exports; the platform's `enforce-monitoring` policy supplies the last.

| Variable | Meaning |
|---|---|
| `COSMOS_ENDPOINT`, `COSMOS_DATABASE`, `COSMOS_CONTAINER` | Where the data lives. The container's partition key must be `/id` (see the `database` mapping). |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | When set, telemetry goes to Azure Monitor; when absent, nothing is exported. |

## Run locally

```bash
dotnet run --project samples/sample-app            # /health answers 503 "not configured"
COSMOS_ENDPOINT=https://<account>.documents.azure.com:443/ COSMOS_DATABASE=<db> COSMOS_CONTAINER=<container> \
  dotnet run --project samples/sample-app          # needs `az login` as an identity with a Cosmos data role
```

Tests (`samples/sample-app.tests`) run offline against a fake store: `dotnet test samples/sample-app.tests`.

## Publish the image

```bash
samples/sample-app/publish.sh
```

Builds the multi-stage `linux/amd64` image (non-root, port 8080), logs in to GHCR with the existing `gh` login
(`write:packages` scope; the token is piped, never printed), pushes
`ghcr.io/<owner>/just-deliver-sample-app:<git short sha>` and prints the image reference and **digest**. Deployments
should pin the digest. GHCR packages start private: make the package public in GitHub (Package settings, Change
visibility) so Container Apps can pull without credentials.

The script refuses a working tree with uncommitted changes (the tag must match what was built) and runs
`docker logout ghcr.io` on exit so the `gh` token is not left in `~/.docker/config.json` — this also ends any GHCR
login you had before running it.
