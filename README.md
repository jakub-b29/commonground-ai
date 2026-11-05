# CommonGround

Property management, fund accounting, computer-vision maintenance verification, and statutory governance software for firms that manage residential housing associations and condominiums.

An independent management firm typically assigns 15 to 30 properties (400 to 1,200 units) to one property manager. Most existing systems only record accounting entries after the fact. Managers reconcile utility sub-meters in spreadsheets, check statutory compliance by hand, chase contractors by phone, and type meeting minutes from audio recordings. CommonGround combines the unit and meter registry, fund-separated double-entry accounting, work order verification, owner voting, and an AI operations assistant in one multi-firm web application.

Built for the [Meta Global AI Developer Hackathon, Cerebral Valley](https://cerebralvalley.ai/e/meta-ai-developer-hackathon).

| | |
|---|---|
| Runtime | .NET 10, C# 14, Blazor Web App (Interactive Server) |
| Database | SQL Server 2025 LocalDB via EF Core 10 (in-memory provider for tests) |
| Meta models | `muse-spark-1.3`, `muse-wonder`, `muse-voice-transcribe-1.0`, `sam-3.1`, `muse-image-1.0` |
| Interface languages | English, Polish, Spanish, French, German |
| Verification | `dotnet build -warnaserror`: 0 warnings, 0 errors. `dotnet test`: 160 passed, 0 failed |

## Describe your use of Meta products to build the submitted project

CommonGround calls five Meta models through the Meta Model API (`https://api.meta.ai/v1`). Each model has a specific job in a workflow; none is used as a generic chatbot.

| Model | Endpoint | Where it is used in CommonGround |
|---|---|---|
| `muse-spark-1.3` | `/v1/responses` (with `web_search` grounding where allowed) | Agentic operations assistant (ReAct loop with a live tool catalog), portfolio question answering, address-driven property discovery (utility operators, gross tariffs, emergency authorities, national statutes), multimodal resident request triage and translation, statute compliance checks over the full governing corpus, meter dial OCR, paper ballot OCR, neighbor mediation and arrears notice drafting, ARC and renovation permit evaluation |
| `muse-wonder` | `/v1/responses` (multi-step workflows) | Public registry and legal corpus synchronization, annual meeting package compilation, insurance claim dossier assembly |
| `muse-voice-transcribe-1.0` | `POST /v1/asr/transcribe` (multipart `request` JSON + `audio` WAV) | Live browser microphone transcription for resident reports and the assistant chat (`PUSH_TO_TALK`), speaker-diarized meeting minutes (`DIARIZATION`) |
| `sam-3.1` | Segmentation | Defect masks (`wall crack`, `water stain`, `spalling concrete`, `damaged handrail`), affected area in m², basement valve and gas lever annotation, before/after repair verification (remaining defect below 5% of original area) |
| `muse-image-1.0` | Image generation | Option A / Option B renovation ballot previews, QR stairwell notice posters, multilingual waste segregation posters, and custom annotated 640x400 PNG visuals with user-specified title, info lines, and QR link |

How the integration is built:

- **Typed clients behind interfaces.** `IMuseSparkClient`, `IMuseWonderClient`, `IMuseVoiceTranscriber`, `ISam31Client`, and `IMuseImageClient` are implemented by `MetaModelClients` in `src/CommonGround.Infrastructure/MetaModels`. Prompts live in `MuseSparkDiscoveryPrompts`.
- **Agentic loop.** `MuseSparkAutomationAgentService` runs a Think, Tool Call, Observation, Final Answer loop. The model chooses from a tool catalog (query entities, create units, register residents, run utility settlement, generate images, and others). Every tool call is checked against the user's permissions before it executes, and step-by-step mode pauses between steps.
- **Voice path.** The browser records microphone audio, encodes 16 kHz mono 16-bit PCM into a RIFF/WAVE file (`wwwroot/js/voice-recorder.js`), and the server posts it as `multipart/form-data` to `/v1/asr/transcribe` with the `muse-voice-transcribe-1.0` request settings.
- **Language handling.** The assistant answers in the language selected in the UI and is instructed to search for data in that language and in the property's country-native language, so tariff and statute lookups hit local sources.
- **Clean output.** Chat shows human-readable summaries, highlights, and generated images. Raw JSON is never rendered in the chat.
- **Offline behavior.** When no API key is configured, deterministic fallbacks keep every workflow runnable for local development and tests. Live calls start as soon as a key is present.

## Why are Meta's models important to your solution?

Property management work is not text-only. A single request can arrive as a voice note in Ukrainian, a photo of a stained ceiling, a scan of a signed paper ballot, or a bank transfer title with no reference. The Meta model family covers each of those inputs, so one platform can handle the full workflow instead of stitching together unrelated vendors.

- **Multimodal triage in one pass.** `muse-spark-1.3` reads the resident message, translates it for the manager, checks the riser stack, active municipal outages, and 5-year developer warranty, and classifies common property versus private unit responsibility with a verbatim statutory citation.
- **Measurement that feeds cost estimates.** `sam-3.1` turns a photo into a defect mask and an area in m². That area goes to a deterministic bill-of-quantities estimator, so contractor bids can be compared with an independent figure. After the repair, the same concept is segmented again to decide whether the invoice can be paid.
- **Voice as a first-class input.** Residents and managers often cannot type a detailed report. `muse-voice-transcribe-1.0` gives live transcription from the browser microphone and diarized minutes for owner meetings, which are then exported as PDF and DOCX.
- **Long-context legal checking.** Housing law differs by country and by community statute. `muse-spark-1.3` can load the community's governing documents and the acquired national statutes in one pass to flag an unlawful clause or a wrong majority rule, and returns a one-click compliant rewrite.
- **Grounded discovery.** From an address alone, web-search-grounded `muse-spark-1.3` identifies local utility operators, current gross tariffs, emergency authorities, and statutes in the local language, which removes most of the manual setup for a new property.
- **Communication artifacts.** `muse-image-1.0` produces ballot option previews and printable stairwell notices with QR codes, and can render specific information that the manager supplies directly onto the image.
- **Multilingual operation.** Residents in one building commonly speak several languages. The models translate in both directions and keep the assistant in the interface language the user selected.

Models do not decide anything that involves money, votes, or access. All balances, utility splits, reserve cashflows, cost totals, vote tallies, and hashes are computed by deterministic C# services covered by tests. Every state-changing proposal from a model is stored as an `AiAction` and waits for manager approval. This split is what makes it reasonable to let a model act inside a financial system.

## Operating Modes

The application supports two statutory operating profiles, selectable per property:

- **Housing Association Mode (European condominium)**: ownership share fractions (`625 / 10,000`), statutory 1-owner-1-vote switch at 20% share demand, monthly operating and repair fund advances, bulk-to-sub-meter water and district heating reconciliation, 5-year developer warranty tracking, mandatory 1-year and 5-year building inspection logs, and e-Court payment order dossiers.
- **US HOA and Condominium Mode**: CC&Rs and bylaws enforcement, Architectural Review Committee requests checked against state solar and EV-charging statutes, RUBS utility splits, 20% assessment increase cap checks, Structural Integrity Reserve Studies, builder right-to-repair claims, county assessment liens, and resale estoppel certificates.

## Functional Modules

### Property and unit registry
- Hierarchy: Management Firm, Community, Building, Stairwell, Floor, Vertical Riser Stack, Unit.
- Interactive building builder with floor and stairwell matrix sizing, riser stack creation, and bulk unit generation.
- Unit dossier with ownership share, co-owners, occupants, sub-meters, and calibration dates. Resident profiles, assignments, household members, and document vault.
- Vertical riser cross-section that shows leak paths and shutoff points, plus an infrastructure atlas of annotated valves and levers with role-gated access codes.
- Three-step AI onboarding wizard that creates a property from an address and merges a reviewed dossier.

### Fund accounting, utilities, and reserves
- Double-entry ledger separated into Operating, Repair and Reserve, Utilities Clearing, and Commercial Ancillary funds.
- Monthly assessment engine and tariff and inflation simulator.
- Meter reading from photos, anomaly detection (readings above 2.5 times the rolling average), and bulk utility reconciliation or RUBS allocation.
- Bank transfer matching with FIFO arrears allocation, contracted power auditing, 10-year reserve study, and grant payback calculator.
- Arrears staging, installment plans, pre-litigation notices, and legal recovery dossiers.

### Maintenance and verification
- Omnichannel intake with outage suppression, warranty routing, and boundary responsibility.
- Walkthrough video splitting into geo-tagged work orders, defect measurement, BOQ estimation, completion verification gate, and insurance claim dossier PDF.

### Governance and minutes
- Statutory and community legal corpus with verbatim citations.
- Annual meeting package, statute compliance check with one-click rewrite, share-weighted voting, and paper ballot digitization.
- Renovation visualizer, stairwell QR notices, and diarized meeting minutes exported as PDF and DOCX.

### Multi-firm administration
- Firm registration, hashed invitations, join-by-firm-code queue, multi-property assignment scopes, and per-member permission matrices.
- Roles: Firm Manager, Property Manager, Accountant, Maintenance Dispatcher, Board Member (read-only), Resident, Contractor, External Auditor (view).

## Architecture

```
src/
  CommonGround.Domain           Entities, enums, deterministic calculators
  CommonGround.Application      Workspace services, agent orchestration, interfaces
  CommonGround.Infrastructure   EF Core persistence, Meta model clients, PDF/DOCX/image rendering
  CommonGround.Web              Blazor UI, authentication, localization, export endpoints
tests/
  CommonGround.UnitTests        Calculators, workspace integration, security regression, assistant tests
```

- **Deterministic calculators** (`AssessmentCalculator`, `UtilitySettlementEngine`, `ReserveStudyCalculator`, `BoqCostEstimator`, `VotingCalculator`, `LedgerService`, and others) own all numbers.
- **Documents and media**: QuestPDF, DocumentFormat.OpenXml, SixLabors.ImageSharp, QRCoder.
- **Design system**: dual theme (Neo-Dark and Premium Light) through semantic CSS tokens, with reusable bento, metric, badge, and insight components.

## Security and Privacy

- **Outbound PII redaction.** Every prompt to the Meta API passes through `DataSecurityAndPrivacyGuard.RedactPiiForModelPrompt`, which replaces owner emails, phone numbers, IBANs, tax IDs, and building PINs with pseudonymous tokens.
- **Prompt-injection guard.** Resident messages, transcripts, OCR text, bank transfer titles, and web content are wrapped as untrusted input. Attempts such as `ignore previous instructions` or `reveal access code` are blocked and logged.
- **Property-aware permissions for the assistant.** When a question names a property (by name, `COM-` account number, or address), the assistant resolves the highest-privilege approved membership the user holds for that property and uses the union of its granted permissions. It never exceeds the permissions of the authenticated user.
- **Tenant isolation.** Every community-scoped entity carries a community id and belongs to a firm. Queries and exports enforce both firm ownership and claim-scoped property access.
- **Secret vault and least-privilege dispatch.** Intercom codes, key-safe PINs, and resident contact details are masked by role. Contractors receive temporary codes only.
- **Tamper-evident audit chain.** Secret reveals, AI action decisions, exports, membership changes, and blocked injections are written to a SHA-256 hash-chained `SecurityAuditEvent` table that can be verified at runtime.
- **Transport and session.** HttpOnly cookie authentication, ASP.NET Core Data Protection, antiforgery, rate-limited `/auth/*` endpoints, and strict security headers.

## Interface Languages

The full workspace is available in English, Polish, Spanish, French, and German. Dictionaries are in `src/CommonGround.Web/wwwroot/i18n/{en,pl,es,fr,de}.json` and are loaded on demand by `WorkspaceLocalization`. A unit test verifies that required keys exist and are non-empty in all five files. The AI assistant follows the selected language for replies and for search queries.

## Building and Running

Prerequisites: .NET 10 SDK and SQL Server LocalDB (the app falls back automatically when LocalDB is unavailable).

```powershell
dotnet build CommonGround.slnx -warnaserror
dotnet test CommonGround.slnx
dotnet run --project src/CommonGround.Web
```

The app seeds two demo communities: Amber Park Residence in Warsaw (Housing Association mode) and Oakridge Palms Condominium Association in Miami (US HOA mode).

To enable live Meta model calls, set an API key. Without one, deterministic offline fallbacks are used.

```powershell
dotnet user-secrets set "MetaModels:ApiKey" "<YOUR_KEY>" --project src/CommonGround.Web
```

The key can also be supplied through the `META_KEY` or `MODEL_API_KEY` environment variables. Keys are never committed to the repository.

## Demo Walkthrough

1. Sign in and open the Portfolio Assistant or the Spark automation chat. Ask a question in any of the five languages, for example a settlement run for a specific property by name.
2. Open the chat microphone panel, record a short report, and stop. The WAV file is transcribed through `muse-voice-transcribe-1.0` and placed in the input box.
3. Open the image studio in the chat, enter a title and info lines, and generate a 640x400 PNG with the model, resolution, and latency shown next to the image.
4. Go to Maintenance, submit a resident leak request, and review the routing, boundary decision, area measurement, BOQ estimate, and the proposed action in Pending Approvals.
5. Go to Governance, generate Option A / Option B previews with custom info lines, and export meeting minutes as PDF or DOCX.
6. Switch the interface language from the header and confirm that labels and assistant replies follow it.
