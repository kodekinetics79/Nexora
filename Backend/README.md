# ERP RFQ Automation Backend

ASP.NET Core 8 backend API for an ERP-style RFQ automation platform. It provides authentication, role-based access control, business unit scoped master data, lead ingestion, RFQ processing, quotations, orders, shipments, customer/supplier management, inventory management, file uploads, email polling, document parsing, and AI-assisted lead extraction.

## Project Overview

The backend is a database-first ASP.NET Core Web API using Entity Framework Core with SQL Server. Controllers expose REST endpoints under `/api/*`, repositories contain most data access logic, and services handle imports, document extraction, email ingestion, quote generation, and AI integration.

The paired frontend lives in `../RFQ-Automation-Vite` and expects this API at `http://localhost:5192` during local development.

## Technology Stack

- ASP.NET Core 8 Web API
- Entity Framework Core with SQL Server
- JWT bearer authentication
- Custom authorization policies and permission handler
- BCrypt password verification
- Swagger/OpenAPI
- MailKit and MimeKit for mailbox integration
- EPPlus and XLSX processing for templates/imports
- QuestPDF for document generation
- Tesseract, PdfPig, Docnet, FreeSpire.Doc, OpenXmlPowerTools, and HtmlAgilityPack for document/email parsing support
- Ollama-compatible chat API integration for RFQ/lead extraction

## Solution Structure

```text
ERP_RFQ_Automation.sln
ERP_RFQ_Automation/
  Authorization/              Custom permission requirement and handler
  Controllers/                REST API controllers
  DTOs/                       Request and response contracts
  Interfaces/                 Repository and service interfaces
  Models/                     EF Core database-first entities and DbContext
  Repositories/               Data access and business-unit scoped queries
  Services/                   Uploaders, email processing, document parsing, AI extraction, quote/order services
  Properties/launchSettings.json
  Program.cs                  Service registration and middleware pipeline
  appsettings.json            Local configuration template; use safe values only
  wwwroot/                    Uploaded images and public file assets
  Uploads/                    Runtime upload folders
  tessdata/                   Tesseract language data
```

## Main Domains

- Authentication and users
- Roles, modules, and role permissions
- Business units and setup master data
- Country, state, city, UOM, warehouse, and currency setup
- Customers and contacts
- Suppliers, supplier quoted items, and supplier purchase history
- Inventory products, product categories, product sub-categories, product matching, stock details
- Leads from manual upload, folder upload, mailbox polling, and AI extraction
- RFQs, RFQ items, approval, draft/outstanding/all RFQ views
- Quotations, quote items, quote PDFs, quote status, quote email
- Orders, order items, order invoices, RFQ-to-order and quote-to-order flows
- Shipments, shipment items, shipment status history, shipment documents
- Dashboard metrics

## API Surface

The controllers use `api/[controller]` routing. Common endpoint groups include:

- `POST /api/Auth/Login`
- `GET/POST/PUT/DELETE /api/User`
- `GET/POST/PUT/DELETE /api/RolePermission`
- `GET/POST/PUT/DELETE /api/BusinessUnit`
- `GET/POST/PUT/DELETE /api/Currency`
- `GET/POST/PUT/DELETE /api/Warehouse`
- `GET/POST/PUT/DELETE /api/Uom`
- `GET/POST/PUT/DELETE /api/Country`
- `GET/POST/PUT/DELETE /api/State`
- `GET/POST/PUT/DELETE /api/City`
- `GET/POST/PUT/DELETE /api/Customer`
- `GET/POST/PUT/DELETE /api/Supplier`
- `GET /api/Lead`, `POST /api/Lead/accept/{id}`, `POST /api/Lead/reject/{id}`, `GET /api/Lead/stats`
- `GET/POST/PUT/DELETE /api/Rfq`, `POST /api/Rfq/{id}/approve`, `GET /api/Rfq/stats`
- `GET/POST/PUT/DELETE /api/Quote`, `GET /api/Quote/{id}/pdf`, `POST /api/Quote/{id}/email`, `POST /api/Quote/{id}/status`
- `GET/POST/PUT/DELETE /api/Order`, `POST /api/Order/from-rfq/{rfqId}`, `POST /api/Order/from-quote/{quoteId}`, `GET /api/Order/{id}/invoice`
- `GET/POST/PUT/DELETE /api/Shipment`
- uploader controllers for customers, suppliers, products, categories, RFQs, leads, and quotations

Swagger is enabled in development and launches at `/swagger` by default.

## Authentication and Authorization

`AuthController` accepts email, password, and business unit data. `AuthRepository` verifies the password with BCrypt, checks that the selected business unit matches the user, and issues a JWT with:

- user id
- email
- role id
- business unit id
- token id

