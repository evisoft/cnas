# Annex 1 & 2 — Business Processes for Contributors and Insured Persons

Source: `tor/TOR.md` lines 7140-11206 (PDF pages 87-168).

---

## Annex 1 — Plătitori de contribuții (Contribution Payers)

### Section 1.1 — Gestionarea Registrului plătitorilor de contribuții (Contributor Registry Management)

**Regulatory framework:**
- Law no. 489-XIV/1999 on the public social insurance system (with amendments)
- Annual State Social Insurance Budget Law
- Gov. Decision no. 399/2021 — Regulation on administering the State Register of Individual Records in the public social insurance system
- CNAS Order no. 275-A/2015 — Regulation on the records of contribution payers to BASS (with amendments)

---

### BP 1.1-A — Register/modify/delete contributor based on info from RSUD (State Register of Legal Entities)

**Trigger:** Automated, scheduled (configurable, default daily) OR manual launch by Administrator role.
**Actors:**
- MConnect (gateway to RSUD)
- Sistem (SI PS — the system)
- Utilizator CNAS (CNAS/CTAS user — monitoring)
**Inputs (data / documents):**
- Lista unităților de drept noi sau modificate (List of new or modified legal units) — electronic doc
- Datele din RSUD privind unitatea de drept solicitată (RSUD data on requested legal entity) — electronic doc
**Outputs (data / documents / decisions):**
- Certificatul privind luarea la evidență în calitate de plătitor de contribuții la BASS (Certificate of registration as BASS contributor) — placed in payer's cabinet
- Notificare privind rezultatele sincronizării cu RSUD (Notification on RSUD sync results) — to admin and CNAS user; duplicated to MCabinet via MNotify
**External systems touched:** RSUD (via MConnect), MCabinet (via MNotify), MLog
**Process steps (numbered):**
1. **1.1-A01 — System launches data-pull procedure from RSUD.** Extracts the list of newly registered legal entities and entities that have changed (including dissolved). If list is empty, process ends.
2. **1.1-A02 — Update classifiers from RSUD.** System checks if RSUD classifiers have changed and updates classifiers used in the system.
3. **1.1-A03 — Process list of legal entities.** For each record, apply validation and storage rules. On validation failure, log event and error details, skip record. Rules:
   - Check if payer already exists by IDNO and/or fiscal code
   - If found, create new record and move previous to modification history (preserve history per payer with update date)
   - If not found, create new record and assign new CNAS code
   - After processing, update processing status in RSUD
4. **1.1-A04 — Generate output documents.** For each new payer generate "Certificatul privind luarea la evidență" (BASS Contributor Certificate) and place in payer's personal cabinet. Also create notification in personal cabinet duplicated to MCabinet via MNotify. If insolvency procedure initiation info is detected, system notifies the CNAS user responsible for managing the insolvent payers registry per each identified case. Send results notification to System Administrator and CNAS User.
**Decision points / rules:**
- Validation rule failure → skip record, log error
- New payer → generate CNAS code automatically
- CTAS code assigned based on legal address
- For existing payer → preserve modification history
- Insolvency detection → notify insolvency registry manager
- Editing option: when source is ASP and data registered/modified since 2022, no modification allowed; manually entered data is editable.
**Diagram present:** Figura A1.1 (no full-page image referenced for this BP, only text-based BPMN)
**Edge cases / exceptions:**
- Empty list from RSUD → terminate process
- Validation error per record → ignore record, continue
- Classifier change → auto-update
**Status transitions:** Plătitor states (stored as `Starea actuală`): 0=Activ, 1=Suspendat activitate, 2=Procedura insolvabilității, 3=Proces de lichidare, 4=Lichidată

---

### BP 1.1-B — Register/modify/delete payer based on info from SFS (State Tax Service)

**Trigger:** Automated, scheduled (configurable, default daily) OR manual launch by Administrator.
**Actors:**
- MConnect (gateway to SFS)
- Sistem (SI PS)
- Utilizator CNAS (monitoring)
**Inputs (data / documents):**
- Lista persoanelor fizice noi sau modificate (List of new/modified individuals) — electronic doc
- Datele din SFS privind persoana fizică solicitată (SFS data on requested individual) — electronic doc
**Outputs (data / documents / decisions):**
- Certificatul privind luarea la evidență (BASS Contributor Certificate) — placed in personal cabinet
- Notificare privind rezultatele sincronizării cu SFS (SFS sync results notification) — to admin and CNAS user
- Transactions registered in personal accounts (Plătitor and Persoana asigurată) when patentă contributions are computed
**External systems touched:** SFS (via MConnect), MCabinet, MNotify, RSP (for new insured persons)
**Process steps (numbered):**
1. **1.1-B01 — Launch data-pull from SFS.** System launches the procedure per schedule. Extract list of new/modified individuals (incl. deletions). If empty, terminate; otherwise continue. Result viewable by responsible specialists per declaration type and processing status.
2. **1.1-B02 — Process individuals — Contribution Payers.** For each record apply rules:
   - Check if payer registered previously by IDNP
   - If found, create new record with previous moved to history (per-payer history with update date)
   - If not found, create new record, assign new CNAS code and CTAS code
3. **1.1-B03 — Process individuals — Insured Persons.** Apply rules to insured persons registry:
   - Check if individual exists in registry
   - If not, create new record, assign new CPAS code, fetch data from RSP
4. **1.1-B04 — Calculate contributions for patentă (entrepreneurship patent) holders.** For this type of payer, calculate/recalculate contributions and reflect in payer's and insured person's personal accounts based on rules:
   - Calculate social insurance contribution for entire requested period of patent issuance/renewal
   - Determine persons for whom calc applies, excluding those exempt
   - Determine contribution tariff for the calculation period
   - Calculate the social insurance contribution
5. **1.1-B05 — Generate output documents.** Generate BASS Contributor Certificate for new payer and place in personal cabinet; create notification in cabinet duplicated to MCabinet via MNotify. Send results notification to System Administrator and CNAS User.
**Decision points / rules:**
- Categories subject to registration: individuals practicing independent activities; patentă holders requesting issuance/renewal; other types per legislation
- New individual payer → generate CNAS code; assign CTAS by legal address
- New insured individual → generate CPAS ID; assign CTAS by domicile address
- Patentă holders → automatic monthly-style contribution calc
- Validation failure → ignore record, log error
**Diagram present:** Figura B1.1; `images/pdf_p092_full.png` referenced on PDF page 92
**Edge cases / exceptions:**
- Empty SFS list → end
- Exempt persons → skip contribution calc
- Validation error → log + skip
**Status transitions:** Implicit on patentă activity periods (start/end) producing contribution lines.

---

### BP 1.1-C — Register/modify/delete payer based on paper request filed at CNAS

**Trigger:** On demand (paper request filed at CTAS).
**Actors:**
- Sistem (SI PS)
- Solicitant (Applicant — natural or legal person)
- Utilizator CNAS (CTAS specialist)
**Inputs (data / documents):**
- Cerere (Application) — paper
- Documentele obligatorii atașate (Mandatory attached documents) — paper
**Outputs (data / documents / decisions):**
- BASS Contributor Certificate — placed in personal cabinet
- Notificare privind executarea cererii (Notification of request execution)
**External systems touched:** MCabinet (via MNotify) for notifications
**Process steps (numbered):**
1. **1.1-C01 — Register request.** CNAS User fills in the system registration/modification form based on the paper application and attaches the scanned application. System validates the form. On success, form is sent to processing. On failure, user is notified of deviations and granted edit access.
2. **1.1-C02 — Process the request.** Rules:
   - If the last update in the Registry was based on data pulled from RSP/RSUD or SFS, the request is rejected
   - Check if the payer is already registered (by IDNO/IDNP/Fiscal Code)
   - If found, update record with previous moved to history
   - If not found, new record + new CNAS code + new CTAS code
3. **1.1-C03 — Generate output documents.** Generate BASS Contributor Certificate, place in personal cabinet, create notification duplicated to MCabinet via MNotify.
**Decision points / rules:**
- Cannot accept paper request if last update came from authoritative external source (RSP/RSUD/SFS)
- For each request type the system uses configured registration interface, output document template (certificate, refusal letter, etc.), receipt template
- Mandatory document list configured per request type (not enforced for proactive services)
- Per-payer electronic dossier maintained
**Diagram present:** Figura C1.1
**Edge cases / exceptions:**
- Validation failure → return to user for editing
- Conflict with authoritative source → reject
**Status transitions:** Application states implicit: registered → processed (or rejected) → certificate generated.

---

### BP 1.1-D — Register supplementary data in the Contributor Records Registry

