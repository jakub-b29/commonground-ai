# CommonGround

Property management, fund accounting, and governance software for firms managing residential housing associations and condominiums.

Independent management firms typically assign 15 to 30 properties (400 to 1,200 units) to a single property manager. Most existing systems only record accounting entries after the fact, leaving managers to reconcile utility sub-meters in spreadsheets, check statutory compliance by hand, chase contractors over phone calls, and type meeting minutes from audio recordings. CommonGround combines the unit and meter registry, fund-separated double-entry accounting, work order verification, and owner voting in one web application.

Built during the [Meta Global AI Developer Hackathon – Cerebral Valley](https://cerebralvalley.ai/e/meta-ai-developer-hackathon).

## Operating Modes

The application supports two statutory operating profiles selectable at the system or property level:

- **Housing Association Mode (European Condominium)**: common-property ownership share fractions, statutory 1-owner-1-vote switch (triggered at 20% share demand), monthly operating and repair fund advances, sub-metered water and district heating settlements, 5-year developer statutory warranty tracking, mandatory 1-year and 5-year building inspection logs (c-KOB), and electronic court (e-Court) debt recovery filings.
- **US HOA & Condominium Mode**: CC&Rs, Bylaws, and rules enforcement, Architectural Review Committee (ARC/ACC) requests checked against state solar and EV-charging statutes, regular and special assessments, RUBS (Ratio Utility Billing System) utility splits, resale estoppel certificates, CAI / SIRS reserve studies, builder right-to-repair claims, and county assessment lien workflows.

## Functional Modules

### Property & Unit Registry
- Multi-property hierarchy: Community -> Building -> Stairwell / Floor / Vertical Riser Stack -> Unit (apartment, commercial premise, garage bay, storage room).
- Unit dossier: floor and storage area, ownership share fraction, co-owners and proxies, declared occupants, short-term rental flags, virtual IBAN / lockbox identifiers, and water, heating, and gas sub-meters with calibration expiry dates.
- 2D vertical riser cross-section: maps units across floors to shared water, sewage, and heating risers so vertical leak paths and shutoff points are immediately visible.
- Infrastructure photo atlas and access directory: annotated photos of basement shutoff valves, heating substations, gas levers, and breaker boards paired with stairwell intercom and key-safe codes attached to contractor dispatches.
- Public registry, statutory legal corpus, and local utility lookup: acquires applicable national property statutes, civil warranty articles, building inspection codes, and municipal ordinances alongside local electric/water/heating/gas/waste operators, outage feeds, tariff rates, and waste schedules from a property address or tax ID.

### Fund Accounting, Utilities & Reserves
- Double-entry general ledger separated into Operating, Repair/Reserve, Utilities Clearing, and Commercial Ancillary funds, enforcing balanced journal entries ($\sum \text{Debit} = \sum \text{Credit}$).
- Monthly assessment engine and tariff simulator: computes unit charges from area, share, occupant count, and rolling meter averages, and models the per-unit impact of heating, electricity, wage, or reserve rate adjustments.
- Sub-meter reading and bulk utility reconciliation: extracts meter serials and digits from resident photos or voice notes, flags abnormal consumption spikes (such as silent toilet valve leaks), and reconciles master utility invoices against sub-meters or RUBS formulas.
- Accounts payable, bank transfer matching, and contracted power auditing: matches incoming transfers with incomplete references to unit ledgers via FIFO arrears rules, verifies vendor invoices against completed work orders, and compares contracted vs peak measured power (`MW`/`kW`).
- 10-year reserve study and grant calculator: projects capital expenditure cashflows and evaluates net cost and payback periods for thermal modernization, solar PV, heat pump, and EV-charging grants.
- Arrears and legal recovery: tracks delinquency stages, installment schedules, pre-litigation demands, e-Court filings, and US HOA assessment liens and estoppel certificates.

### Maintenance, Inspections & Verification
- Request intake and responsibility check: processes resident text, voice, and photo reports, suppresses redundant contractor callouts when a municipal street outage is active, checks active 5-year developer warranties, and classifies faults as common property or private unit responsibility under the community statute.
- Defect measurement and BOQ estimation: segments damaged surfaces (`water stain`, `wall crack`, `spalling concrete`) with SAM 3.1, calculates affected area in square meters, and estimates material and labor costs from regional unit rates prior to contractor bidding.
- Completion verification gate and insurance claims: compares contractor "after" photos against the initial defect mask before releasing invoices for payment, and compiles insurance claim dossiers (incident timeline, riser map, measurements, invoices, and policy deductible summary) in PDF format.

### Governance, Voting & Minutes
- Statutory and community legal corpus: acquires, stores, and displays verbatim texts of national condominium statutes, civil warranty articles, construction inspection laws, municipal ordinances, and internal community governing documents (Statute / CC&Rs, House Rules, Utility Policy), citing exact articles across compliance checks, warranty notices, and debt recovery dossiers.
- Annual meeting preparation and statute compliance check: compiles annual financial statements and draft resolutions, verifying drafts against the full text of the acquired statutory corpus, community statute, house rules, and prior resolutions to catch procedural or majority-rule conflicts.
- Share-weighted electronic voting and paper ballot digitization: tallies online ballots by ownership share fraction and per capita, and extracts rows and signatures from photographed door-to-door paper voting sheets.
- Renovation previews and meeting minutes: generates visual comparisons for capital improvement ballots, printable stairwell notices with QR codes, and formal PDF/DOCX minutes from speaker-diarized meeting audio.

## Architecture & Stack

- **Runtime & UI**: .NET 10, C# 14, ASP.NET Core Blazor Server, Radzen.Blazor.
- **Persistence**: Entity Framework Core 10, SQL Server 2025 LocalDB (with in-memory test provider support).
- **Documents & Media**: QuestPDF, DocumentFormat.OpenXml, SixLabors.ImageSharp, QRCoder.
- **Model Integration (`https://api.meta.ai/v1`)**:
  - `muse-spark-1.3`: structured classification, statute compliance checks, OCR extraction, and data queries.
  - `muse-wonder`: multi-step workflows for public registry lookup, annual meeting packages, and insurance claim dossiers.
  - `muse-voice-transcribe-1.0`: voice maintenance reports and speaker-diarized meeting audio transcription.
  - `sam-3.1`: defect segmentation, surface area measurement, valve annotation, and before/after repair verification.
  - `muse-image-1.0`: architectural ballot previews and printable stairwell notices.

All financial, utility, reserve, cost-estimation, and voting calculations run in deterministic C# domain services covered by xUnit tests. All state-changing model proposals are placed in a review queue (`Pending Approvals`) for manager sign-off.

## Data Security & Privacy

- **Multi-Provider Authentication (`Facebook`, `Google`, `Apple`, and `Email`)**: Supports sign-in via Facebook OAuth, Google OpenID Connect, Sign in with Apple, and Email/Password (PBKDF2-SHA256) with role-mapped claims (`PropertyManagerAdmin`, `AccountingAuditor`, `MaintenanceDispatcher`, `BoardMemberReadOnly`, `ExternalContractorView`) and unit-owner account linking.
- **Outbound PII Redaction**: Before any text or structured context is sent to external model endpoints (`https://api.meta.ai/v1`), `DataSecurityAndPrivacyGuard` strips and tokenizes owner emails, phone numbers, bank account numbers (IBAN / ACH), tax identifiers, and physical building PINs into pseudonymous session tokens (`[OWNER_REF_A301]`, `[IBAN_MASKED]`, `[ACCESS_PIN_VAULTED]`).
- **Prompt-Injection & Untrusted Input Isolation**: External inputs (resident messages, voice transcripts, OCR text from invoices and paper ballots, bank transfer descriptions, and web bulletins) are sanitized and isolated inside non-executable data delimiters, blocking instructions attempting to alter ledger accounts, approve payouts, or exfiltrate access codes.
- **Role-Scoped Secret Vault & Least-Privilege Dispatch**: Stairwell intercom codes, boiler-room key-safe PINs, and owner contact details are masked by default according to the active workspace role (`PropertyManagerAdmin`, `AccountingAuditor`, `MaintenanceDispatcher`, `BoardMemberReadOnly`, `ExternalContractorView`). Contractor work orders receive only time-bounded contractor access codes rather than permanent resident codes.
- **Tamper-Evident SHA-256 Security Audit Log**: Secret reveals, approval queue decisions, legal dossier exports, and blocked injection attempts are recorded in a hash-chained `SecurityAuditEvent` log (`PreviousChainHash` -> `EntrySha256Hash`) verifiable at runtime.
- **Multi-Community Tenant Isolation & Transport Hardening**: Every community-scoped entity enforces `ICommunityScoped` tenant boundaries, ASP.NET Core Data Protection (`IDataProtectionProvider`), antiforgery validation, and strict HTTP security headers (`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy`).

## Building and Running

```powershell
dotnet build CommonGround.slnx -warnaserror
dotnet test CommonGround.slnx
dotnet run --project src/CommonGround.Web
```

To configure live Meta Model API credentials (optional; deterministic local fallbacks are enabled by default for offline testing):

```powershell
dotnet user-secrets set "MetaModels:ApiKey" "<YOUR_API_KEY>" --project src/CommonGround.Web
```
