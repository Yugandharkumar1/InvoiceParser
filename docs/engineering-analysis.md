# Invoice Parser — Developer / Engineering Perspective

Technical engineering overview for onboarding, architecture reviews, modernization planning, and technical due diligence.

---

## 1. Technical Overview

| Aspect | Description |
|--------|-------------|
| **Style** | **Modular monolith:** ASP.NET Core **MVC** host, **Core** domain/services, **Infrastructure** persistence. |
| **Front/back** | Server-rendered Razor views + static files; **no SPA** evidenced in project layout. |
| **API** | **MVC controller** exposes `POST api/invoice/parse` alongside traditional views — **not** a minimal-API `Program.cs` map group; same pipeline as UI. |
| **Data flow** | Upload/stream → **bytes** → **text extraction** (PDF vs image OCR) → **parse stack** (rules + optional ML + optional AI + Verizon + validation) → optional Python augment → review → EF **SaveChanges** across two DB contexts. |
| **Async processing** | **Hosted service** watches CSV training data; potential **automatic retrain** debounced on disk events. |

---

## 2. Technology Stack

| Layer | Stack (versions where known) |
|-------|------------------------------|
| **Languages** | **C#** (.NET **7** per `*.csproj`), **Python** **3.10+** recommended (Python service README). |
| **Web** | ASP.NET Core MVC (`Microsoft.NET.Sdk.Web`). |
| **ORM** | **EF Core 7** + `Microsoft.EntityFrameworkCore.SqlServer`. |
| **PDF/OCR/ML (C#)** | **PdfPig**, **Docnet.Core**, **SkiaSharp**, **Tesseract**, **Microsoft.ML** `3.0.1`. |
| **Python** | **FastAPI**, **uvicorn**, **torch**, **transformers**, **PyMuPDF**, **pytesseract**. |
| **Auth** | **None wired** in `Program.cs` (only `UseAuthorization`). |
| **Infra/CI** | **Not present** in repository scan (no Dockerfile at root, no `.github/workflows` found). |

---

## 3. Repository Structure Analysis

| Area | Observations |
|------|--------------|
| **Layout** | `src/InvoiceParser.{Web,Core,Infrastructure}` + `src/python/invoice_parser_service` + **out-of-solution** `TestVerizon`. |
| **Layering** | **Core:** entities, parsers, ML, HTTP-based AI helpers. **Infrastructure:** EF contexts + repository. **Web:** DI composition, hosted services, views. |
| **Separation** | Generally consistent **Repository** abstraction (`IInvoiceRepository`). Large **orchestration** lives in `InvoiceService`. |
| **Shared libs** | None beyond project references; Python is a **separate deployable**. |
| **Build artifacts** | `bin/`/`obj/` and mixed **net7/net8** under `obj` in some projects — suggests local/tooling inconsistency **Needs Validation**. |

Solution file: `InvoiceParser.sln` includes **Core**, **Infrastructure**, and **Web** only (`TestVerizon` not in solution).

---

## 4. Architecture & Design Patterns

| Pattern | Where it appears |
|---------|------------------|
| **Layered / n-tier** | Web → Core services → Infrastructure repositories. |
| **Repository** | `InvoiceRepository` mediates two DbContexts. |
| **DI** | Extensive `AddSingleton`/`AddScoped`/`AddHttpClient` registration in `Program.cs`. |
| **Strategy / pipeline** | Multiple parsers (generic, Verizon, ML overlay, AI overlay) composed sequentially. |
| **Hosted background work** | `TrainingDataWatcherService` monitors filesystem. |
| **Optional microservice** | Python FastAPI — **feature-flagged** via configuration (`InvoiceParserPython`). |

---

## 5. API & Integration Analysis

| Topic | Notes |
|--------|--------|
| **Style** | **REST-ish** MVC route `POST api/invoice/parse` with `IFormFile` + `carrierId`. |
| **Conventions** | Returns anonymous JSON objects from controller — **no** versioned API namespace or OpenAPI in repo. |
| **Auth** | **No** `[Authorize]` or token middleware observed in reviewed host setup. |
| **External HTTP** | Named `HttpClient`s for AI parse, feedback LLM, charge validation, and Python integration — **timeout** values set per client. |
| **Resilience** | **Try/catch** + logging around calls; **no** Polly/circuit breaker in package list reviewed. |

---

## 6. Data Layer Analysis

| Topic | Notes |
|--------|--------|
| **Databases** | **SQL Server** only (per packages). |
| **Contexts** | `AppDbContext`: invoices, charges, usages, inventories, rules, feedback. `IPathDbContext`: lookup **Customer**, **Carrier**. |
| **Migrations** | **No `Migrations/` folder** found — schema may be **manual, external, or EnsureCreated** *(Needs Validation)*. |
| **Transactions** | EF `SaveChangesAsync` in repository methods; cross-context **not** a single transaction (two databases). |
| **Caching** | **No** distributed cache references found in sampled projects. |

---

## 7. Security Assessment

| Area | Finding |
|------|---------|
| **Authentication** | **Absent** in `Program.cs` composition reviewed. |
| **Authorization** | `UseAuthorization()` without authentication produces **limited practical protection**. |
| **Secrets** | **Never commit** database passwords or API keys; use user secrets, environment variables, or a vault. Rotate anything ever committed. |
| **CSRF** | MVC `Save`/`Upload` uses **`[ValidateAntiForgeryToken]`** — appropriate for form posts. |
| **API abuse** | Parse endpoint lacks auth/rate limit in code reviewed. |
| **Input validation** | Basic guard rails on uploads and carrier/customer IDs; full threat model **not** evidenced. |

---

## 8. Testing & Quality Engineering

| Signal | Assessment |
|--------|------------|
| **Automated tests** | **No** test framework packages in `.csproj` files searched (xUnit/NUnit/MSTest). |
| **Integration/E2E** | **None** found. |
| **Manual harness** | `TestVerizon` console prints line-numbered PDF text; **hardcoded default path** — dev-only. |
| **Coverage confidence** | **Low** — behavior enforced by production runs only. |

---

## 9. DevOps & Deployment

| Item | State |
|------|--------|
| **CI/CD** | **Not found** in `.github/` (may exist externally). |
| **Containers** | **No** Dockerfile at repo root. |
| **IaC** | **None** observed. |
| **Environments** | `appsettings.json` + `appsettings.Development.json`; `launchSettings` uses localhost. |
| **Observability** | Console/file logging defaults; **no** metrics exporters in reviewed config. |

---

## 10. Code Quality Assessment

| Strength | Risk |
|---------|------|
| **Readable orchestration** in `InvoiceService` with structured logging | Large **god-service** tendency — harder to test and evolve. |
| **Composable parsers** | Duplication risk across C# ML / AI / Python paths. |
| **Repository consolidation** | Two-database coupling in one repository may hinder boundary clarity. |
| **Hygiene** | Minimal root `.gitignore` coverage; path-based test program; configuration must stay free of secrets in VCS. |

---

## 11. Modernization Opportunities

| Area | Opportunity |
|------|----------------|
| **Security** | Secrets management, keys via vault, gateway auth, optional **mTLS** for internal API. |
| **Engineering** | Extract parsing into **testable components** + golden-file tests from anonymized PDFs. |
| **Data** | Formalize schema via **migrations** + seed scripts for carriers/customers in non-prod. |
| **Architecture** | If scale matters, **out-of-process OCR/parse workers** with a queue and blob storage for originals. |
| **Python path** | Decide single **source of truth** for ML or formalize **dual-run** with reconciliation metrics. |

---

## 12. Engineering Health Scorecard (1–10)

| Dimension | Score | Rationale |
|-----------|-------|-----------|
| **Architecture** | **6** | Solid layering and clear extension points; monolithic orchestration and dual-DB coupling temper score. |
| **Code quality** | **6** | Generally clean C#; some large classes and configuration coupling. |
| **Testing** | **2** | Effectively absent for the main app. |
| **Security** | **2** | No auth in reviewed host + sensitive document API + secrets must not live in repo. |
| **Maintainability** | **5** | Understandable structure undermined by ops and secret hygiene risks. |
| **Performance** | **5** | Adequate for low volume; OCR/ML paths need profiling and async offload at scale. |
| **Scalability** | **4** | Single-node assumptions; heavy optional ML. |
| **DevOps maturity** | **3** | Missing visible automation and packaging. |

---

## Assumptions & Needs Validation

- **End users and deployment context** (internal-only vs. internet-facing).
- **Whether CI/CD lives in another repository or platform.**
- **Whether database schema is managed by DBA scripts** outside this repo.
- **Intended production topology** for the Python service (GPU nodes, sidecar, or unused).
- **Actual LLM/HTTP endpoint** and data-processing agreements (vendor, residency, retention).

*Generated from codebase and configuration review.*