**Trigger:** As needed (manual registration of supplementary CNAS-administered data).
**Actors:**
- Utilizator CNAS
- Sistem (SI PS)
**Inputs (data / documents):**
- Formular de înregistrare (Registration form) — electronic
- Documentul atașat (Attached document) — electronic (scan)
**Outputs (data / documents / decisions):** Registry updated with supplementary data; history preserved
**External systems touched:** None (internal)
**Process steps (numbered):**
1. **1.1-D01 — Form entry.** CNAS User completes the registration form and attaches the scanned document. Validation rules apply.
2. **1.1-D02 — Process form.** System updates Contributor Registry with form data per registration rules.
**Decision points / rules:**
- Separate interface (entry form) provided for supplementary data
- Document on which entries are based must be attached
- History preserved per payer
- Process logged (start time, modifications made, end time)
**Diagram present:** Figura D1.1
**Edge cases / exceptions:** Validation rule violation on form
**Status transitions:** Form: drafted → submitted → processed.

---

### BP 1.1-E — Explore the Contributors Registry

**Trigger:** As needed (read-only browsing).
**Actors:**
- Utilizator CNAS
- Sistem (SI PS)
**Inputs (data / documents):**
- Search form (search by selected register field)
- Filter form (filter by field values)
**Outputs (data / documents / decisions):**
- File exported in XLS, CSV, or PDF
**External systems touched:** None
**Process steps (numbered):**
1. **1.1-E01 — Explore registry.** System provides a viewer interface for the Contributors Registry with search and filter tools by field. On double-click on a Payer, system opens a tabbed interface including: modification history, payer info, submitted declarations info, and other info determined at design time. Any tab may be exported to XLS, CSV, or PDF. Search/filter results may be exported. Processes specific to the payer type may be launched from the interface (modification, report generation, etc.).
**Decision points / rules:**
- View-only mode with search/filter
- Process events logged
- Export to XLS/CSV/PDF supported
**Diagram present:** (no specific image)
**Edge cases / exceptions:** None explicit
**Status transitions:** N/A

---

### Registry: Registrul plătitorilor de contribuții (Contributors Registry — section 8.1.1.6)

| Field | Type | Required | Description | Source/External link |
|---|---|---|---|---|
| Cod CNAS (CNAS Code) | string | yes | Internal payer identifier assigned by the system | Generated by CNAS |
| Cod CTAS (CTAS Code) | code (classifier) | yes | Territorial CTAS code assigned based on legal address | Classifier |
| IDNO/IDNP | string | yes | State registration identifier (legal person IDNO; natural person IDNP) | RSUD / RSP |
| Număr de înregistrare (Registration number) | string | yes | Registration number | RSUD |
| Cod Fiscal (Fiscal Code) | string | yes | Tax identification code | SFS / RSUD |
| Denumirea completă (Full name) | string | yes | Full legal name | RSUD |
| Denumirea prescurtată (Short name) | string | no | Short/abbreviated name | RSUD |
| Data înregistrării (Registration date) | date | yes | Date of registration | RSUD |
| Organul înregistrării (Registration body) | code (classifier) | yes | Registering authority (ASP, SFS, Manual, ...) | Classifier |
| Cauza lichidării (Reason of liquidation) | code (classifier) | conditional | Reason of liquidation | Classifier |
| Starea actuală (Current state) | enum | yes | 0=Active, 1=Activity suspended, 2=Insolvency procedure started, 3=Liquidation process started, 4=Liquidated | Internal/RSUD |
| Data înregistrării stării actuale (Date of current state registration) | date | yes | When the current state was set | Internal |
| Organul subordonat (Subordinate body) | code (classifier) | no | Subordinate authority | Classifier |
| Forma organizatorico-juridică (Legal form) | code (classifier) | yes | Legal organizational form | Classifier |
| Categoria plătitorului (Payer category) | code (classifier) | yes | Payer category | Classifier |
| Tipul persoanei juridice (Type of legal person) | code (classifier) | yes | Type of legal entity | Classifier |
| Tipul gestionării (Management type) | code (classifier) | no | Management type | Classifier |
| Forma de proprietate (Form of ownership) | code (classifier) | yes | Ownership form | Classifier |
| Capital statutar în lei (Statutory capital in MDL) | decimal | no | Statutory capital, MDL | RSUD |
| Capital de stat în lei (State capital in MDL) | decimal | no | State capital, MDL | RSUD |
| Capital străin (Foreign capital) | decimal | no | Foreign capital | RSUD |
| Codul valutei capitalului străin (Foreign capital currency code) | code (classifier) | no | Currency code | Classifier |
| **Address data** | | | | |
| Codul CUATM (CUATM code) | code (classifier) | yes | Administrative-territorial classifier code | Classifier |
| Raionul (District) | string | yes | District | RSUD |
| Localitatea (Locality) | string | yes | Locality | RSUD |
| Strada (Street) | string | no | Street | RSUD |
| Casa (House) | string | no | House number | RSUD |
| Blocul (Block) | string | no | Building block | RSUD |
| Apartamentul (Apartment) | string | no | Apartment | RSUD |
| Telefon (Phone) | string | no | Phone | RSUD |
| Fax | string | no | Fax | RSUD |
| Email | string | no | Email | RSUD |
| **Activity types data** | | | | |
| Numărul de ordine (Order number) | int | yes | Sequence number for activity entry | Internal |
| Tipul genului de activitate (Type of activity) | enum | yes | 1=Licensed, 0=Not licensed | RSUD |
| Codul genului de activitate (Activity code) | code (classifier) | yes | NACE/CAEM code | Classifier |
| **Directors data** | | | | |
| IDNP (director) | string | yes | Director's personal ID | RSP |
| Numele (Last name) | string | yes | Last name | RSP |
| Prenumele (First name) | string | yes | First name | RSP |
| Patronimicul (Patronymic) | string | no | Patronymic | RSP |
| Data nașterii (Birth date) | date | yes | Date of birth | RSP |
| Adresa (Address) | string | yes | Address | RSP |
| **Founders data** | | | | |
| Tipul fondatorului (Founder type) | enum | yes | 1=Natural person, 2=Legal person | RSUD |
| Cota fondatorului în capitalul statutar în % (Founder share %) | decimal | yes | Founder's share in statutory capital (%) | RSUD |
| Capitalul fondatorului în capitalul statutar (Founder capital) | decimal | yes | Founder's capital | RSUD |
| Codul valutei capitalului fondatorului (Founder capital currency code) | code (classifier) | no | Currency code | Classifier |
| Founder (natural person): IDNP | string | conditional | If founder is natural person | RSP |
| Founder (natural person): Name, surname, patronymic | string | conditional | If natural person | RSP |
| Founder (natural person): Birth date | date | conditional | If natural person | RSP |
| Founder (legal person): IDNO | string | conditional | If founder is legal person | RSUD |
| Founder (legal person): Short name | string | conditional | If legal person | RSUD |
| **Last reorganization data** | | | | |
| Participant IDNO | string | conditional | Participant IDNO in reorganization | RSUD |
| Participant Denumirea (Name) | string | conditional | Participant name | RSUD |
| Tipul reorganizării (Type of reorganization) | code (classifier) | conditional | Type per classifier | Classifier |
| Data inițierii reorganizării (Date of initiation) | date | conditional | Date when reorganization was initiated | RSUD |
| Starea reorganizării (Reorganization state) | code (classifier) | conditional | State of the reorganization | Classifier |
| Cauza finisării reorganizării (Reason of completion) | code (classifier) | conditional | Reason completion ended | Classifier |
| Data înregistrării stării actuale (Date of current state registration) | date | conditional | Date current reorg state recorded | Internal |
| **Insolvency procedure data** | | | | |
| Data intentării procedurii de insolvabilitate (Date of insolvency initiation) | date | conditional | Insolvency initiation date | Court/Internal |
| Data încetării procedurii de insolvabilitate (Date of insolvency termination) | date | conditional | Insolvency termination date | Court/Internal |
| IDNO al succesorului (Successor IDNO) | string | conditional | Successor's IDNO | RSUD |
| **CNAS-administered data** | | | | |
| Rezident Park IT — Data înregistrării (IT Park resident — registration date) | date | conditional | IT Park registration date | CNAS |
| Rezident Park IT — Data ieșirii (IT Park resident — exit date) | date | conditional | IT Park exit date | CNAS |
| **Contribution data** | | | | |
| Contribuții calculate (Calculated contributions) | decimal | yes | Calculated social insurance contributions | CNAS |
| Contribuții plătite la BASS (Contributions paid to BASS) | decimal | yes | Paid contributions to State Social Insurance Budget | CNAS |

**Mențiuni / Notes:**
1. **Edit option:** If source is ASP and data was registered/modified from 2022 onward — no modification allowed. If manually entered — editable.
2. Modifications are journaled, with full history preserved.
3. Registry structure may be modified/extended during business analysis.

---

### Section 1.2 — Calcularea și achitarea contribuțiilor (Calculation and Payment of Contributions)

