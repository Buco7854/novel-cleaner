# 📚 Tergeo

A multi-user web application that strips watermarks, tracking codes, distributor-inserted boilerplate, and other non-book content from EPUB files using an LLM.

- **Backend** — ASP.NET Core 10 (Identity, EF Core + SQLite, SignalR)
- **Frontend** — React 18 + TypeScript + Tailwind CSS + Headless UI
- **Auth** — local password + OIDC (auto-provision, optional group → role mapping)
- **OPDS sources** — connect Calibre / Standard Ebooks / your own catalog and import books in one click
- **Background processing** — uploads clean in the background; live logs and progress stream over SignalR
- **Storage** — SQLite DB in `/data`, uploaded books and cleaned outputs in `/books` (separate volumes)
- **Drop folder** — optional auto-copy of cleaned EPUBs to a folder of your choice (e.g. `/bookdrop`)
- **Container** — single multi-arch image (amd64 + arm64), published to GHCR

## Quick start

### Run from source
```bash
# server (terminal 1)
cd server
dotnet run

# client (terminal 2)
cd client
npm install
npm run dev
```
The Vite dev server proxies `/api` and `/hubs` to the .NET backend.

### Run with Docker
```bash
cp .env.example .env
# edit .env — at minimum set TERGEO_FIRST_ADMIN_PASSWORD
docker compose up -d
# open http://localhost:8080
```

`.env` is gitignored. Sensitive values (admin password, OIDC client secret) live there, never in `docker-compose.yml`.

### Run the tests
```bash
dotnet test
```
The xUnit suite under `tests/Tergeo.Server.Tests/` covers the EPUB scanner, removal pipeline, and the drop-folder helper.

## Configuration

There are **two layers** of environment variables — keep the distinction in mind:

1. **`.env` variables** (`TERGEO_*`) are friendlier names that **only the `docker-compose.yml` reads**. They are mapped to the actual ASP.NET Core configuration paths inside the compose file.
2. **App environment variables** are what the binary itself reads. They follow the standard ASP.NET Core convention (`__` for nested keys, e.g. `Auth__Password__Enabled`). If you run the app **without docker-compose** (bare `dotnet run`, Kubernetes, systemd, etc.), set these directly.

The tables below list both sides for every option.

### Storage

| `.env` variable | App env var | Default | Purpose |
| --- | --- | --- | --- |
| `TERGEO_DATA_DIR` | `Storage__DataDirectory` | `/data` | SQLite DB and Data Protection keys |
| `TERGEO_BOOKS_DIR` | `Storage__BooksDirectory` | `/books` | Uploaded EPUBs and cleaned outputs |
| `TERGEO_MAX_UPLOAD_BYTES` | `Storage__MaxUploadBytes` | 209715200 (200 MB) | Per-file upload limit |
| — | `ConnectionStrings__Default` | `Data Source={DataDirectory}/tergeo.db` | Override SQLite path entirely |

### Local password auth

| `.env` variable | App env var | Default | Purpose |
| --- | --- | --- | --- |
| `TERGEO_PASSWORD_AUTH_ENABLED` | `Auth__Password__Enabled` | `true` | Allow username/password login. Set to `false` for SSO-only. |
| `TERGEO_FIRST_ADMIN_EMAIL` | `Auth__FirstAdminEmail` | _(empty)_ | Optional bootstrap admin email |
| `TERGEO_FIRST_ADMIN_PASSWORD` | `Auth__FirstAdminPassword` | _(empty)_ | Optional bootstrap admin password |

> **First-run flow**: if either bootstrap variable is empty (the default), the app shows a one-time setup page where you create the admin account interactively. Set BOTH to seed the admin automatically — useful for unattended deployments. Once any admin exists, the setup page is no longer reachable.

> **Lockout guard**: if password auth is disabled but OIDC isn't usable (no `Enabled=true`, `ClientId`, or discovery info), the app force-enables password auth at startup and prints a `[WARN]` to stderr — so you can never ship an unreachable instance.

### OPDS

| `.env` variable | App env var | Default | Purpose |
| --- | --- | --- | --- |
| `TERGEO_OPDS_ALLOW_PRIVATE_NETWORKS` | `Opds__AllowPrivateNetworks` | `false` | Allow OPDS connections to private/loopback/link-local IPs. Set to `true` for self-hosted OPDS servers on your LAN; otherwise leave off to keep the SSRF guard active. |

### OIDC discovery — pick one mode

There are really just **two paths**: let the app discover the endpoints automatically (almost always what you want), or hand-wire every endpoint yourself.

**Discovery (recommended)** — set exactly **one** of `Authority` or `MetadataAddress`:

