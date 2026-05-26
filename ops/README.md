# Operations

This folder contains the artefacts required to run SI "Protecția Socială" locally and on
MCloud.

| File | Purpose |
|------|---------|
| `Dockerfile.api` | Multi-stage build for the REST API host. |
| `Dockerfile.web` | Multi-stage build for the Blazor frontend. |
| `docker-compose.yml` | Local + staging stack (Postgres, MinIO, API, Web). |
| `.env.example` | Sample environment variable file. Copy to `.env` for local use. |

## Local quickstart

```bash
cd ops
cp .env.example .env
docker compose up --build
```

The API listens on `http://localhost:8080` (health: `/health`), the Web app on
`http://localhost:8081`, MinIO console on `http://localhost:9001`.

## MCloud deployment notes

- Container images are pushed to the MCloud registry per the AGE conventions.
- Secrets (`POSTGRES_PASSWORD`, `MINIO_*`, `MGov:*ClientSecret`) are injected by the
  MCloud secret store — they must never appear in repository config.
- TLS termination happens at the MCloud ingress; the application itself listens on plain
  HTTP inside the trust boundary.
- Postgres is provided by MCloud's managed Postgres; backups follow SEC 060.
- MinIO buckets `cnas-citizen-uploads` and `cnas-generated` are created on first start.