**Regulatory framework:**
- Law no. 489-XIV/1999; CNAS Order no. 275-A/2015; CNAS Order 233-A/2022 (data correction); CNAS Order 247-A/2022 (payment corrections); SFS Order 1108/2015 (specifically recorded debts); CNAS Order 96-A/2023 (recovery of incorrectly paid social benefits from employer)

---

### BP 1.2-A — Register data from SFS-received declarations

**Trigger:** Automated, scheduled (configurable, default daily) OR manual launch by Administrator.
**Actors:**
- MConnect (gateway to SFS)
- Sistem (SI PS)
- Utilizator CNAS (monitoring)
**Inputs (data / documents):**
- Lista dărilor de seamă/declarații noi (List of new declarations/reports) — electronic
- Datele din darea de seamă/declarația solicitată (Declaration data) — electronic
**Outputs (data / documents / decisions):**
- Transactions recorded in payers' personal accounts
- Transactions recorded in insured persons' personal accounts
- Notification on SFS sync results
**External systems touched:** SFS (via MConnect)
**Process steps (numbered):**
1. **1.2-A01 — Launch data pull from SFS.** Procedure runs per schedule for each declaration type. Extract list of new declarations per type. If overall list empty → end; else continue. Result list accessible to specialists per declaration model and processing status.
2. **1.2-A02 — Process declarations — Payers side.** For each declaration apply validation and storage rules in Declarations Registry and Payer's personal account. Rules:
   - From declaration of the same form, identify the payer, fiscal period, type, other data (budget classification per classifier), payer category, contribution rate, and amount calculated
   - If a prior declaration for the same period exists, prior declaration's transactions may be reversed/stornoed depending on declaration type
   - On validation failure: ignore record, log event + error details, move on
3. **1.2-A03 — Process declarations — Insured persons side.** Apply rules:
   - Check if insured person exists by IDNP; if not, fetch from RSP and create new with new CPAS code
   - From declaration: identify period (month/year), person category, function code, remuneration fund, calculated contributions, and labor relationship info from IPC 18 (table 2), DSA 18, IRM 19
   - If declaration is a correction type, it replaces the primary one (with history retained)
   - After processing, update declaration status in SFS
   - Send sync results notification to System Administrator and CNAS User
**Decision points / rules:**
- Different declaration types: IPC18, IPC21, DSA, IU17, TAXI18, CAS18-AN, IRM19; each with its own validation/registration rules
- Records of social insurance contributions kept by: payer, budget classification, payer category, contribution rate, fiscal period, insured person
- Payment deadlines per month registered at year start (modifiable)
- Declaration identifier = SFS-issued identifier
- Correction-type declaration replaces primary, history preserved
**Diagram present:** Figura A1.2
**Edge cases / exceptions:** Validation failure → log, skip
**Status transitions:** Declaration: pending → processed (validated/registered) OR error; supersedes any prior correction.

---

### BP 1.2-B — Register data from declarations submitted at CNAS (4-BASS, BASS, BASS-AN)

**Trigger:** On request (paper declaration submitted at CNAS).
**Actors:**
- Solicitant (natural/legal applicant)
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- Darea de seamă pe suport de hârtie (Paper declaration)
**Outputs (data / documents / decisions):**
- Transactions recorded in payers' current accounts
**External systems touched:** None
**Process steps (numbered):**
1. **1.2-B01 — Register declaration.** CNAS User registers the received declaration. System provides separate interface (entry form) per declaration type. System checks declaration against validation rules (per type). On error, CNAS specialist is informed.
2. **1.2-B02 — Process declaration.** If no errors, system processes generating entries in Declarations Registry + Payer's current account. Rules:
   - From declaration identify: fiscal period, budget classification of income (per classifier), payer type, contribution rate, calculated amount
   - If prior declaration found, also register a storno (reversal) transaction as needed
**Decision points / rules:**
- Only declarations for periods before 01.01.2018 are registered via this process
- System generates a unique identifier for each declaration
- Contribution records by: payer, budget classification, payer category, fiscal period
**Diagram present:** Figura B1.2
**Edge cases / exceptions:** Validation errors prevent processing; prior declaration triggers storno
**Status transitions:** Declaration: registered → processed.

---

### BP 1.2-C — Register contributions calculated/reduced from other documents

**Trigger:** On request (e.g., control act, internal CNAS decision, court decision, etc.).
**Actors:**
- Solicitant (Payer)
- Sistem (SI PS)
- Utilizator CNAS
- Șeful direcției (Department Head)
**Inputs (data / documents):**
- Document pe suport de hârtie (Paper document scanned and attached)
**Outputs (data / documents / decisions):**
- Transactions recorded in payers' current accounts
- Notification on document status
**External systems touched:** None
**Process steps (numbered):**
1. **1.2-C01 — Register document.** CNAS User registers and attaches scanned document. System validates entered data. On error, document doesn't advance. Otherwise, user routes document to Department Head.
2. **1.2-C02 — Review document.** Department Head reviews registered document and attachment. If no objections, route for processing. Otherwise return to user (step 1.2-C01).
3. **1.2-C03 — Process document.** System generates entries in Payer's current account. Rules:
   - From document identify: fiscal period, budget income classification (per classifier), payer category, contribution rate, amount calculated/reduced, late penalty surcharges, fines
   - Generate notifications to affected payers
**Decision points / rules:**
- Document types include: control acts, internal CNAS decisions, court decisions, administrative sanctions, subsidiary liability, recovery from payer of incorrectly paid amounts, sums for special-record entries/removals, etc. (final list at design)
- Each document type → separate registration form + validation rules + transaction formation rules
- Each document type has role with registration right
- Unique identifier per document
- Records by: payer, budget classification, fiscal period
**Diagram present:** Figura C1.2
**Edge cases / exceptions:** Validation failure; Department Head rejection
**Status transitions:** Document: registered → under review → approved/returned → processed.

---

### BP 1.2-D — Monthly contribution calculation

**Trigger:** Automated, scheduled (configurable, default monthly) OR manual launch by Administrator.
**Actors:**
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- None explicit (uses registries)
**Outputs (data / documents / decisions):**
- Transactions recorded in payers' personal accounts
- Transactions recorded in insured persons' personal accounts
- Procedure results notification
**External systems touched:** None (internal)
**Process steps (numbered):**
1. **1.2-D01 — Launch procedure.** System determines list of payers for whom calculations will be performed. Result: transactions attached to new and closed management periods.
2. **1.2-D02 — Calculate/recalculate contributions.** For each payer type/category, calculate/recalculate contributions and record in Declarations Registry + personal accounts:
   - Determine eligible persons (exclude exempt)
   - Determine contribution tariff for the period
   - Determine number of activity days in calculation period
   - Calculate social insurance contribution
3. **1.2-D03 — Generate output documents.** Send results notification to System Administrator and CNAS User.
**Decision points / rules:**
- Monthly calculation applies to:
  - Individuals practicing independent activities in retail commerce (except excise goods)
  - Individuals in agricultural product procurement (fitotehnie/horticultură/regnului vegetal)
- Records by: payer, payer category, contribution rate, budget classification, fiscal period, insured person
**Diagram present:** Figura D1.2
**Edge cases / exceptions:** Exempt persons skipped
**Status transitions:** N/A (calculation outputs to registers)

---

### BP 1.2-E — Refund amounts from State Social Insurance Budget to payers

**Trigger:** On request (refund application from payer).
**Actors:**
- Sistem (SI PS)
- Solicitant (Payer)
- Utilizator CNAS
- Șeful direcției (Department Head)
- Șeful CNAS (CNAS Head)
**Inputs (data / documents):**
- Cererea de restituire a sumelor (Refund application) — electronic
- Documentele obligatorii atașate (Mandatory attached documents) — electronic
**Outputs (data / documents / decisions):**
- Decizia de restituire (Refund decision) — electronic
- Lista documentelor de plată (List of payment documents) — electronic
**External systems touched:** Treasury (downstream via 1.2-G)
**Process steps (numbered):**
1. **1.2-E01 — Submit request.** Applicant authenticates, selects service, fills application. Attaches mandatory documents (classifier per request type). Application is signed and submitted. If applicant is physically present, CNAS user registers on their behalf and attaches scanned signed paper application (also applies to scanned applications received by email).
2. **1.2-E02 — Review request.** CNAS User reviews application and attached documents. System validates per service rules. System generates decision and payment documents list. Payment documents list contains entries at payment order level (document type, document number, payer bank details, beneficiary bank details, amount, payment destination) which the CNAS User completes with reference to system payment order. Decision is generated in PDF, signed electronically. Document routed to Department Head. On return, CNAS User may modify/complete.
3. **1.2-E03 — Review decision.** Department Head reviews decision, payment documents list, and application. If objections, return to step 1.2-E02. Otherwise sign decision electronically and end processing. Decision stored in Decisions Registry.
**Decision points / rules:**
- Electronic dossier per request
- For each request type: cerere/decizie templates attached, mandatory documents list configured
**Diagram present:** Figura E1.2; `images/pdf_p113_full.png` referenced
**Edge cases / exceptions:** Rejection by Department Head returns to reviewer
**Status transitions:** Request: submitted → reviewed → approved/returned → completed.