- `Authority` is the issuer URL **without** any `.well-known/...` suffix. The app appends `/.well-known/openid-configuration` for you. Use this 99% of the time.

  ```
  # Keycloak
  TERGEO_OIDC_AUTHORITY=https://keycloak.example.com/realms/main

  # Authentik
  TERGEO_OIDC_AUTHORITY=https://authentik.example.com/application/o/tergeo/

  # Google
  TERGEO_OIDC_AUTHORITY=https://accounts.google.com

  # Microsoft Entra ID
  TERGEO_OIDC_AUTHORITY=https://login.microsoftonline.com/<tenant-id>/v2.0
  ```

  `# → Auth__Oidc__Authority`

- `MetadataAddress` is the **full discovery URL, including `.well-known/openid-configuration`**. Only set this when your IdP exposes its discovery doc somewhere other than `{Authority}/.well-known/openid-configuration` — otherwise stick with `Authority`.

  ```
  TERGEO_OIDC_METADATA_ADDRESS=https://idp.example.com/custom/path/.well-known/openid-configuration
  ```

  `# → Auth__Oidc__MetadataAddress`

> Quick rule: if the URL ends in `.well-known/openid-configuration`, it goes in `MetadataAddress`. If it doesn't, it goes in `Authority`.

**Manual endpoints (advanced)** — no discovery round-trip. Use only if your IdP doesn't publish a discovery document:

```
TERGEO_OIDC_ISSUER=https://idp.example.com/                        # → Auth__Oidc__Issuer
TERGEO_OIDC_AUTHORIZATION_ENDPOINT=https://idp.example.com/authorize
TERGEO_OIDC_TOKEN_ENDPOINT=https://idp.example.com/token
TERGEO_OIDC_USERINFO_ENDPOINT=https://idp.example.com/userinfo
TERGEO_OIDC_JWKS_URI=https://idp.example.com/.well-known/jwks.json
TERGEO_OIDC_END_SESSION_ENDPOINT=https://idp.example.com/logout    # optional
```

### OIDC — common settings

| `.env` variable | App env var | Default | Purpose |
| --- | --- | --- | --- |
| `TERGEO_OIDC_ENABLED` | `Auth__Oidc__Enabled` | `false` | Set to `true` to enable OIDC sign-in |
| `TERGEO_OIDC_CLIENT_ID` | `Auth__Oidc__ClientId` | _(none)_ | OAuth client ID |
| `TERGEO_OIDC_CLIENT_SECRET` | `Auth__Oidc__ClientSecret` | _(none)_ | OAuth client secret (confidential clients only) |
| `TERGEO_OIDC_DISPLAY_NAME` | `Auth__Oidc__DisplayName` | `single sign-on` | Provider name shown on the login button (e.g. "Authentik", "Okta") |
| `TERGEO_OIDC_AUTO_PROVISION` | `Auth__Oidc__AutoProvision` | `true` | Create local user records on first sign-in |
| — | `Auth__Oidc__Scopes__0`, `__1`, … | `openid profile email` | Override the requested scope list (numbered indices) |

### OIDC — group → role mapping (optional)

Every authenticated OIDC user automatically gets the **User** role. Two opt-in groups can additionally grant elevated capabilities:

| `.env` variable | App env var | Behaviour when empty |
| --- | --- | --- |
| `TERGEO_OIDC_GROUPS_CLAIM` | `Auth__Oidc__GroupsClaim` | Default `groups`. Only consulted when at least one mapping group is configured. |
| `TERGEO_OIDC_ADMIN_GROUP` | `Auth__Oidc__AdminGroups__0` | Admin role is **not managed** by OIDC. Manual promotions persist. |
| `TERGEO_OIDC_DROP_GROUP` | `Auth__Oidc__DropFolderGroups__0` | `DropFolder` role is **not managed** by OIDC. Combined with `DefaultDropFolder=false`, this means only admins can use the drop folder. |

To map several IdP groups to a single role, set numbered indices directly: `Auth__Oidc__AdminGroups__0=admins`, `Auth__Oidc__AdminGroups__1=ops`, etc.

### Permissions

| `.env` variable | App env var | Default | Purpose |
| --- | --- | --- | --- |
| `TERGEO_DEFAULT_DROP_FOLDER` | `Auth__DefaultDropFolder` | `true` | When `true`, every authenticated user has permission to use the drop folder. When `false`, only admins and users with the `DropFolder` role do (granted via `TERGEO_OIDC_DROP_GROUP` or directly in the DB). |

The drop folder permission is checked **at the time the cleanup runs**:
- If the book's owner has it → the cleaned EPUB is auto-copied at the end AND the `Copy to drop folder` button is shown on the editor page.
- If they don't → the auto-copy is silently skipped (with a `info` line in the book's log) and the button is hidden.

Admins always have the permission regardless of any setting.

