# The Gamma Unified Service

A single .NET 8 / Giraffe application consolidating 6 previously separate F# services that form The Gamma data visualization platform.

## Build & Run

```bash
dotnet build
dotnet run
```

The server starts on `http://localhost:5000` by default.

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `THEGAMMA_BASE_URL` | `http://localhost:8080` | Public base URL (used in generated provider endpoints) |
| `THEGAMMA_STORAGE_ROOT` | `./storage` | Root directory for runtime data (uploads, snippets, logs) |
| `RECAPTCHA_SECRET` | _(empty)_ | Google reCAPTCHA secret for gallery submission validation |

### Docker

```bash
docker build -t thegamma .
docker run -p 8080:8080 -v ./storage:/data/storage thegamma
```

## Routes

| Route | Description | Original Service |
|-------|-------------|-----------------|
| `/` | Health check | — |
| `/csv/*` | CSV upload, pivot queries, web scraping | gallery-csv-service |
| `/snippets/*` | Snippet CRUD | gallery-snippet-service |
| `/gallery/*` | Web gallery UI (DotLiquid templates) | gallery-web |
| `/expenditure/*` | UK Government expenditure data | govuk-expenditure-service |
| `/log/*` | Append-only logging | thegamma-logging |
| `/worldbank/*` | World Bank data provider | thegamma-services |
| `/olympics/*` | Olympic medals (faceted browsing) | thegamma-services |
| `/pdata/*` | Pivot data engine (olympics, smlouvy) | thegamma-services |
| `/pivot/*` | Pivot proxy (wraps REST providers) | thegamma-services |
| `/smlouvy/*` | Czech government contracts | thegamma-services |
| `/adventure/*` | Text adventure demo | thegamma-services |
| `/minimal/*` | Minimal type provider example | thegamma-services |

## Project Structure

```
Program.fs                          Entry point, route mounting, initialization

Common/
  Serializer.fs                     Type provider protocol serialization
  JsonHelpers.fs                    Shared JSON helpers (toJson, formatPairSeq)
  Facets.fs                         Generic faceted browsing framework

CsvService/
  Pivot.fs                          CSV pivot/query engine (groupby, filter, sort, aggregations)
  Storage.fs                        Local file storage, MailboxProcessor agents for uploads/cache
  WebScrape.fs                      HTML scraping to CSV
  Listing.fs                        Browse uploads by date/tag
  Routes.fs                         /csv/* routes

SnippetService/
  SnippetAgent.fs                   MailboxProcessor agent, local file read/write
  Routes.fs                         /snippets/* routes

Gallery/
  Filters.fs                        DotLiquid custom filters (niceDate, cleanTitle, etc.)
  GalleryLogic.fs                   Snippet agent, CSV upload, reCAPTCHA validation
  Routes.fs                         /gallery/* routes, DotLiquid template rendering

Expenditure/
  LoadData.fs                       CSV data loading (Table4-2, Table5-2, etc.)
  Routes.fs                         /expenditure/* routes

Logging/
  LogAgent.fs                       File-based append logging
  Routes.fs                         /log/* routes

Services/
  Minimal/Routes.fs                 Minimal type provider example
  Adventure/Routes.fs               Text adventure service
  WorldBank/Domain.fs               Types and binary cache reader
  WorldBank/Routes.fs               /worldbank/* routes
  Olympics/Routes.fs                Olympic medals with faceted filtering
  PivotData/Routes.fs               Pivot engine for olympics + smlouvy data
  Smlouvy/Routes.fs                 Czech contracts (XML data)
  Pivot/Routes.fs                   Pivot proxy (wraps REST providers with transforms)

templates/                          DotLiquid HTML templates (gallery UI)
wwwroot/                            Static files (CSS, JS)
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
Dockerfile
```

## Migration from Original Services

This project was migrated from 6 separate F# services:

- **Suave** replaced with **Giraffe** (same functional composition model: `>=>`, `choose`, route handlers)
- **Azure Blob Storage** replaced with **local file I/O** (same MailboxProcessor agent architecture)
- **gallery-web HTTP calls** to csv-service and snippet-service replaced with **direct F# function calls**
- **Paket + FAKE** replaced with **dotnet CLI**
- **.NET Framework 4.x** replaced with **.NET 8**
- **Hardcoded `*.azurewebsites.net` URLs** in templates replaced with relative paths

Dropped: `govuk-service` (air quality + traffic, SQL Server backend, not currently used).
Unchanged: `the-gamma.github.io` (GitHub Pages), `turing-web` (Node.js, static).