`Program.cs` configures JWT bearer validation using `Jwt:Issuer`, `Jwt:Audience`, and `Jwt:Key`.

Role-based authorization is implemented with `PermissionRequirement` and `PermissionHandler`. The handler reads `roleId` and `businessUnitId` claims, then checks repository-backed permissions for module/action combinations.

## Background Processing and AI Extraction

`EmailBackgroundService` runs as a hosted service. It periodically reads active email configurations, fetches messages, saves raw emails and attachments, and triggers lead extraction.

Lead extraction services process email bodies, PDFs, Word documents, Excel files, and manual uploads. `OllamaLlmService` sends normalized RFQ text to an Ollama-compatible chat API and expects structured JSON with header fields, line items, dates, quantities, confidence scores, and extraction metadata.

## Configuration

Use local configuration or environment variables for these values:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_SQL_SERVER;Database=ERP_RFQ_Automation_v2;User Id=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=True;"
  },
  "Jwt": {
    "Key": "YOUR_LONG_RANDOM_SIGNING_KEY",
    "Issuer": "YOUR_ISSUER",
    "Audience": "YOUR_AUDIENCE",
    "ExpiryMinutes": 60
  },
  "Ollama": {
    "BaseUrl": "https://ollama.com/",
    "ApiKey": "YOUR_OLLAMA_API_KEY",
    "Model": "YOUR_MODEL"
  }
}
```

Do not commit production connection strings, JWT signing keys, mailbox passwords, or AI provider API keys.

ASP.NET Core environment variable examples:

```powershell
$env:ConnectionStrings__DefaultConnection="Server=localhost;Database=ERP_RFQ_Automation_v2;Trusted_Connection=True;TrustServerCertificate=True;"
$env:Jwt__Key="replace-with-a-long-random-secret"
$env:Jwt__Issuer="KodeKinetics"
$env:Jwt__Audience="RFQ"
$env:Ollama__ApiKey="replace-with-api-key"
```

## Requirements

- .NET SDK 8
- SQL Server database matching the EF Core model
- Optional: Ollama-compatible API credentials for AI extraction
- Optional: mailbox credentials in database email configuration records for background email ingestion

## Setup

```powershell
dotnet restore
dotnet build ERP_RFQ_Automation.sln
dotnet run --project ERP_RFQ_Automation\ERP_RFQ_Automation.csproj
```

Default launch URLs:

```text
http://localhost:5192
https://localhost:7172
```

Swagger:

```text
http://localhost:5192/swagger
```

## Database Model

The EF Core model appears to be scaffolded from an existing SQL Server database. If the schema changes, regenerate or update `Models/ErpRfqAutomationV2Context.cs` and related entity classes carefully.

Example scaffold pattern:

```powershell
Scaffold-DbContext "Server=YOUR_SQL_SERVER;Database=ERP_RFQ_Automation_v2;Trusted_Connection=True;TrustServerCertificate=True" Microsoft.EntityFrameworkCore.SqlServer -OutputDir Models -Force
```

Use a safe local connection string when scaffolding.

## File Storage

The app writes and serves runtime files from folders such as:

- `Uploads/Raw_Emails`
- `Uploads/Manual_Attachments`
- `Uploads/RFQ_Attachments`
- `Uploads/Leads_Folder_Attachments`
- `Uploads/Processed_Leads_Folder`
- `wwwroot/UserImages`
- `wwwroot/CustomerImages`
- `wwwroot/SupplierImages`
- `wwwroot/InventoryImages`
- `wwwroot/ProductAttachments`

For production, decide whether these should remain local disk storage or move to object storage such as Azure Blob Storage or S3.

## CORS

`Program.cs` currently registers an `AllowAll` CORS policy. This is convenient for local development. For production, restrict allowed origins to trusted frontend domains.

## Verification

Current local check:

```powershell
dotnet build ERP_RFQ_Automation.sln
```

Result: blocked by a locked running executable:

```text
ERP_RFQ_Automation.exe is being used by process ERP_RFQ_Automation (35164)
```

The build restored packages and compiled far enough to reveal warnings before failing on the locked output file. Notable warnings include nullable reference warnings, package compatibility warnings for `OpenXmlPowerTools`/`System.Management.Automation.dll`, and moderate vulnerability advisories for `MailKit` and `MimeKit`.

## Security Notes

- Rotate any secrets that were previously committed in configuration files.
- Move secrets to environment variables, user secrets, Key Vault, or another secret manager.
- Restrict CORS in production.
- Confirm that anonymous endpoints are intentional, especially download/search endpoints.
- Consider moving uploaded runtime files out of the repository before publishing publicly.
- Review package advisories and update affected dependencies where compatible.
