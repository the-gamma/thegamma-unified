# The Gamma Unified Service

A single .NET 9 / Giraffe application consolidating 6 previously separate F# services that form The Gamma data visualization platform.

## Build & Run

```bash
dotnet build
dotnet run       # http://localhost:5000
```

### Docker

```bash
# Run locally (uses local storage volume)
bash run-docker.sh

# Or manually:
docker run -p 5000:8080 \
  -e THEGAMMA_BASE_URL=http://localhost:5000 \
  -v /path/to/storage:/app/storage \
  tomasp/thegamma-unified:latest
```

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `THEGAMMA_BASE_URL` | `http://localhost:8080` | Public base URL (used in generated provider endpoints) |
| `THEGAMMA_STORAGE_ROOT` | `./storage` | Root directory for runtime data (uploads, snippets, logs) |
| `RECAPTCHA_SECRET` | _(empty)_ | Google reCAPTCHA secret for gallery submission validation |

## Production Deployment

- **Hosting**: Hostim (EU, Germany)
- **Docker image**: `tomasp/thegamma-unified:latest` on Docker Hub
- **Custom domains** (via Cloudflare):
  - `gallery.thegamma.net` — gallery web UI
  - `services.thegamma.net` — backend services
  - `rio2016.thegamma.net` — Rio 2016 Olympics articles

## Routes

Gallery web UI is served at the root (`/`). All backend services are under `/services/*`.

| Route | Description | Original Service |
|-------|-------------|-----------------|
| `/` | Gallery home (lists recent snippets) | gallery-web |
| `/create` | Multi-step create wizard | gallery-web |
| `/all` | All snippets listing | gallery-web |
| `/{id}/{slug}` | Snippet detail page | gallery-web |
| `/{id}/{slug}/embed` | Embeddable snippet | gallery-web |
| `/rio2016/*` | Rio 2016 Olympics articles | thegamma-olympics-web |
| `/services/csv/*` | CSV upload, pivot queries, web scraping | gallery-csv-service |
| `/services/snippets/*` | Snippet CRUD | gallery-snippet-service |
| `/services/expenditure/*` | UK Government expenditure data | govuk-expenditure-service |
| `/services/log/*` | Append-only logging | thegamma-logging |
| `/services/worldbank/*` | World Bank data provider | thegamma-services |
| `/services/olympics/*` | Olympic medals (faceted browsing) | thegamma-services |
| `/services/pdata/*` | Pivot data engine (olympics, smlouvy) | thegamma-services |
| `/services/pivot/*` | Pivot proxy (wraps REST providers) | thegamma-services |
| `/services/smlouvy/*` | Czech government contracts | thegamma-services |
| `/services/adventure/*` | Text adventure demo | thegamma-services |
| `/services/minimal/*` | Minimal type provider example | thegamma-services |

## Project Structure

```
thegamma-unified.fsproj             Project file
Dockerfile                          Multi-stage .NET 9 build
run-docker.sh                       Local Docker run script

src/                                All F# source code
  Program.fs                        Entry point, route mounting, initialization

  Common/
    Serializer.fs                   Type provider protocol serialization
    JsonHelpers.fs                  Shared JSON helpers
    Facets.fs                       Generic faceted browsing framework

  CsvService/
    Pivot.fs                        CSV pivot/query engine (groupby, filter, sort, aggregations)
    Storage.fs                      Local file storage, MailboxProcessor agents for uploads/cache
    WebScrape.fs                    HTML scraping to CSV
    Listing.fs                      Browse uploads by date/tag
    Routes.fs                       /services/csv/* routes

  SnippetService/
    SnippetAgent.fs                 MailboxProcessor agent, local file read/write
    Routes.fs                       /services/snippets/* routes

  Gallery/
    Filters.fs                      DotLiquid custom filters (niceDate, cleanTitle, etc.)
    GalleryLogic.fs                 Snippet agent, CSV upload, reCAPTCHA validation
    Routes.fs                       / routes, DotLiquid template rendering

  Expenditure/
    LoadData.fs                     CSV data loading (Table4-2, Table5-2, etc.)
    Routes.fs                       /services/expenditure/* routes

  Logging/
    LogAgent.fs                     File-based append logging
    Routes.fs                       /services/log/* routes

  Olympics/
    Routes.fs                       /rio2016/* routes, Markdown article rendering

  Services/
    Minimal/Routes.fs               Minimal type provider example
    Adventure/Routes.fs             Text adventure service
    WorldBank/Domain.fs             Types and binary cache reader
    WorldBank/Routes.fs             /services/worldbank/* routes
    Olympics/Routes.fs              Olympic medals with faceted filtering
    PivotData/Routes.fs             Pivot engine for olympics + smlouvy data
    Smlouvy/Routes.fs               Czech contracts (XML data)
    Pivot/Routes.fs                 Pivot proxy (wraps REST providers with transforms)

templates/                          DotLiquid HTML templates (gallery UI)
olympics-templates/                 DotLiquid HTML templates (rio2016 articles)
olympics-docs/                      Markdown source for rio2016 articles
wwwroot/                            Static files (CSS, JS, images)
data/                               Bundled static data (read-only, shipped with app)
  worldbank/                        Pre-cached World Bank data (countries, indicators, years)
  expenditure/                      UK Government expenditure CSVs
  medals-expanded.csv               Olympic medals dataset
  countrycodes.html                 Country code mappings
  adventure-sample.txt              Text adventure data
  smlouvy-index.xml                 Czech contracts XML samples
  smlouvy-dump.xml
storage/                            Runtime data (Docker volume in production)
  uploads/                          User-uploaded CSV files + files.json metadata
  cache/                            Cached CSV files from URL imports
  snippets/                         Snippet JSON files per source
  logs/                             Append-only log files
```

## Migration from Original Services

Consolidated from 6 separate F# / Suave / .NET Framework services:

- **Suave** → **Giraffe** (same functional composition model: `>=>`, `choose`, route handlers)
- **Azure Blob Storage** → **local file I/O** (MailboxProcessor agent architecture preserved)
- **gallery-web HTTP calls** to csv-service/snippet-service → **direct F# function calls**
- **Paket + FAKE** → **dotnet CLI**
- **.NET Framework 4.x** → **.NET 9**
- **Hardcoded `*.azurewebsites.net` URLs** → relative paths / env var `THEGAMMA_BASE_URL`
- **thegamma-olympics-web** (separate Suave app) → ported to `/rio2016/*` in this app

Dropped: `govuk-service` (air quality + traffic, SQL Server backend, not currently used).
Unchanged: `the-gamma.github.io` (GitHub Pages), `turing-web` (Node.js, GitHub Pages).