---

### BP 1.2-F — Correct payments administered by CNAS paid incorrectly or in excess

**Trigger:** On request.
**Actors:**
- Sistem (SI PS)
- Solicitant (Payer)
- Utilizator CNAS
- Șeful direcției
- Șeful CNAS
**Inputs (data / documents):**
- Cererea privind corectarea plăților la BASS (Request to correct BASS payments) — electronic
- Documentele obligatorii atașate (Mandatory documents)
**Outputs (data / documents / decisions):**
- Formularul de corectare (Correction form)
- Lista documentelor de plată (List of payment documents)
**External systems touched:** Treasury (via 1.2-G)
**Process steps (numbered):**
1. **1.2-F01 — Submit request.** Same as 1.2-E01.
2. **1.2-F02 — Review request.** CNAS User reviews. System validates. System generates correction form and payment documents list. Correction form contains correction entries within the same account (budget classification) which CNAS User completes with reference to system payment order. Payment documents list mirrors 1.2-E. Routed to Department Head. On return, user may modify.
3. **1.2-F03 — Review results documents.** Department Head reviews correction form, payment documents list and application. If objections, return to 1.2-F02. On approval, system generates transactions in payers' personal accounts based on the correction form per reflection rules set at form level.
**Decision points / rules:**
- Allowed corrections:
  - From one fiscal code to another (total or partial)
  - From fiscal code "999" to correct fiscal code (total)
  - From one ECO BASS code to another (total or partial)
  - From ECO BASS code to other budgets (state, local, mandatory health insurance funds) (total or partial)
  - Transfer of receipts from ECO BASS code to CNAS-managed accounts (pensions, benefits, etc.) (total or partial)
- Electronic dossier per payer
**Diagram present:** Figura F1.2; `images/pdf_p115_full.png` referenced
**Edge cases / exceptions:** Departmental review return
**Status transitions:** Request: submitted → reviewed → approved/returned → processed.

---

### BP 1.2-G — Generate information for the Treasury

**Trigger:** Automated, scheduled (default daily) OR manual launch.
**Actors:**
- Sistem (SI PS)
- Utilizator CNAS
- Șeful direcției
**Inputs (data / documents):**
- Approved payment documents lists from 1.2-E and 1.2-F
**Outputs (data / documents / decisions):**
- Informația spre executare pentru Trezorerie (Information for Treasury execution) — electronic
- Procedure results notification
**External systems touched:** Treasury / Ministry of Finance
**Process steps (numbered):**
1. **1.2-G01 — Launch procedure.** System determines newly approved payment documents lists for generating Treasury execution info.
2. **1.2-G02 — Generate Treasury info.** System generates info in the established format and sends to the user responsible for signing and transmission to MoF Treasury.
**Decision points / rules:**
- Only approved lists from 1.2-E and 1.2-F included
- All events logged
**Diagram present:** Figura G1.2; `images/pdf_p118_full.png` referenced
**Edge cases / exceptions:** None explicit
**Status transitions:** Payment list: approved → included in Treasury info → transmitted.

---

### BP 1.2-H — Staggered repayment of late penalties (penalități) to BASS

**Trigger:** On request (payer applies for staggering).
**Actors:**
- Solicitant (Payer)
- Utilizator CNAS
- Șeful CNAS
**Inputs (data / documents):**
- Cererea (Application) — electronic
**Outputs (data / documents / decisions):**
- Acordul semnat (Signed agreement) OR Scrisoarea de refuz (Refusal letter) — electronic
- Notification on signed agreement
**External systems touched:** MNotify / MCabinet (for notifications)
**Process steps (numbered):**
1. **1.2-H01 — Submit application.** Applicant authenticates, selects service, fills application. Application signed and sent to CNAS. If physical presence, CNAS User registers on applicant's behalf and attaches scanned application.
2. **1.2-H02 — Review request.** CNAS User reviews. System validates. If no nonconformities, system generates Agreement pre-filled with available data (applicant data, periods, etc.) and late penalty amount. CNAS User may complete Agreement with missing data. System warns user if this type of service was previously offered. System generates either Acord or Scrisoare de refuz (with refusal motive). PDF generated. CNAS User signs electronically and sends to CNAS Head. On return, user may modify or generate new version (Agreement or refusal letter).
3. **1.2-H03 — Review document.** CNAS Head reviews result document. If no objections, signs electronically and ends. Otherwise return to 1.2-H02. Result: Agreement signed, stored in Contracts Registry, request processed.
**Decision points / rules:**
- Electronic dossier per payer
- New payer → generate CNAS code; CTAS code based on legal address
- Existing payer → preserve modification history
- Templates per request type for: cerere, decizie, acord, contract, informație
**Diagram present:** Figura H1.2; `images/pdf_p122_full.png` referenced
**Edge cases / exceptions:** Refusal letter when nonconformities; CNAS Head can return
**Status transitions:** Request: submitted → reviewed → signed by user → approved/returned by head → finalized.

---

### BP 1.2-I — Process receipts to BASS

**Trigger:** Automated, scheduled (default daily) OR manual launch.
**Actors:**
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- Pachet trezorerial (Treasury package: Registry of BASS budget income + file with payment-document-level info) — electronic
**Outputs (data / documents / decisions):**
- Transactions recorded in payers' current accounts
- Notification on bank statement processing results
**External systems touched:** Treasury (direct connection OR via MConnect web service), MPay (origin of payments via Government Payment Service)
**Process steps (numbered):**
1. **1.2-I01 — Pull Treasury package.** System connects to Treasury or invokes MConnect web service. If no data, end. Otherwise store package in system.
2. **1.2-I02 — Validate package.** System validates per rules. On validation failure, notify CNAS user and end. On success continue.
3. **1.2-I03 — Process payment documents.** Rules:
   - Document types: 1=Ordin de plată (Payment order); 2=Ordin incasso; 10TT (10)=Notă de transfer (internal MDL bank-account operations)
   - Process each based on document type, operation type, debit/credit
   - On data entry, identify CNAS code and register alongside each document
   - If fiscal code not found in BASS or fiscal registry, record under CNAS code "1999"; when transmitting to SFS, use unidentified fiscal code "999"
   - After processing, generate notification to System Administrator and CNAS User
   - All payment documents stored in system, viewable by users
**Decision points / rules:**
- Daily Treasury generates BASS Income Registry + payment-document-level file
- Records by: payer, budget classification
- Insolvent payer may pay validated claims with destination text "stingerea creanțelor validate în procesul de insolvabilitate" or via assignment-of-receivables contract. If detected, notify responsible users for verification and confirmation
- Validation failure → notify, end
**Diagram present:** Figura I1.2
**Edge cases / exceptions:**
- Unknown fiscal code → CNAS code "1999"
- Insolvent payer payments → manual verification triggered
**Status transitions:** Package: pulled → validated → processed.

---

### BP 1.2-J — Calculate late penalty (majorare de întârziere)

**Trigger:** Automated, scheduled (default monthly) OR manual launch (per all payers / per single payer / per list of payers).
**Actors:**
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- None explicit (uses registries)
**Outputs (data / documents / decisions):**
- Transactions recorded in payers' current accounts
- Penalty calculation breakdown registrations
- Notification on processing results
**External systems touched:** None
**Process steps (numbered):**
1. **1.2-J01 — Identify payers with arrears.** System determines payers with delays in obligation payment by income classification, for which penalties are due.
2. **1.2-J02 — Calculate penalty.** For each payer:
   - Set penalty calculation rules based on payer category
   - Determine unpaid contribution amount
   - Determine number of late days (from arrears formation date to payment date inclusive)
   - Calculate penalty amount
   - Record penalty per all classifications in a separate document (with system-assigned ID) per payer (may have multiple entries per same classification if arrears increased/decreased during the month)
   - Sum total recorded as transaction in payer's current account under income classification 121410, with reference to source document containing penalty breakdown (income classification, calculation base, late period, penalty amount)
**Decision points / rules:**
- Penalties are calculated monthly without a separate decision
- Rate set annually by BASS Budget Law (system parameter per calendar year)
- Penalty = (unpaid social insurance contribution amount) × (number of late days) × (daily penalty rate %)
- For agricultural payers (point 1.5 of Annex 1 of Law 489/1999): penalty for contributions calculated for the management year applies from November 1 of the management year; if arrears for previous years, penalty calculated monthly
- If penalty calculation date falls on weekend, no transfer of calculation date — system respects this
- Penalty NOT calculated when:
  - Payer submitted documents for transfer of amounts between BASS budget accounts (between payment date and effective transfer)
  - Payer filed application with SFS for compensation of BASS debts from VAT or excise refunds (between filing and transfer)
  - Obligation amounts written off and on special records by SFS decision
  - Documents filed for transfer from other budgets (state, local, mandatory health) to BASS (between filing and transfer)
  - From insolvency procedure initiation date, on validated/arrears claims
