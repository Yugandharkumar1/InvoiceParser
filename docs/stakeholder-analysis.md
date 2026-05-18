# Invoice Parser — Stakeholder Perspective

Business-facing overview for leadership, product owners, managers, architects, and stakeholders.

---

## 1. Executive Summary

| Topic | Summary |
|--------|---------|
| **What it is** | A **telecom / utility-style invoice ingestion system** that reads PDFs and images, extracts structured fields (summary, line items, usages, inventory), supports **human review**, **persists** results to SQL Server, and improves extraction through **rules and ML feedback loops**. |
| **Primary business purpose** | Reduce manual data entry for carrier invoices, standardize fields for downstream systems, and **continuously improve** parsing accuracy using corrections and optional advanced models. |
| **Intended users** | **Assumed:** internal operations, billing analysts, or partner onboarding staff *(not validated from docs; inferred from MVC workflow and lack of product multi-tenancy/auth)*. |
| **Domains** | **Telecom expense / carrier billing** (explicit Verizon-oriented parsing, “telecom/SaaS” wording in AI prompts), **invoice master data** (customers/carriers from an external DB), **ML/AI-assisted document understanding**. |

---

## 2. Core Functionalities

| Area | Capabilities (evidence-based) |
|------|------------------------------|
| **Document intake** | Web upload of PDF / images; server-side text extraction (PDF libraries + OCR). |
| **Intelligent extraction** | Rule/regex “smart” parser, optional **ML.NET** gap-fill, optional **remote LLM** gap-fill, **Verizon Wireless**-specific layout handling, **charge validation/filtering**. |
| **Review & correction** | MVC “Review” screen; user edits; **duplicate invoice** prevention; structured save. |
| **Feedback & learning** | User feedback ingestion; generation of **vendor parsing rules**; optional **LLM “agent”** when rule engine cannot infer rules; tracking correct vs. corrected fields to tune rules. |
| **Training / model ops** | **Hosted file watcher** on training CSVs triggers retraining logic; model files under web content paths; optional **Python microservice** with retrain/promote workflow *(disabled in default config)*. |
| **API-style access** | `POST` **parse** endpoint returning extracted JSON *(no visible auth in code reviewed)*. |
| **Reporting** | **Minimal:** list and detail views for saved invoices *(no dedicated reporting module found)*. |
| **AuthN/AuthZ** | **Not implemented** in the reviewed startup pipeline (`UseAuthentication` not registered); **Needs Validation** if deployed beyond trusted networks. |
| **Admin** | Operational actions via MVC (e.g., process feedback, train model) rather than a separate admin product. |

---

## 3. Current Product Maturity Assessment

| Dimension | Assessment |
|-----------|------------|
| **Stage** | **MVP → early product:** rich parsing pipeline and persistence, but **enterprise hardening** (secrets, auth, CI/CD, tests) is **not** at enterprise-ready level based on repository signals. |
| **Stable vs. evolving** | **Relatively stable:** classic MVC + EF + repository shape. **Evolving / experimental:** dual-path **Python LayoutLM** service, **LLM integrations**, multiple parsing strategies layered in one service. |
| **Technical debt** | Committed configuration with **secrets**; **no migrations** in repo; **mixed runtime build artifacts** suggest environment drift; **TestVerizon** console with **hardcoded local paths**. |
| **Missing capabilities** | Authentication/authorization, audit trails *(not evident)*, formal API product surface, observability stack, automated test suite, deployment packaging. |
| **Scalability readiness** | **Assumed** single-deploy workload: file processing, OCR, and optional GPU-heavy Python training are **resource-intensive**; horizontal scale **not** evidenced (no queue abstraction in reviewed core path). |

---

## 4. Business Workflows

