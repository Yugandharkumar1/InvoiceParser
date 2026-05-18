# Invoice Parser

ASP.NET Core app for **ingesting telecom/carrier invoices** (PDF and images), **extracting** structured fields and line items, **human review**, and **persisting** results to **SQL Server**. Optional **ML** (.NET and Python), **rule learning** from corrections, and **HTTP-based LLM** assist can improve extraction over time.

**Scope:** document capture, validation, and continuous improvement—not a full billing or customer portal product. Treat as **MVP / early internal platform** from an operations and security perspective until auth, testing, and deployment hardening match your environment.

## Documentation

| Document | Description |
|----------|-------------|
| [docs/overview.md](docs/overview.md) | Short product and tech overview |
| [docs/stakeholder-analysis.md](docs/stakeholder-analysis.md) | Business-facing maturity, workflows, risks, recommendations |
| [docs/engineering-analysis.md](docs/engineering-analysis.md) | Architecture, stack, data layer, security, DevOps notes |

Python service (LayoutLM + FastAPI): [src/python/invoice_parser_service/README.md](src/python/invoice_parser_service/README.md).

## Tech stack

| Area | Technologies |
|------|----------------|
| **Web app** | .NET 7, ASP.NET Core MVC (Razor), EF Core 7, SQL Server |
| **Parsing (C#)** | PdfPig, Docnet, SkiaSharp, Tesseract (OCR), Microsoft.ML |
| **Optional Python service** | Python 3.10+, FastAPI, PyTorch, Hugging Face transformers, PyMuPDF, Tesseract |
| **Solution layout** | `InvoiceParser.Core` (domain/services), `InvoiceParser.Infrastructure` (EF, repositories), `InvoiceParser.Web` (host, views, DI) |

## Prerequisites

- [.NET 7 SDK](https://dotnet.microsoft.com/download/dotnet/7.0)
- **SQL Server** reachable from the app (two connection strings: application DB and reference DB for customers/carriers)
- **Tesseract OCR** on the PATH if you rely on image OCR (C# and/or Python)
- (Optional) Python 3.10+ and CUDA if you run the **invoice_parser_service** locally

## Getting started (web app)

1. Clone the repository and open `InvoiceParser.sln` (or use the CLI).

2. **Configure database connection strings** for your machine. Do **not** commit secrets. Prefer [ASP.NET Core User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) or environment variables for `ConnectionStrings:DefaultConnection` and `ConnectionStrings:IPathConnection`.

   Example (Development user secrets for the Web project):

   ```bash
   cd src/InvoiceParser.Web
   dotnet user-secrets init
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<your-app-db>"
   dotnet user-secrets set "ConnectionStrings:IPathConnection" "<your-reference-db>"
   ```

3. Ensure the schema exists for both databases (migrations may be maintained outside this repo—confirm with your team).

4. Run the web project:

   ```bash
   dotnet run --project src/InvoiceParser.Web
   ```

   Default profile in `launchSettings.json` uses **http://localhost:5200** (adjust in Visual Studio or `launchSettings.json` if needed).

5. **Optional:** Enable the Python parser by setting `InvoiceParserPython:Enabled` and `InvoiceParserPython:BaseUrl` in configuration, then start the FastAPI service per `src/python/invoice_parser_service/README.md`.

## API note

A minimal **parse** endpoint is exposed from the MVC app (multipart upload + `carrierId`). **Secure and authenticate** it before any internet-facing deployment; the reviewed host pipeline does not configure end-user authentication by default.

## Repository layout (high level)

```text
InvoiceParser.sln
docs/                      # Overview and analysis docs
src/
  InvoiceParser.Core/      # Entities, parsing, ML, integrations
  InvoiceParser.Infrastructure/  # EF Core contexts, repositories
  InvoiceParser.Web/       # MVC UI, startup, views, static assets
  python/invoice_parser_service/  # Optional FastAPI + ML service
  TestVerizon/             # Console helper (not in .sln); dev-only paths possible
```

## Security

This application processes **sensitive financial and PII-heavy documents**. Use **vault/user secrets/environment variables** for connection strings and API keys, **HTTPS** where appropriate, **network restrictions**, and a **threat model** before production use (including any optional LLM or third-party HTTP calls).

## License

License for this repository is not specified here. Add a `LICENSE` file or team policy if needed.