- For contributions on special records → no penalty
- Transaction identifier = system-generated
**Diagram present:** Figura J1.2
**Edge cases / exceptions:** Multiple penalty entries per classification within one month if arrears change
**Status transitions:** N/A (calculation outputs)

---

### BP 1.2-K — Close management period and generate generalizing report

**Trigger:** Automated, scheduled (default monthly) OR manual launch.
**Actors:**
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- None explicit (uses registries)
**Outputs (data / documents / decisions):**
- Darea de seamă generalizatoare (Generalizing report) — electronic
- Procedure results notification
**External systems touched:** SFS (report sent post-closing)
**Process steps (numbered):**
1. **1.2-K01 — Select payers.** System launches report generation. Selects payers for whom report will be calculated.
2. **1.2-K02 — Generate generalizing report.** System generates a statistical report per management period, monthly. Includes indicators: calculated obligations, reduced, paid, cancelled, balances for the period and from year start.
   - Per-payer record contains all 4 tables data
   - Export to model: per-payer detail, per-CTAS totals, per-republic totals
   - **Table 1** "Contribuții de asigurări sociale calculate" — sums of social insurance contributions for month/year by category per Law 489/1999 Annex 1 and annual BASS Law
   - **Table 2** "Obligații calculate la BASS" — opening balances, contributions for the period, recalculated contributions for previous years, calculations on acts, subsidiary liability amounts, calculated penalties; divided by economic classification
   - **Table 3** "Obligații achitate la BASS" — opening balances, cancelled obligations, transferred sums, payment corrections (type-to-type, code-to-code), refunds to payers, recalculated/compensated amounts, sums lost due to exemption of agricultural landowners along Rîbnița-Tiraspol route, cancelled sums; divided by classifications
   - **Table 4** "Soldul datoriilor la sfârșitul perioadei de gestiune" — closing balances, debts taken/excluded from special records, balances without special records, payer arrears/BASS arrears at period end, BASS balance at period end; divided by classifications
**Decision points / rules:**
- Per payer + budget classification, 2 balance types: "Sold curent" (end of month) and "Sold gestiune" (end of management period)
- Indicator verification → close period for report
- After closure, calculated obligations info sent to SFS by administrator (per agreed format)
**Diagram present:** Figura K1.2
**Edge cases / exceptions:** None explicit
**Status transitions:** Management period: open → closed; Report: generated → verified → period closed.

---

### BP 1.2-L — Register scanned copies of declarations

**Trigger:** As needed.
**Actors:**
- Utilizator CNAS
- Șeful direcției
- Sistem (SI PS)
**Inputs (data / documents):**
- Formular de înregistrare (Registration form with attached scanned declaration)
**Outputs (data / documents / decisions):**
- Field "Documentul scanat" (Scanned document, PDF) populated in Declarations Registry
**External systems touched:** None
**Process steps (numbered):**
1. **1.2-L01 — Fill form.** CNAS User registers a form with declaration identification data + scanned declaration copy. Sent to Department Head for review.
2. **1.2-L02 — Review form.** Department Head reviews. If no objections, approve. Otherwise return to user (step 1.2-L01) OR the head may correct and approve without returning. Result: form processed and stored; "Documentul scanat" populated in Declarations Registry.
**Decision points / rules:**
- Stored in Declarations Registry
- Form-level validation rules: correctness, completeness, no duplicates per payer
**Diagram present:** Figura L1.2
**Edge cases / exceptions:** Department Head may correct in-place
**Status transitions:** Form: completed → submitted to head → approved/returned → stored.

---

### BP 1.2-M — Explore Declarations Registry

**Trigger:** As needed.
**Actors:**
- Utilizator CNAS
- Sistem (SI PS)
**Inputs (data / documents):**
- Search form
- Filter form
**Outputs (data / documents / decisions):**
- File exported in XLS, CSV, or PDF
**External systems touched:** None
**Process steps (numbered):**
1. **1.2-M01 — Explore registry.** System provides viewer interface for Declarations Registry. Search by registry fields and declaration content; filter by field values. On double-click on declaration, tabbed interface shows: payer info, declaration content, and other design-time info. Any tab exportable to XLS/CSV/PDF. Search/filter results exportable. Payer-type-specific processes (modification, report generation, etc.) can be launched.
**Decision points / rules:**
- View-only with search/filter
- Search criteria: CNAS code, Fiscal code, declaration form, period (from/to), IDNO/IDNP
- Selected declaration shown per approved model
- Declaration forms: IPC18, IPC21, IU17, TAXI18, CAS18-AN...
- Declaration types: Primary, Correction, After fiscal control, Correction of insured persons data; for TAXI18 also Supplementary and Supplementary-correction
**Diagram present:** (no specific image)
**Edge cases / exceptions:** None
**Status transitions:** N/A

---

### Registry: Registrul declarațiilor (Declarations Registry — section 8.1.3)

| Field | Type | Required | Description | Source/External link |
|---|---|---|---|---|
| Cod CTAS (CTAS Code) | code (classifier) | yes | Territorial CTAS code | Classifier |
| Cod CNAS (CNAS Code) | string | yes | Payer's CNAS code | Internal |
| IDNO/IDNP | string | yes | Legal entity / natural person identifier | RSUD/RSP |
| Denumirea prescurtată a plătitorului (Payer short name) | string | yes | Payer short name | Internal/RSUD |
| Forma declarației (Declaration form) | code | yes | E.g., IPC18, IPC21, IU17, TAXI18, CAS18-AN, etc. | Classifier |
| Tipul declarației (Declaration type) | code | yes | Primary, Correction, After fiscal control, Correction-of-insured-persons; TAXI18 also has Supplementary and Supplementary-correction | Classifier |
| Perioada declarării – De la (Period from, month/year) | year-month | yes | Period start | From declaration |
| Perioada declarării – Până la (Period to, month/year) | year-month | yes | Period end | From declaration |
| Data prezentării (Submission date) | date | yes | Submission date | SFS/CNAS |
| Data înscrierii în sistem (Date entered in system) | date | yes | System entry date | Internal |
| Numărul dării de seamă/declarației (Declaration number) | string | yes | SFS number, or CNAS document number | SFS/Internal |
| Numărul persoanelor asigurate (Number of insured persons) | int | yes | Count of insured persons in declaration | From declaration |
| Suma contribuțiilor calculate (Sum of calculated contributions) | decimal | yes | Total calculated contributions | From declaration |
| Statutul declarației (Declaration status) | enum | yes | Processing status | Internal |
| Conținutul dării de seamă/declarației în format XML (Declaration content in XML) | XML | yes | Full content in XML | SFS/Internal |
| Documentul scanat (Scanned document) | PDF (binary) | conditional | For types 4-BASS, BASS, BASSAN, REV-5 | Filled by 1.2-L process |
| Numărul pachetului din REVSPAS (REVSPAS package number) | string | conditional | For REV-5 case | REVSPAS |
| Pachetul din REVSPAS (REVSPAS package) | binary/blob | conditional | REVSPAS package archive | REVSPAS |

**Mențiuni / Notes:**
- Search criteria: CNAS code, Fiscal code, Declaration form, Period (from..to), IDNO/IDNP
- On selection, declaration is shown per approved model
- Structure may be modified/extended during business analysis

---

### Section 1.3 — Gestionarea Registrului plătitorilor insolvabili (Insolvent Payers Registry Management)

**Regulatory framework:**
- Law no. 489-XIV/1999; Annual BASS Budget Law; Law on Insolvency no. 149/2012; CNAS orders regulating the insolvent payers registry

---

### BP 1.3-A — Register/modify/delete insolvent payer

**Trigger:** As needed.
**Actors:**
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- Formular de înregistrare/modificare/radiere (Registration/modification/removal form)
- Documentul atașat (Scanned document)
**Outputs (data / documents / decisions):**
- Insolvent Payers Registry updated; Contributors Registry updated if applicable
**External systems touched:** None
**Process steps (numbered):**
1. **1.3-A01 — Form entry.** CNAS User fills form, attaches scanned document. Validation rules applied.
2. **1.3-A02 — Process form.** System updates Insolvent Payers Registry and (when applicable) Contributors Registry per registration rules.
**Decision points / rules:**
- Electronic dossier per payer
- History preserved per existing payer
- Mandatory documents list configured (not enforced for proactive services)
**Diagram present:** Figura A1.3
**Edge cases / exceptions:** None explicit
**Status transitions:** Form: completed → processed.

---

### BP 1.3-B — Register/modify/delete claims (creanțe)