1. **Upload → extract → review → save:** User selects customer/carrier, uploads file, system parses, presents review screen, prevents duplicates on save.
2. **Correction loop:** User adjusts fields; on save, system compares originals vs. confirmed values and **updates rule feedback counts** / triggers learning routines.
3. **Feedback to rules:** Structured feedback can produce regex rules; if insufficient, optional LLM generates candidate rules validated against PDF text.
4. **ML retraining (C#):** Training batches pulled from stored invoices/feedback; training exposed via controller action.
5. **Background retrain signal:** CSV changes under `TrainingData` debounce-retrain.
6. **Optional Python path:** When enabled, C# may augment parse results and **submit feedback** to Python for weak-label training.
7. **API parse:** External or internal clients POST a file + `carrierId` for JSON extraction.

---

## 5. Integrations & External Systems

| Type | Items |
|------|--------|
| **Databases** | **Two SQL Server** connection strings: **application DB** (`AppDbContext`) and **reference DB** (`IPathDbContext`) for **Customer** and **Carrier**. |
| **HTTP AI / LLM** | **Configurable HTTP client** for “OpenAI”-shaped settings; **BaseUrl and provider must be validated per environment** *(label vs. actual endpoint — **Needs Validation**)*; separate client for **Ollama-style** localhost/11434 detection in feedback agent. |
| **Python service** | **FastAPI** stack: LayoutLM/transformers, Tesseract, PyMuPDF — integrated via typed HTTP client when `InvoiceParserPython:Enabled` is true. |
| **Monitoring/logging** | Default **ASP.NET Core logging** only *(no APM/metrics configs found in repo root)*. |
| **CI/CD** | **No `.github/workflows`** found — **Needs Validation** if pipelines live outside repo. |

---

## 6. Risks & Concerns (High Level)

| Risk | Why it matters |
|------|----------------|
| **Credential exposure** | Database credentials and API-style keys must not live in committed configuration; treat as **critical** compliance and incident risk if present in source control. |
| **Unauthenticated API** | Parse endpoint can process **sensitive invoice documents** without auth in reviewed `Program.cs`. |
| **Data residency / PII** | Invoices and PDF text likely contain **account numbers, employee names, usage** — sending to third-party LLMs requires **legal/security review**. |
| **Operational fragility** | No container/IaC/tests in repo increases **deployment variance** and regressions. |
| **Performance** | OCR + optional ML workloads can be **CPU/GPU hungry** and unpredictable under load. |

---

## 7. Recommendations

| Horizon | Actions |
|---------|---------|
| **Short term (days–weeks)** | **Remove secrets from git**; use user secrets, environment variables, or a vault; **rotate** any exposed credentials; add **`.gitignore`** at repo root; add **authentication** (even basic) for UI + API; network-restrict deployment. |
| **Medium term (1–3 months)** | Introduce **EF migrations** or explicit schema management; add **CI build** + minimal **integration tests** for parse/save; structured logging + health checks; document **Python optional path** operationally. |
| **Long term** | Split **document processing** into a job/queue if scale demands; consolidate ML paths (C# ML.NET vs Python) behind a **single product strategy**; add **audit & RBAC** if multi-user enterprise adoption is targeted. |

---

## 8. Overall Repository Health Score (1–10)

| Dimension | Score | Rationale |
|-----------|-------|-----------|
| **Maintainability** | **5** | Clear layering and readable services, but secrets-in-repo risk, weak repo hygiene (.gitignore), stray test harness paths, and no migrations in-repo hurt long-term safety. |
| **Scalability** | **4** | Monolith + synchronous parsing; optional heavy ML; no clear async job fabric. |
| **Security** | **2** | Secrets must not be committed; open parse surface when unauthenticated; no authentication in host setup reviewed. |
| **Testability** | **2** | No xUnit/NUnit projects; only a manual console helper. |
| **Architecture quality** | **6** | Sensible separation Core/Infrastructure/Web and composable parsers; some “kitchen sink” orchestration in `InvoiceService`. |
| **DevOps maturity** | **3** | No CI found in repo; no Dockerfile; config-driven deploy only. |
| **Documentation quality** | **4** | Python README is strong; **no** top-level product/architecture README. |

---

*Generated from codebase and configuration review. Items marked **Assumed** or **Needs Validation** require confirmation with the owning team.*