### In-app settings — two scopes

The Settings page is split into two sections with very different semantics:

**Global (admin-managed)** — shared by everyone, editable by admins, read-only for everyone else:

| Setting | Purpose |
| --- | --- |
| LLM API key, base URL, model | Any OpenAI-compatible chat completions endpoint |
| Parallel requests | LLM calls in flight concurrently per job (1–10). Caps cost / rate-limit usage. |
| System prompt — additional instructions | Appended to the locked output-format prompt |
| **Drop folder** | Optional absolute server path. When set AND the book's owner has the DropFolder permission, every cleaned EPUB is auto-copied as `{name}_cleaned.epub`. Existing files are never overwritten — duplicates get ` (1)`, ` (2)`, … suffixes. Failures are logged as warnings without failing the cleanup. |

**Personal (per-user)** — every user manages their own:

| Setting | Purpose |
| --- | --- |
| System prompt addition | Personal instructions appended after the admin's prompt at every LLM call (e.g. "preserve em-dashes", "this book is in French") |

The Drop folder also has a **manual re-trigger** button on the editor page that copies the cleaned output again using the *current* drop folder setting, so you can re-route after the fact. The button is only shown when the book's owner has the DropFolder permission.

See `.env.example` for the full template and `docker-compose.yml` for the wiring.

## Architecture

```
client/   React + Tailwind + Headless UI single-page app
          └─ Vite-built static assets are served by the .NET app from wwwroot
server/   ASP.NET Core 10 Web API (Tergeo.Server)
          ├─ EF Core + SQLite (DB at /data/tergeo.db)
          ├─ ASP.NET Core Identity (cookie auth)
          ├─ OIDC handler (auto-provisioning + optional group mapping)
          ├─ Background BookProcessor — Channels-based queue (BookProcessingQueue)
          ├─ SignalR BookHub — pushes log lines, status, and per-page progress
          ├─ BookEditorRepo — one git repo per book, file per page, edit history as commits
          ├─ BookImporter / BookFinalizer — EPUB → editor repo / editor repo → cleaned EPUB
          ├─ EPUB pipeline — AngleSharp + System.IO.Compression
          ├─ OPDS client — Atom feed parser + downloader (with SSRF guard)
          └─ DropFolderHelper — shared drop-folder copy logic (auto + manual)
tests/    xUnit test project covering pure helpers
```

## Reverse proxy notes

The container expects `X-Forwarded-Proto`, `X-Forwarded-For`, and `X-Forwarded-Host` from any reverse proxy in front of it (Traefik, Caddy, nginx, k8s ingress, …). Without these, OIDC will redirect users back to `http://…` even when the public URL is HTTPS, breaking the login flow.

A correctly configured proxy passes through all three headers; for example, a minimal Caddy config:

```caddyfile
tergeo.example.com {
    reverse_proxy tergeo:8080
}
```

does this automatically. For nginx:

```nginx
proxy_set_header Host $host;
proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header X-Forwarded-Host  $host;
```

### Which proxies are trusted?

For security, the forwarded headers are only honored when they come from a trusted source IP. Out of the box the app trusts:

- `127.0.0.0/8` and `::1/128` (loopback)
- `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` (RFC 1918 private)
- `fc00::/7` (IPv6 unique-local)

That covers Docker bridges, k8s pod networks, and the vast majority of home-LAN setups out of the box.

If your proxy lives outside those ranges (a public-facing load balancer talking directly to the container, an external Cloudflare Tunnel relay, etc.) extend trust via these env vars:

| `.env` variable | App env var | Format | Purpose |
| --- | --- | --- | --- |
| `TERGEO_FORWARDED_KNOWN_NETWORKS` | `ForwardedHeaders__KnownNetworks` | Comma-separated CIDRs (`1.2.3.0/24,2001:db8::/32`) | Add IPv4/IPv6 networks to the trust list |
| `TERGEO_FORWARDED_KNOWN_PROXIES` | `ForwardedHeaders__KnownProxies` | Comma-separated IPs (`1.2.3.4,2001:db8::1`) | Add individual IPs |

Both accept either bare IPs or CIDRs. To trust **any** proxy (single-tenant, fully under your control), set `KnownNetworks` to `0.0.0.0/0,::/0`.

## Security notes
- API keys, passwords for OPDS sources, and Identity cookies are first-class citizens
- OPDS source passwords are protected with ASP.NET Core Data Protection (key ring stored in the data directory)
- Outbound OPDS requests refuse to follow redirects and (by default) refuse private/loopback addresses to prevent SSRF and DNS rebinding. Toggle via `Opds__AllowPrivateNetworks` for self-hosted setups.
- Cookies are HttpOnly, SameSite=Lax, sliding expiry 14 days