**Trigger:** As needed.
**Actors:**
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- Formular de înregistrare/modificare/radiere (Form)
- Documentul atașat (Scanned document)
**Outputs (data / documents / decisions):**
- Insolvent Payers Registry updated
**External systems touched:** None
**Process steps (numbered):**
1. **1.3-B01 — Form entry.** CNAS User completes form, attaches document, validation applied.
2. **1.3-B02 — Process form.** System updates Insolvent Payers Registry per registration rules. User can consult system data without leaving the form.
**Decision points / rules:** Same as 1.3-A
**Diagram present:** Figura B1.3
**Edge cases / exceptions:** None explicit
**Status transitions:** Form: completed → processed.

---

### BP 1.3-C — Register payments against claims

**Trigger:** As needed.
**Actors:**
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- Formular de înregistrare (Form)
- Documentul atașat (Scanned document)
**Outputs (data / documents / decisions):**
- Insolvent Payers Registry updated
**External systems touched:** None
**Process steps (numbered):**
1. **1.3-C01 — Form entry.** User completes form, attaches document, validation applied.
2. **1.3-C02 — Process form.** System updates Insolvent Payers Registry. User can consult system data inline.
**Decision points / rules:** Same as 1.3-A
**Diagram present:** Figura C1.3
**Edge cases / exceptions:** None explicit
**Status transitions:** Form: completed → processed.

---

### BP 1.3-D — Explore Insolvent Payers Registry

**Trigger:** As needed.
**Actors:**
- Utilizator CNAS
- Sistem (SI PS)
**Inputs (data / documents):**
- Search form
- Filter form
**Outputs (data / documents / decisions):**
- File exported in XLS, CSV, or PDF
**External systems touched:** None
**Process steps (numbered):**
1. **1.3-D01 — Explore registry.** System provides viewer with search/filter. On payer double-click, tabbed interface: payer info, claims info, payments info, and other design-time tabs. Any tab exportable. Type-specific processes launchable.
**Decision points / rules:** View-only; events logged; export supported
**Diagram present:** (none specific)
**Edge cases / exceptions:** None
**Status transitions:** N/A

---

### Registry: Registrul plătitorilor insolvabili (Insolvent Payers Registry — section 8.1.4.5)

| Field | Type | Required | Description | Source/External link |
|---|---|---|---|---|
| Date generale (General data) | — | yes | Inherited from Contributors Registry | Contributors Registry |
| Date privind adresa (Address data) | — | yes | Inherited from Contributors Registry | Contributors Registry |
| Date privind genurile de activitate (Activity types data) | — | yes | Inherited from Contributors Registry | Contributors Registry |
| Date privind conducătorii (Directors data) | — | yes | Inherited from Contributors Registry | Contributors Registry |
| Date privind fondatorii (Founders data) | — | yes | Inherited from Contributors Registry | Contributors Registry |
| Date privind ultima reorganizare (Last reorganization data) | — | conditional | Inherited from Contributors Registry | Contributors Registry |
| **Date privind procedura de insolvabilitate (Insolvency procedure data)** | | | | |
| Data înregistrării (Registration date) | date | yes | Date insolvency record entered | Internal |
| Data intentării (Initiation date) | date | yes | Insolvency initiation date | Court |
| Data încetării (Termination date) | date | conditional | Insolvency termination date | Court |
| IDNO al succesorului (Successor IDNO) | string | conditional | Successor entity IDNO | RSUD |
| Alte date (Other data) | text/blob | no | Other relevant data | — |
| **Datele despre creanțe (Claims data)** | | | | |
| Organul emitent (Issuing body) | string | yes | Issuing court/authority | Court |
| Nr. actului judecătoresc (Court act number) | string | yes | Court act number | Court |
| Data actului judecătoresc / data intentării procedurii (Court act date / insolvency initiation date) | date | yes | Date of court act or insolvency procedure initiation | Court |
| Notificarea (Notification) | text | conditional | Notification reference | Court/Internal |
| Suma creanței, contribuții (Claim amount, contributions) | decimal | yes | Claim amount for contributions | Court/Internal |
| Suma creanței, penalități (Claim amount, penalties) | decimal | yes | Claim amount for penalties | Court/Internal |
| Suma creanței, amenzi (Claim amount, fines) | decimal | yes | Claim amount for fines | Court/Internal |
| Suma creanței, altele (Claim amount, others) | decimal | no | Other claim amounts | Court/Internal |
| **Datele despre achitarea creanțelor (Claim payments data)** | | | | |
| Suma plății (Payment amount) | decimal | yes | Payment amount | Internal/Treasury |
| Referință la tranzacția din Contul personal al Plătitorului (Reference to payer's personal account transaction) | FK (transaction id) | yes | Link to payer's account transaction | Internal |

---

## Annex 2 — Persoane asigurate (Insured Persons)

### Section 2.1 — Gestionarea Registrului persoanelor asigurate (Insured Persons Registry Management)

**Regulatory framework:**
- Law no. 489-XIV/1999
- Gov. Decision no. 399/2021
- CNAS Order no. 188-A/2022 — Regulation on assigning the social insurance personal code and updating personal data
- CNAS Order no. 235-A/2022 — Regulation on deactivating duplicate CPAS codes

---

### BP 2.1-A — Update Insured Persons Registry based on info from RSP

**Trigger:** Automated, scheduled (default daily) OR manual launch.
**Actors:**
- MConnect (gateway to RSP)
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- Lista persoanelor fizice noi sau modificate (List of new/modified individuals)
- Datele din RSP privind persoana fizică solicitată (RSP data on requested individual)
**Outputs (data / documents / decisions):**
- Notificare privind rezultatele sincronizării cu RSP (RSP sync results notification)
- Auto-generated decisions terminating active benefits in case of death or authorized emigration
- Payment accounts updated to status "Anulat"
**External systems touched:** RSP (via MConnect)
**Process steps (numbered):**
1. **2.1-A01 — Launch data pull from RSP.** Procedure runs per schedule. Extract list of new individuals and modified ones, incl. deaths. If empty, end.
2. **2.1-A02 — Process individuals list.** For each record:
   - **Update existing record.** If individual found, update personal data, prior record moved to history (per-person history with update date)
   - **If RSP contains death date:**
     - System checks whether prior death date already present in registry
     - If RSP death date doesn't match registry's prior death date, register deviation separately and update all fields EXCEPT death date; deviations list accessible via separate functionality (report)
     - If no prior death date in registry: check active benefits for this person; auto-generate termination decision (no specialist approval needed) updating Decisions Registry (modify granting period, fill "Data terminării", "Motivul terminării", "Numărul deciziei terminării" per identified decision). New decisions registered in Decisions Registry. Update all payment accounts with status "Spre achitare" in Benefit Payment Accounts Registry to "Anulat" with "Data anulării" and "Motivul anulării"
   - **If RSP contains authorized emigration mention:** same termination flow — check active benefits, auto-generate termination decisions, update Decisions Registry, cancel pending payment accounts
   - Update processing status in RSP
**Decision points / rules:**
- Only existing individuals in the registry are updated (new individuals are not added via this process)
- History preserved per insured person
- Process events logged
**Diagram present:** Figura A2.1
**Edge cases / exceptions:**
- Death date mismatch → deviation list, partial update
- Active benefits on deceased/emigrated → auto-terminate
**Status transitions:** Person record: active → updated; Benefit decision: granted → terminated; Payment account: Spre achitare → Anulat

---

### BP 2.1-B — Register/modify/deactivate insured person via paper request to CNAS

**Trigger:** On request.
**Actors:**
- Solicitant (Applicant)
- Utilizator CNAS
**Inputs (data / documents):**
- Formular (Form)
**Outputs (data / documents / decisions):**
- Notification on form execution
- Registry updated
**External systems touched:** MCabinet (via MNotify), RSP (lookup)
**Process steps (numbered):**
1. **2.1-B01 — Fill form.** CNAS User registers form on applicant's behalf. System pre-fills fields with available data or RSP data (if IDNP supplied and missing locally). For non-residents, primary documents must be attached (scanned).
2. **2.1-B02 — Process form.** System validates per service type. Rules:
   - Check person by IDNP, initials, birth date
   - If not found: new record + new CPAS code; fill from form
   - If found: update data
   - **On deactivation:** transfer all calculated/paid contributions and benefits from deactivated ID to active ID (duplicate CPAS case); fill "Cod ID vechi" and "data trecerii" in registry
   - System updates registry, generates notification in personal cabinet (duplicated to MCabinet via MNotify)
**Decision points / rules:**
- Electronic dossier per insured person
- New insured → generate CPAS code
- Existing → update; preserve history
- Templates per request type (cerere, certificat, refuz, recipisă)
- Mandatory documents list configured (not enforced for proactive)
- Duplicate CPAS deactivated → contribution-period info transferred to retained ID
**Diagram present:** Figura B2.1
**Edge cases / exceptions:**
- Non-residents → must attach primary docs
- Duplicate CPAS → deactivate and merge
**Status transitions:** Person record: pending → registered/updated/deactivated.

---

### BP 2.1-C — Update insured persons data based on info from hospitals (eCMND)

**Trigger:** Automated, scheduled (default daily) OR manual launch.
**Actors:**
- MConnect (gateway to eCMND)
- Sistem (SI PS)
- Utilizator CNAS
- Administrator de sistem
**Inputs (data / documents):**
- Lista persoanelor fizice decedate (List of deceased individuals)
- Datele de deces (Death data on requested individual)
**Outputs (data / documents / decisions):**
- Sync results notification
- Auto-generated termination decisions for active benefits
**External systems touched:** eCMND (Constatarea medicală a nașterii și a decesului — Medical confirmation of births and deaths) via MConnect
**Process steps (numbered):**
1. **2.1-C01 — Launch data pull from eCMND.** Procedure runs per schedule. Extract list of deceased. If empty, end.
2. **2.1-C02 — Process individuals list.** For each record:
   - If found in registry, create new record (prior moved to history)
   - Check active benefits; auto-generate termination decisions (no specialist approval needed) updating Decisions Registry (period, "Data terminării", "Motivul terminării", "Numărul deciziei terminării"); new decisions registered
   - Update processing status in eCMND resource
**Decision points / rules:**
- Only existing individuals in registry are updated
- History preserved per insured
- Process events logged
**Diagram present:** Figura C2.1
**Edge cases / exceptions:**
- Validation failure → log, skip
**Status transitions:** Person: active → deceased; Benefit: active → terminated.

---

### BP 2.1-D — Explore Insured Persons Registry

**Trigger:** As needed.
**Actors:**
- Utilizator CNAS
**Inputs (data / documents):**
- Search form
- Filter form
**Outputs (data / documents / decisions):**
- File exported in XLS, CSV, or PDF
**External systems touched:** None
**Process steps (numbered):**
1. **2.1-D01 — Explore registry.** Viewer with search and filter by registry fields. Double-click on insured person → tabbed interface: modification history, social benefits info, and design-time tabs. Any tab exportable. Person-specific processes launchable (modification, benefit request, report generation).
**Decision points / rules:** View-only; events logged; export supported
**Diagram present:** (none specific)
**Edge cases / exceptions:** None
**Status transitions:** N/A

---

### Section 2.2 — Calcularea și achitarea contribuțiilor pe persoanele asigurate (Calculation & Payment of Contributions for Insured Persons)

**Regulatory framework:**
- Law no. 489-XIV/1999; CNAS Order 275-A/2015; CNAS Order 233-A/2022; CNAS Order 247-A/2022; SFS Order 1108/2015; CNAS Order 96-A/2023; Regulation on insurance by contract — CNAS Order 209-A/2024; Social Insurance Contract approved by CNAS Order 209-A/2024

---

### BP 2.2-A — Register data from declarations submitted by Payer for insured persons (REV-5)

**Trigger:** On request.
**Actors:**
- Solicitant (Payer)
- MConnect (gateway to RSUD/RSP)
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- Declarations on paper (REV-5)
- REVSPAS-format electronic packages (structure provided by CNAS at design)
**Outputs (data / documents / decisions):**
- Transactions in insured persons' accounts and Declarations Registry
- Notification on declaration status
**External systems touched:** MConnect (RSP/RSUD lookups)
**Process steps (numbered):**
1. **2.2-A01 — Register declarations.** Applicant brings paper declarations plus electronic REVSPAS packages. CNAS User selects service, uploads REVSPAS package(s). Alternatively, user can register declarations manually without uploading package. System validates per declaration type (each type has its own rules). On errors, communicate to user. If declaration has insured-person identification errors, place in waiting status until person is identified; user has edit access.
2. **2.2-A02 — Process declarations.** If no errors, system records in Declarations Registry + Insured Person's account:
   - If person not found in Insured Persons Registry, register via flow 2.1-B
   - From declaration identify: period (month/year), person category, function code, etc., and calculated contributions amount
   - If correction-type declaration, replaces the primary one (history preserved)
**Decision points / rules:**
- Only declarations for periods before 01.01.2018 are registered
- Unique identifier generated per declaration
- Declarations stored in Declarations Registry
**Diagram present:** Figura A2.2
**Edge cases / exceptions:**
- Insured-person ID error → waiting status
- Correction-type → replaces primary (history kept)
**Status transitions:** Declaration: pending identification → registered → processed.

---

### BP 2.2-B — Distribute Treasury payments into insured persons' personal accounts

**Trigger:** Automated, scheduled (default daily) OR manual launch.
**Actors:**
- Sistem (SI PS)
- Utilizator CNAS
**Inputs (data / documents):**
- Processed Treasury bank statement (from 1.2-I)
**Outputs (data / documents / decisions):**
- Transactions in insured persons' accounts
- Notification on distribution results
**External systems touched:** None (input is internal from 1.2-I)
**Process steps (numbered):**
1. **2.2-B01 — Launch distribution procedure.** System runs per schedule or manually by Administrator. Identify last processed statement; extract list of payments per payer (List 1). Also identify list of payers with affected period for which updates to insured persons' accounts occurred since last run (List 2). If both empty, end.
2. **2.2-B02 — Process List 1.** Rules:
   - Identify which declaration(s) the paid amount closes. If amount partially covers obligation for declaration (oldest unpaid), system computes settlement percentage and computes amount paid per insured persons for that declaration applying the percentage. Record remaining unpaid amount on that declaration for next payment processing. Example: paid 800 MDL, declaration obligation 1000 MDL → 80% settlement → insured persons' accounts reflect 80% paid; remaining 200 MDL queued for next payment. Next payment first closes the queued 200 then moves to next declaration.
   - From identified declaration extract period (month/year) and calculated contributions amount
3. **2.2-B03 — Process List 2.** Rules:
   - Starting from affected period, redistribute prior payments anew, cancelling old ones (history preserved) using same logic as 2.2-B02
   - Send procedure-results notification to System Administrator and responsible Direction
**Decision points / rules:**
- Ignore calculation and payment transactions registered via 2.2-D (those have their own payment generation)
- Process events logged
**Diagram present:** Figura B2.2
**Edge cases / exceptions:**
- Partial coverage of obligation → percent-based distribution
- Recalculation reflows from affected period
**Status transitions:** Payment: pending → distributed; Prior distribution: cancelled, new applied.

---

### BP 2.2-C — Social insurance contract

**Trigger:** On request.
**Actors:**
- Sistem (SI PS)
- MPay (gov payment service)
- Solicitant (Applicant)
- Utilizator CNAS
- Șeful CNAS
**Inputs (data / documents):**
- Formular de solicitare (Application form) — electronic or paper
**Outputs (data / documents / decisions):**
- Contract de asigurare socială (Social insurance contract) signed by both parties — electronic or paper
- Scrisoare de refuz (Refusal letter)
- Notification on contract issuance
**External systems touched:** MPay (for payment of premium); MCabinet (for delivery); MConnect for RSP if needed
**Process steps (numbered):**
1. **2.2-C01 — Fill form.** Applicant authenticates, selects service, completes form. Submitted to CNAS. If physical presence, CNAS User fills form on applicant's behalf at CTAS level. System validates per service rules. On nonconformities, inform user.
2. **2.2-C02 — Generate contract.** System generates Contract pre-filled from system data (form, person data, etc.) OR a refusal letter. User can complete missing data. User exports Contract as PDF, sends to applicant alongside nota de plată (payment note for MPay or other methods). Applicant signs contract from personal cabinet and pays via MPay. If physical presence, contract printed in 3 copies + payment note for signing.
3. **2.2-C03 — Verify payment.** System notifies user when payment note is paid. On payment:
   - Electronic contract → routed to CNAS Head for signing
   - Paper contract → routed to CNAS Head for signing (outside system)
4. **2.2-C04 — Sign contract.** CNAS Head signs:
   - Electronic → e-signature; signed contract accessible by applicant from personal cabinet
   - Paper → wet signature; returns 2 copies to CTAS; CTAS hands one copy to applicant
5. **2.2-C05 — Process contract.** System generates transactions in payer's and insured person's personal accounts per established method. Notify applicant about reflection of contract in personal accounts.
**Decision points / rules:**
- Electronic dossier per payer
- Templates per request type (cerere, decizie, contract, recipisă)
- Payment required before signing by CNAS Head
**Diagram present:** Figura C2.2; `images/pdf_p159_full.png` referenced
**Edge cases / exceptions:**
- Not paid → contract not signed by head
- Refusal letter alternative
**Status transitions:** Application: submitted → contract generated → payment pending → paid → signed by user → signed by head → processed.

---

### BP 2.2-D — Register contributions calculated and paid for insured persons from other documents

**Trigger:** On request (e.g., patentă holders, other docs).
**Actors:**
- Solicitant (Insured Person)
- Sistem (SI PS)
- Utilizator CNAS
- Șeful direcției
**Inputs (data / documents):**
- Document pe suport de hârtie (Scanned paper document)
**Outputs (data / documents / decisions):**
- Transactions in insured persons' accounts
- Notification on document status
**External systems touched:** MConnect/RSP (for new insured persons), MCabinet/MNotify (notifications)
**Process steps (numbered):**
1. **2.2-D01 — Register document.** CNAS User registers document with scanned copy attached. System validates entered data. On error, document doesn't advance. Otherwise sent to Department Head.
2. **2.2-D02 — Review document.** Department Head reviews. If no objections, send to processing. Otherwise return to user (2.2-D01).
3. **2.2-D03 — Process document.** Rules in Declarations Registry + insured person's account:
   - If insured person not found by IDNP, fetch from RSP and register new with new CPAS code
   - From document identify: period (month/year), person category, function code, remuneration fund, calculated contributions, labor relationship info
   - For calculated contributions, system generates payment-side transactions (so these are recorded as already paid)
   - Generate notifications to insured persons referenced
**Decision points / rules:**
- Separate registration form per document type with own validation and transaction formation rules
- Role with registration right per document type
- Unique identifier per document
- All events logged
**Diagram present:** Figura D2.2
**Edge cases / exceptions:**
- New insured → auto-create from RSP
**Status transitions:** Document: registered → under review → approved/returned → processed.

---

### Section 2.3 — Gestionarea perioadelor de activitate realizate până la 1 ianuarie 1999 (Pre-1999 Activity Periods)

**Regulatory framework:**
- Law no. 489-XIV/1999; Gov. Decision 399/2021; Gov. Decision 426/2018 on labor seniority records; CNAS Order 188-A/2022; CNAS Order 67-A/2018 on scanning labor booklet info pre-01.01.1999; CNAS Order 184-A/2019 on reading info from scanned labor booklets

---

### BP 2.3-A — Register scanned copies of labor booklets (carnete de muncă)

**Trigger:** As needed (on person's request to scan).
**Actors:**
- Utilizator CNAS
- Șeful direcției
- Sistem (SI PS)
**Inputs (data / documents):**
- Carnet de muncă scanat (Scanned labor booklet)
**Outputs (data / documents / decisions):**
- Notification on electronic archiving of scanned booklet
- Field "Copia carnetului de muncă" populated (PDF) in Insured Persons Registry
**External systems touched:** None
**Process steps (numbered):**
1. **2.3-A01 — Fill form.** System provides interface pre-filled with personal data; user attaches scanned copy/copies of labor booklet.
2. **2.3-A02 — Review form + scanned booklet.** Department Head verifies correctness and completeness of scan and form. If no objections, approve scan. Otherwise return to user (2.3-A01). On approval, "Copia carnetului de muncă" field in Insured Persons Registry populated with PDF.
**Decision points / rules:**
- Booklets scanned on individual request
- Scanned copy stored in system per person
- Department Head approves
- System assigns CPAS code to individuals lacking one
- System creates reports per various criteria
**Diagram present:** Figura A2.3
**Edge cases / exceptions:** Returned for correction
**Status transitions:** Form: completed → reviewed → approved/returned → stored.

---

### BP 2.3-B — Register pre-01.01.1999 activity periods

**Trigger:** As needed.
**Actors:**
- Utilizator CNAS
- Șeful direcției
**Inputs (data / documents):**
- Formular de înregistrare (Registration form)
**Outputs (data / documents / decisions):**
- Notification on entry of data into insured person's personal account
**External systems touched:** None
**Process steps (numbered):**
1. **2.3-B01 — Fill form.** Interface for registering activity-period data read corresponding to labor booklet entries. Interface shows scanned booklet content alongside for convenience.
2. **2.3-B02 — Review form.** Department Head reviews. If no objections, approve. Otherwise return to user (2.3-B01) OR head can correct and approve without returning.
**Decision points / rules:**
- Scanned booklets retrievable by IDNP, CPAS code, initials, or scan date
- Form-level validation: correctness, completeness, no duplicates per insured
- Events logged
- Reads/manual entries written to person's personal account
- System creates statistics and reports per various criteria
**Diagram present:** Figura B2.3
**Edge cases / exceptions:** Head may correct in-place
**Status transitions:** Form: completed → reviewed → approved/returned → stored in personal account.

---

### Registry: Registrul persoanelor asigurate (Insured Persons Registry — section 8.2.4)

| Field | Type | Required | Description | Source/External link |
|---|---|---|---|---|
| **Date generale (General data)** | | | | |
| Codul personal de asigurări sociale — cod CPAS (Personal Social Insurance Code) | string | yes | Insured person's CPAS code | Internal (generated) |
| Numele (Last name) | string | yes | Last name | RSP |
| Prenumele (First name) | string | yes | First name | RSP |
| Patronimicul (Patronymic) | string | no | Patronymic | RSP |
| Sexul (Sex) | enum | yes | Sex | RSP |
| Data nașterii (Birth date) | date | yes | Date of birth | RSP |
| **Locul nașterii (Place of birth)** | | | | |
| Țara (Country) | code (classifier) | yes | Country of birth | Classifier |
| Raionul (District) | code (classifier) | yes | District of birth | Classifier |
| Municipiul/orașul/satul (Municipality/town/village) | code (classifier) | yes | Locality of birth | Classifier |
| Cetățenia (Citizenship) | code (classifier) | yes | Citizenship | Classifier |
| **Datele despre locul de trai (Domicile data)** | | | | |
| Țara (Country) | code (classifier) | yes | Country of residence | Classifier |
| Raionul (District) | code (classifier) | yes | District of residence | Classifier |
| Municipiul/orașul/satul (Municipality/town/village) | code (classifier) | yes | Locality of residence | Classifier |
| Sectorul, strada, numărul casei, blocul, apartamentul (Sector, street, house number, block, apartment) | string | no | Detailed address | RSP |
| Datele despre deces (Death data) | composite | conditional | Death data block | RSP / eCMND |
| **Datele despre documentele de identitate (Identity documents)** | | | | |
| IDNP — numărul de identificare de stat al persoanei | string | yes | State Personal ID Number | RSP |
| Denumirea (codul) documentului (Document name/code) | code (classifier) | yes | Document type per classifier | Classifier |
| Seria (Series) | string | no | Document series | RSP |
| Numărul (Number) | string | yes | Document number | RSP |
| Data eliberării (Issue date) | date | yes | Issuance date | RSP |
| Termenul de valabilitate (Expiry date) | date | conditional | Validity expiration | RSP |
| Emitentul (Issuer) | string | yes | Issuing authority | RSP |
| Statutul persoanei asigurate (Insured person status) | enum | yes | Active / Inactive | Internal |
| **Datele privind contribuțiile de asigurări sociale (Social insurance contribution data)** | | | | |
| Angajatorul — IDNO (Employer IDNO) | string | conditional | Employer state ID (legal person) | RSUD |
| Angajatorul — Cod fiscal (Employer fiscal code) | string | conditional | Employer fiscal code | SFS / RSUD |
| Angajatorul — Denumirea (Employer name) | string | conditional | Employer name | RSUD |
| Angajatorul — Adresa juridică (Employer legal address) | string | conditional | Employer legal address | RSUD |
| Baza de calcul (Calculation base) | decimal | yes | Calculation base for mandatory state social insurance contributions | From declaration |
| Contribuțiile calculate (Calculated contributions) | decimal | yes | Calculated social insurance contributions | From declaration |
| Contribuțiile plătite la BASS (Paid contributions to BASS) | decimal | yes | Paid contributions to State Social Insurance Budget | Treasury / 2.2-B |
| Categoria persoanei asigurate (Insured person category) | code (classifier) | yes | Category of insured person | Classifier |
| Datele despre perioade necontributive asimilate stagiului de cotizare (Non-contributory periods assimilated to contribution stage) | composite | conditional | Per legislation | Internal |
| Informația privind activitatea de muncă realizată în perioada de până la 1 ianuarie 1999 (Work activity prior to 1999) | composite | conditional | From scanned labor booklets | 2.3-B / Labor booklets |
| Datele despre tipul și cuantumul pensiilor, indemnizațiilor și altor prestații sociale (Type and amount of pensions, indemnities and other social benefits) | composite | conditional | Benefit registry references | Benefits subsystem |
| Cod ID vechi (Old ID code) | string | conditional | Previous (deactivated duplicate) CPAS code | Internal (set on 2.1-B deactivation) |
| Data trecerii (Transfer date) | date | conditional | Date of transfer from old CPAS to retained CPAS | Internal |
| Copia carnetului de muncă (Labor booklet copy) | PDF (binary) | conditional | Scanned labor booklet (PDF) | 2.3-A |

