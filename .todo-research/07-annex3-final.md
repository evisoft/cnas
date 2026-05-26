# 07 — Annex 3 Final (Adjacent Services, Payments, Registries, Web Services, Reports, Templates, Part B)

> Source: `tor/TOR.md` lines 16518–20771 covering PDF pages 272–end of Annex sections (8.3.6.4 → 8.7.3.5) and the Part B DOCX (Documentația standard).
> Note: The user's question briefly mentioned "Annex 3.6 — atestare stagiu de cotizare, eliberare certificate, restituire sume, dosare arhivate". Those titles do NOT appear in the TOR text in this line range. The only "Servicii conexe" / Annex 3.6 services present are: indemnizația de șomaj (A), șomaj cu statut special (B), șomaj în baza acordurilor internaționale (C), indemnizația viageră sportivi (D), plata ajutorului social (E), plata compensației la energie (F), evidența documentelor executorii (G). Section 3.7 covers the calculation/payment/reconciliation/recovery flows. No "atestare stagiu" or "dosare arhivate" sub-process is documented here.

---

## Annex 3.6 — Procese de lucru "Servicii conexe" (Adjacent services)

### 3.6-A. Indemnizație de șomaj
Not in the requested range (begins line 16170, before 16518). Referenced by 3.6-B as the canonical pattern.

### 3.6-B. Indemnizație de șomaj/alocație de integrare sau reintegrare profesională a șomerilor cu statut special
- **Status:** "Procesul de acordare a prestației este similar procesului descris în secțiunea 8.3.6.1 (3.6-A. Indemnizație de șomaj)." No standalone steps documented.

### 3.6-C. Indemnizație de șomaj — acorduri internaționale
Header only in this range (line 16348). No body details visible from line 16518 onward — body is in earlier lines.

### 3.6-D. Calcularea indemnizației viagere sportivilor de performanță și antrenorilor sportivilor de performanță
- **Name / Code:** 3.6-D (section 8.3.6.4).
- **Description:** Process for awarding the lifetime allowance for performance athletes and their coaches.
- **Actors:**
  - **Sistem** — SI PS.
  - **Beneficiarul** — persoana care solicită acordarea prestației.
  - **Utilizator CNAS** — specialist CNAS/CTAS involved in the process.
  - **Șeful direcției** — direcție head at CNAS/CTAS.
  - **Șeful CNAS** — CNAS/CTAS head approving the decision.
- **Periodicitate:** La solicitare (on demand).
- **Reguli de business:**
  - For each Persoană asigurată an electronic dosier is created.
  - System journals every event (start datetime, success entries, error entries, completion datetime).
  - Each request type has attached templates: cerere, document rezultativ (fișa de calcul, decizie, contract, …) and recipisă.
  - Each request type carries a list of mandatory attached documents (NOT applied for proactive services).
  - Each request type carries a waiting period for missing documents.
  - Prestațiile sociale = fixed amount OR computed from several factors (contribuții calculate și achitate, vechime în muncă, etc.).
  - References Registers: Registrul persoanelor asigurate (8.2.4), Registrul deciziilor (8.3.9), Registrul de conturi de plată a prestațiilor (8.3.10).
- **Inputs:**
  - Intrare / electronic doc — **Cererea privind acordarea prestației** (based on letter received from Ministerul Educației și Cercetării).
  - Intrare / electronic doc — **Documentele obligatorii atașate**.
- **Outputs:**
  - Ieșire / electronic — **Recipisă privind primirea cererii**.
  - Ieșire / electronic — **Decizia privind acordarea prestației sau de refuz**.
  - Ieșire / electronic — **Notificare privind prestația acordată**.
- **Steps (Cod / Actor / Activity / Description):**
  - **3.6-D01 — Utilizator CNAS — Depunerea cererii.** Based on MEC letter, the CNAS user registers cereri in the names of the Beneficiari, attaches mandatory documents (classifier per prestation type drives the document list). Result: cerere înregistrată + recipisă generată.
  - **3.6-D02 — Utilizator CNAS — Examinarea cererii și deciziei.** User signs recipisă, examines cerere + attachments (only if cerere submitted online). System runs validation rules per prestation type. If no nonconformities, the system generates fișa de calcul (pre-populating Beneficiar data, computation periods, …) and the computed sum. CNAS user can complete missing data; sum auto-recalculates. The sum is split per articole de cheltuieli (CNAS classifier). System generates a Decizia privind acordare OR Decizia privind refuz (PDF). Signed electronically by CNAS user; transmitted to Șeful direcției. On return for re-exam, the user can modify fișa and regenerate the decision.
  - **3.6-D03 — Șeful direcției — Examinarea cererii și deciziei.** Reviews; if no objections, e-signs and forwards to Șeful CNAS. Otherwise returns to 3.6-D02. Result: signed or returned.
  - **3.6-D04 — Șeful CNAS — Examinarea cererii și deciziei.** Reviews; if no objections, e-signs and finalizes. Otherwise returns to 3.6-D02 with notifications to Șeful direcției and Utilizator CNAS. Result: decision approved + stored in Registrul deciziilor + cerere processed.
- **Diagram:** Figura D3.6 — sequential lanes Beneficiar → Utilizator CNAS → Șeful Direcției → Șeful CNAS → System; loops on "Se aprobă? Nu" back to D02; "Da" → Stocarea în Registrul deciziilor.
- **Output document:** Decizia PDF e-signed; Fișa de calcul; Recipisă; Notificare.
- **Notifications:** Beneficiar (twice — on intake and on approval); CNAS user and Șeful direcției on return for reexam.
- **Edge cases:**
  - Return loop from Șef CNAS or Șef direcției back to D02.
  - Online submission triggers extra examination step at D02.

### 3.6-E. Evidența plății ajutorului social
- **Status:** Identical to 3.6-F (8.3.6.6). Source of information is **SIAS** ("Asistența Socială").
- All process details follow 3.6-F pattern.

### 3.6-F. Evidența plății compensației la energie sub formă monetară
- **Name / Code:** 3.6-F (8.3.6.6).
- **Description:** Automated process tracking payment of monetary energy compensation.
- **Actors:**
  - **MConnect** — governmental interop platform exposing access to SIVE.
  - **Sistem** — SI PS triggers SIVE invocation to obtain beneficiary data.
  - **SIVE** — Sistemul informațional automatizat „Vulnerabilitatea Energetică” owned by MMPS; supplies energy-compensation beneficiaries.
  - **Beneficiar** — insured person receiving the prestație.
- **Periodicitate:** Automated launch by system per scheduled run config — once per day — OR administrator-triggered. Parameter configurable in admin component.
- **Reguli de business:**
  - Same generic five-point block as 3.6-D (dosier electronic, journaling, templates, mandatory docs list, waiting period, fixed/computed amount).
  - References: Reg. persoanelor asigurate (8.2.4), Reg. deciziilor (8.3.9), Reg. conturi de plată (8.3.10).
- **Inputs:**
  - Lista beneficiarilor (electronic doc).
  - Datele din RSP privind persoana solicitată (Beneficiar).
- **Outputs:**
  - Înregistrarea beneficiarului și a sumei compensate în Registrul de conturi de plată a prestațiilor.
  - Notificare privind prestația acordată.
- **Steps:**
  - **3.6-F01 — Sistem — Lansarea procedurii de obținere a listei beneficiarilor cu suma compensației.** Triggers the procedure to fetch the beneficiary list with compensation amount from SIVE. If empty → process ends. Otherwise continues. Result: lista beneficiarilor cu suma compensației.
  - **3.6-F02 — Sistem — Procesarea listei.** Each row is processed under validation + identification rules. Invalid rows are skipped with event logging (errors logged). Rules: System records in Registrul de conturi de plată a prestațiilor the data: Beneficiar, perioada, suma compensației. Result: list processed; data stored in registru; notifications sent to Beneficiari.
- **Diagram:** Figura H3.6 — System lane (Lansare → Procesare → Stocare → Sfârșit) with Notificare branch to Beneficiar.
- **Output document:** Registry entry + notification.
- **Notifications:** Beneficiari after processing.
- **Edge cases:**
  - Empty list → graceful exit.
  - Validation failures logged; processing continues for next record.

### 3.6-G. Evidența documentelor executorii
- **Name / Code:** 3.6-G (8.3.6.7).
- **Description:** Process for registering titluri executorii against Persoane asigurate.
- **Actors:**
  - **Sistem** — SI PS.
  - **Beneficiar** — insured person named on the titlu executoriu.
  - **Utilizator CNAS** — specialist CNAS/CTAS.
  - **Șeful direcției** — direcție head.
- **Periodicitate:** La solicitare.
- **Reguli de business:**
  - Dosier electronic per insured.
  - Journaling.
  - Form-level validation rules ensure correctness, completeness AND prevent duplicates per titlu executoriu.
  - References: Reg. persoanelor asigurate (8.2.4), **Reg. documentelor executorii (1.3.8 — referenced as 8.3.8 elsewhere)**.
- **Inputs:**
  - Formular de înregistrare (electronic doc).
  - Copia scanată a titlului executoriu (electronic doc).
- **Outputs:**
  - Notificare privind titlul executoriu.
- **Steps:**
  - **3.6-G01 — Utilizator CNAS — Completarea formularului.** Based on titlul executoriu received from executorul judecătoresc, the CNAS user completes the registration form in the system; attaches the scanned copy. Result: înregistrată + recipisă.
  - **3.6-G02 — Șeful direcției — Examinarea formularului.** Reviews; if no objections, approves; otherwise returns to G01. On approval, the form is registered in Registrul documentelor executorii. System generates a notification to Beneficiar. Result: stored or returned + notification dispatched.
- **Diagram:** Figura G3.6 — Beneficiar/Utilizator CNAS/Șeful direcției/Sistem lanes; loop on "Se aprobă? Nu" → G01.
- **Output document:** Registry entry + notification.
- **Notifications:** Beneficiar on registration.
- **Edge cases:** Form returned for correction; duplicates blocked at validation.

---

## Annex 3.7 — Evidența Plății sumelor calculate și achitate (Payments evidence)

### 3.7-A. Calcularea prestațiilor spre plată
- **Trigger:** Scheduled automated run — once per month — OR admin-launched. Per prestation type the launch date(s) are configurable.
- **Actors:** MPay (govt payments service used for prestation payment), Sistem, Utilizator CNAS, Șeful direcției, Șeful direcției finanțe (head of finance — responsible for allocating financial resources).
- **Reguli de business:**
  - Formula(e) de calcul configured per prestation type.
  - Only **valid (active) decisions** are calculated.
  - Fixed-sum vs. parameter-based (contribuții, vechime, …).
  - Journaling.
  - References: Reg. persoanelor asigurate (8.2.4), Reg. deciziilor (8.3.9), Reg. conturi de plată (8.3.10), Reg. comenzilor (8.3.11), Reg. titlurilor executorii (8.3.8).
- **Inputs (computed):** active decisions, decisions ended in previous month, suspended decisions.
- **Calculation rules (3.7-A02):**
  - Verify computation period matches award period.
  - Verify no duplicate prior calculation for same Beneficiar + period + decision.
  - Backfill prior months not yet calculated within the award period (one register entry per month). Example: decision covers 15.07.2025–14.07.2027; if computing 09.2025 and 07/08 not yet computed (15.07–31.07 and 01.08–31.08), the system computes those backfills.
  - Decisions with status "Suspendat" → still compute but record status "Suspendat" in the registry.
  - Decisions with status "Terminat" → use award period as indicated in the termination decision.
  - Apply formulas per prestation type.
  - Compute reținere per titlu executoriu (per cuantum + remaining debt), record per executory doc in Registrul documentelor executorii with status "Draft".
  - Compute reținere per decizii de recalculare (except those with rambursare mode = "rambursarea la contul CNAS") per cuantum + remaining debt.
  - Persist calculations to Registrul de conturi de plată a prestațiilor.
- **Output:** Comanda mijloacelor financiare pentru achitarea prestațiilor sociale — **one per payment mode** (MPay, ONG, Penitenciar, Azil, peste hotare). PDF. Plus internal notifications.
- **Integration:** **MPay** — system pushes calculations with mode "MPay" to MPay AFTER the order reaches status "Spre achitare".
- **Workflow:**
  - **3.7-A03 — Sistem — generates output docs (Comenzi per mod).** Submits for review/signing to Utilizator CNAS and Șeful Direcției. Notifies Admin + responsible Direcție.
  - **3.7-A04 — Utilizator CNAS — examinare calcule și comenzi.** If OK signs; if not, requests Admin to re-run the calc procedure (cancelling prior results — admin has dedicated functionality).
  - **3.7-A05 — Șeful direcției — examinare.** If OK e-signs and forwards to Șeful direcției finanțe. Otherwise requests admin re-run. Comenzile are stored in Registrul comenzilor (meta + signed PDF).
  - **3.7-A06 — Șeful direcției finanțe — Executarea comenzilor.** Examines and checks the availability of financial resources. If available, status of Comanda → "Spre achitare"; system flips the status of the related calculations in Registrul de conturi personale to "Spre achitare"; status of related reținere entries in Reg. titlurilor executorii flips "Draft" → "Activ". System pushes to **MPay** for MPay-mode entries. Notifications go to direcția head, specialist, beneficiari.
- **Reconciliation:** Updated separately in 3.7-B (below).
- **Recovery:** Triggered when calc procedure is re-run (admin cancels prior results).

### 3.7-B. Actualizarea informației despre achitarea/rambursarea prestațiilor
- **Trigger:** Scheduled daily by system OR admin-launched.
- **Actors:** MPay, Sistem, Utilizator CNAS (monitorizare).
- **Reguli:**
  - Update prestation status ONLY for entries in Registrul de conturi de plată with status "Spre achitare".
  - Record rambursări in Registrul deciziilor, compartimentul "Date despre rambursarea prestațiilor".
  - Journaling.
- **Output:** Notificare cu rezultatele procedurii.
- **Steps:**
  - **3.7-B01 — Sistem — Lansarea procedurii.** Extract list of calcule with status "Spre plată". If empty, terminate.
  - **3.7-B02 — Sistem — Procesarea listei pentru actualizare.** Check in MPay whether each prestation was paid; if yes → status "Spre achitare" → "Achitat"; else next row.
  - **3.7-B03 — Sistem — Procesarea plăților rambursate.** Pulls reimbursed-sum info per CNAS account and records in Registrul deciziilor → "Date despre rambursarea prestațiilor" per Cont de plată. Notifies Admin + Utilizator CNAS.
- **Integration:** MPay query for payment confirmation.
- **Reconciliation:** Status transition "Spre achitare" → "Achitat"; rambursare amount recorded against decision + plată account.

### 3.7-C. Recalcularea prestației în urma modificării bazei de calcul
- **Trigger:** Scheduled daily by system OR admin-launched.
- **Actors:** Sistem, Utilizator CNAS, Șeful CNAS, Beneficiar.
- **Reguli:** Dosier electronic per insured; fixed/computed; journaling; Reg. persoane asigurate (8.2.4); Reg. deciziilor (8.3.9).
- **Outputs:**
  - Decizia privind recalcularea prestației.
  - Decizia privind recuperare sumei prestației.
  - Contul de plată.
  - Notificare privind decizia(le) aprobat(e).
- **Steps:**
  - **3.7-C01 — Sistem — Lansarea procedurii de selectare a persoanelor asigurate.** Extract persoane afectate by base-modifying corrected dări de seamă or other docs. If empty, terminate.
  - **3.7-C02 — Sistem — Procesare listă** with two sub-steps:
    - **3.7.1-C02 — Recalcularea sumelor prestațiilor:** check whether person beneficiated (classifier marks which prestation types are analyzed); for each decision recompute using fișa de calcul updated with new contributions (excluding annulled contributions). If deviation → generate Decizia de recalculare a prestației with fișa.
    - **3.7.2-C02 — Identificarea sumelor pentru recuperare:** if overpaid (recalc < prior), generate Decizia privind recuperarea sumelor per case; payer identified per CNAS criteria (Plătitor de contribuții OR Persoană asigurată).
  - **3.7-C03 — Utilizator CNAS — Examinarea deciziilor.** Can modify fișa de calcul, regenerate decisions, cancel with mandatory reason. For recovery decision: if from Persoană asigurată indicate the rambursare mode (out of active prestations OR to CNAS account). E-signs, forwards to Șeful CNAS.
  - **3.7-C04 — Șeful CNAS — Examinarea deciziilor.** If OK e-signs and finalizes. If recovery mode = "rambursarea la contul CNAS", system generates contul de plată and sends to Persoana asigurată (for payment via MPay or bank counter). Decizia de recalculare is registered in Registrul deciziilor; the prior decision is marked "Terminat" with date = approval date of the new one, motivul = "emiterea deciziei noi", și "Numărul deciziei terminării" = numărul deciziei noi (only when prestation sum changes). For Plătitor de contribuții payer: system generates tranzacții in personal account at the configured economic classification of revenue.
- **Diagram:** Figura C3.7 — multi-lane with loop on "Se aprobă? Nu" → C03.
- **Integration:** Cont de plată (for MPay payment by Persoană asigurată); CNAS personal accounts (for Plătitor de contribuții).
- **Recovery:** Decizia privind recuperarea sumelor — paths: rambursare din contul prestațiilor active (auto-deduction) OR rambursare la contul CNAS (manual payment by person/employer).

### 3.7-D. Recalcularea prestațiilor în urma modificării actelor normative
- **Trigger:** Manual launch by admin user. Launch options: prestation selection, filters, recalculation period, etc. (parameters identified during system design).
- **Actors:** Sistem, Utilizator CNAS, Șeful CNAS, Beneficiar.
- **Reguli:** Same as 3.7-C standard set.
- **Inputs:** Lista deciziilor identificate.
- **Outputs:** Decizia privind recalcularea prestației; Notificare privind decizia aprobată.
- **Steps:**
  - **3.7-D01 — Sistem — Lansarea procedurii de selectare a deciziilor.** Extract list per filter criteria. Empty → terminate.
  - **3.7-D02 — Sistem — Procesarea listei cu decizii.** Per decision: generate cererea, fișa de calcul (based on prior one), Decizia de recalculare. Notify CNAS user.
  - **3.7-D03 — Utilizator CNAS — Examinarea deciziei.** Can modify fișa; auto-recalc; rambursare mode if applicable. Decision can be canceled with mandatory motivation. E-signs, forwards to Șeful CNAS.
  - **3.7-D04 — Șeful CNAS — Examinarea deciziei.** Final approval. Records in Reg. deciziilor; prior decision marked Terminat (motive = emission of new), "Numărul deciziei terminării" set.

### 3.7-E. Suspendarea/restabilirea achitării prestațiilor sociale (la inițiativa CNAS)
- **Trigger:** La solicitare.
- **Actors:** Sistem, Beneficiar, Utilizator CNAS, Șeful direcției.
- **Reguli:**
  - Suspendare triggered when: prestation not collected in last 3 months OR prestation incorrectly calculated/allocated.
  - Form-level validation; journaling.
- **Inputs:** Formular de înregistrare.
- **Outputs:** Notificare privind suspendarea/restabilirea.
- **Steps:**
  - **3.7-E01 — Utilizator CNAS — Completarea formularului.**
  - **3.7-E02 — Șeful direcției — Examinarea formularului.** If OK: register in Reg. deciziilor the suspendare/restabilire on the indicated decisions; in Reg. de conturi de plată update status on related calculations:
    - Suspendare → calcule "Spre achitare" devin "Suspendat".
    - Restabilire → calcule "Suspendat" devin "Spre achitare".
    Generate notification to Beneficiar. Otherwise return to E01.

### 3.7-F. Înregistrarea deciziei de recuperare a sumelor la inițiativa CNAS
- **Trigger:** La solicitare.
- **Actors:** Sistem, Plătitor (Plătitor de contribuții OR Persoană asigurată), Utilizator CNAS, Șeful direcției.
- **Reguli:** System provides registration form with validation + transaction-formation rules in Plătitor de contribuții personal account. Role with registration right needed. Journaling. Form stored. References Reg. deciziilor (8.3.9).
- **Inputs:** Formular de înregistrare.
- **Outputs:**
  - Decizia de recuperare a sumelor.
  - Înregistrarea tranzacțiilor în contul personal al plătitorului.
  - Notificare privind emiterea deciziei.
- **Steps:**
  - **3.7-F01 — Utilizator CNAS — Înregistrarea formularului.** Set the payer (Plătitor de contribuții / Persoană asigurată) + rambursare method (only for Persoană asigurată). Validates. If valid, system generates decizia de recuperare. User examines; if OK forwards to Șef direcției; else regenerates.
  - **3.7-F02 — Șeful direcției — Examinarea deciziei.** E-signs OR returns to F01. If approved AND rambursare mode = "rambursarea la contul CNAS" → generate cont de plată, send to Persoană asigurată (MPay or bank counter). If payer = Plătitor de contribuții → generate transactions in payer's personal account. Notify Plătitor.

---

## Annex 3.8 — Structura Registrului documentelor executorii

| Field | Type | Required | Description |
|---|---|---|---|
| Numărul de identificare a titlului executoriu | string | Yes | Identifier of the executory title |
| Data titlului executoriu | date | Yes | Issue date of the executory title |
| Data înregistrării în sistem | datetime | Yes | When registered in SI PS |
| Suma datoriei | decimal | Yes | Debt amount |
| Codul CTAS | code | Yes | CTAS code of registering office |
| Statutul deciziei | enum | Yes | Activ / Stins / Anulat |
| Date despre debitor — IDNP | string | Yes | Debtor IDNP |
| Date despre debitor — Cod ID | string | Yes | Debtor internal ID |
| Date despre creditor — IDNO/IDNP | string | Yes | Creditor tax ID |
| Date despre creditor — Codul CNAS | string | Yes | Creditor CNAS code |
| Date despre creditor — Rechizitele bancare | string | Yes | Creditor banking details |
| Cuantumul stingerii (% sau lei) | decimal | Yes | Deduction quantum, either percent or absolute (lei) |
| Lista prestațiilor de pe care se decontează | list / enum | Yes | All prestations OR a selected subset |
| Data anulării | date | No | Annulment date |
| Motivul anulării | string | No | Annulment reason |
| **Plăți reținute (sub-record):** Numărul de identificare a calcului | string | Yes | Calculation reference |
| Plăți reținute: Suma reținută | decimal | Yes | Retained amount |
| Plăți reținute: Data reținerii | date | Yes | Date of retention |
| Plăți reținute: Statut | enum | Yes | Draft / Activ |

> Mențiune in TOR: structura poate fi modificată/completată pe parcursul analizei business al SI PS.

---

## Annex 3.9 — Structura Registrului deciziilor

| Field | Type | Required | Description |
|---|---|---|---|
| Numărul deciziei | string | Yes | Decision number |
| Data aprobării | date | Yes | Approval date |
| Tipul prestației | enum (classifier) | Yes | Per Clasificatorul pensiilor și prestațiilor sociale |
| Tipul deciziei | enum | Yes | acordare prestație / refuz prestație / recalculare prestație |
| Perioada de acordare | date range | Yes | Award period |
| Periodicitatea achitării | enum | Yes | unic / lunar / anual |
| Codul CTAS | code | Yes | CTAS code |
| Suma prestației divizată pe articole de cheltuieli | decimal list | Yes | Per CNAS expenditure classifier |
| Statutul deciziei | enum | Yes | Activ / Suspendat / Terminat / Anulat |
| Date despre Beneficiar — IDNP | string | Yes | Beneficiary IDNP |
| Date despre Beneficiar — Cod ID | string | Yes | Beneficiary internal ID |
| Modul de achitare | enum | Yes | MPay / Transfer ONG / Transfer penitenciar / Transfer Azil / Transfer peste hotare (țara, rechizite bancare, valuta) |
| Instituția beneficiară | classifier ref | Conditional | Penitenciar / azil / ONG (from classifier) |
| Data anulării | date | No | Annulment date |
| Motivul anulării | string | No | Annulment reason |
| Data terminării | date | No | Termination date |
| Motivul terminării | string | No | Termination reason |
| Referințe — Numărul de identificare a dosarului | string | Yes | Dosier reference |
| Referințe — Numărul deciziei terminării | string | Conditional | Termination decision number (when replaced) |
| Referințe — Numărul cererii | string | Yes | Source request number |
| Referințe — Data cererii | date | Yes | Source request date |
| **Date despre rambursarea prestațiilor** — Numărul contului de plată | string | Conditional | Payment account number |
| Date despre rambursarea — Identificatorul plății | string | Conditional | Payment identifier |
| Date despre rambursarea — Data rambursării | date | Conditional | Refund date |
| Date despre rambursarea — Suma rambursată | decimal | Conditional | Refunded sum |
| **Date despre suspendări** — Data suspendării | date | Conditional | Suspension date |
| Date despre suspendări — Motivul suspendării | string | Conditional | Suspension reason |
| Date despre suspendări — Data restabilirii | date | Conditional | Restoration date |
| Date despre suspendări — Motivul restabilirii | string | Conditional | Restoration reason |

> Structure may be extended during business analysis.

---

## Annex 3.10 — Structura Registrului de conturi de plată a prestațiilor

| Field | Type | Required | Description |
|---|---|---|---|
| Numărul de identificare a calcului | string | Yes | Calculation identifier |
| Data calculării | date | Yes | Calculation date |
| Tipul prestației | enum (classifier) | Yes | Per Clasificatorul prestațiilor |
| Perioada de calcul | date range | Yes | e.g. 01.07.2025–31.07.2025 |
| Codul CTAS | code | Yes | CTAS code |
| Suma prestației calculate (pe articole de cheltuieli) | decimal list | Yes | Per CNAS expenditure classifier |
| Suma reținerilor — pe titluri executorii | decimal | Conditional | Executory-title retentions |
| Suma reținerilor — pe decizii de recalculare | decimal | Conditional | Re-calculation retentions |
| Suma spre plată | decimal | Yes | Net payable amount |
| Statutul calcului | enum | Yes | Calculat / Spre achitare / Achitat / Suspendat / Anulat |
| Date despre Beneficiar — IDNP | string | Yes | |
| Date despre Beneficiar — Cod ID | string | Yes | |
| Date despre Beneficiar — Numele, prenumele | string | Yes | |
| Date despre Beneficiar — Adresa | string | Yes | |
| Modul de achitare | enum | Yes | MPay / Prin ONG (cu liste de Beneficiari și sume, pe acorduri bilaterale) / Penitenciar / Azil / peste hotare (țara, rechizite, valuta) |
| Instituția beneficiară | classifier ref | Conditional | Penitenciar / azil / ONG |
| Numărul comenzii | string | Conditional | Payment order number |
| Data anulării | date | No | |
| Motivul anulării | string | No | |
| Data suspendării | date | No | |
| Motivul suspendării | string | No | |
| Data achitării | date | Conditional | Payment date |
| Prestatorul de plata | string | Conditional | Payment provider |
| Sumele restituite prin MPay | decimal | Conditional | Refunded sums via MPay |
| Referințe — Numărul de identificare a deciziei | string | Yes | Source decision reference |

---

## Annex 3.11 — Structura Registrului comenzilor

| Field | Type | Required | Description |
|---|---|---|---|
| Numărul de identificare a comenzii | string | Yes | Order identifier |
| Data comenzii | date | Yes | Order date |
| Tipul prestației | enum (classifier) | Yes | Per classifier |
| Prestatorul de plată | string | Yes | Payment provider |
| Perioada de calcul | date range | Yes | Calc period |
| Articolul de finanțare (listă) | list of codes | Yes | Funding article(s) |
| Suma (listă) | list of decimals | Yes | Sum per article |
| Statutul | enum | Yes | Calculat / Aprobat / Spre achitare / Anulat |
| Data anulării | date | No | |
| Motivul anulării | string | No | |
| Atașamente — Comanda semnată în format PDF | file (PDF) | Yes | Signed order PDF |

---

## Annex 4 — Web services EXPOSED by SI PS

> Mențiune: list adjustable per technical documents signed with AGE; technical annexes to be provided at system-development stage.

| # | Service name | Purpose | Consumers (typical) | Operations (implied) | Data exchanged |
|---|---|---|---|---|---|
| 1 | Furnizare cod CPAS după IDNP | Return the CPAS code given an IDNP | Other state systems via MConnect | getCpasByIdnp | IDNP → CPAS code |
| 2 | Verificarea statutului certificatului medical | Check medical-certificate status | CNAM / health system | verifyMedCertStatus | Cert ID → status |
| 3 | Furnizare date bilete de tratament și plăți sociale achitate | Treatment vouchers + social payments paid to individuals | Public-sector consumers | getTreatmentTickets, getSocialPayments | IDNP/period → list |
| 4 | Cuantum pensiei în funcție de stagiul de cotizare (Cuantumul estimat al pensiei, MCabinet) | Estimate pension amount given contribution years | MCabinet | estimatePension | IDNP, planned stagiu → amount |
| 5 | Calculatorul vârstei de pensionare | Compute retirement age | MCabinet / public portal | computeRetirementAge | IDNP / DOB → age |
| 6 | Furnizare info din extrasul din cont | Statement-of-account data | MCabinet | getAccountStatement | IDNP, period → statement |
| 7 | Date despre persoane cu dizabilități și pensionari | Disabled persons + pensioners data | Cross-gov | getDisabledPersonsAndPensioners | filters → list |
| 8 | Detalii indemnizație de șomaj stabilit | Unemployment benefit details | ANOFM / MMPS | getUnemploymentBenefitDetails | IDNP → details |
| 9 | Date despre persoane cu dizabilități severe/accentuate/medii beneficiare de pensii și alocații, persoane care îngrijesc o persoană cu dizabilitate severă < 18 ani, pensionari | Detailed disability + care-giver data | Cross-gov | getDisabilityAndCarePensioners | filters → list |
| 10 | Statutul beneficiarilor de prestații sociale, pe categorii (dizabilități de pe urma războiului — severe, accentuate, medii; persoane cu dizabilități severe; cu dizabilități accentuate; cu dizabilități medii; participanți la al doilea război mondial; participanți acțiuni de luptă Afghanistan; lupte pentru apărarea integrității R. Moldova; boală actinică / Cernobîl; victime represiuni politice 1917–1990; dizabilități din copilărie; tutele cu copii minori cu dizabilități din copilărie; pensionari) | Statuses for special categories | Cross-gov | getBeneficiaryStatusByCategory | category, IDNP → status |
| 11 | Beneficiari pensii și alte prestații (Art. 283(1) Cod Fiscal — scutiri impozit bunuri imobile) | Tax-exemption confirmation data | SFS / cadastre | getBeneficiariesForTaxExemption | IDNP → result |
| 12 | Date despre beneficiar, tipurile prestațiilor, cuantum, data stabilirii/finisării și data achitării | General prestation summary | Cross-gov | getBeneficiaryPrestations | IDNP → list |
| 13 | Lista IDNP cu CPAS atribuit pentru perioada indicată (istoric) | History of CPAS assignments | ASP / SFS | getCpasAssignmentsHistory | period → list |
| 14 | Lista angajaților (persoane fizice cu contribuții declarate) după Cod fiscal/IDNO al agentului economic; întoarce codul de înregistrare la CNAS și denumirea agentului | Employee list per economic agent | SFS / banking | getEmployeesByTaxId | IDNO, period → list |
| 15 | Date privind achitarea/neachitarea (sau scutirea) contribuției pentru perioada activității în baza patentei de întreprinzător | Patent contribution status | SFS | getPatentContributionStatus | IDNP → status |
| 16 | Date despre beneficiarii de prestații sociale aferente persoanelor cu dizabilități | Disability prestation beneficiaries | Cross-gov | getDisabilityPrestationBeneficiaries | filters → list |
| 17 | Date privind luarea la evidență ca plătitor de contribuții la BASS | Contributor registration status | SFS / banks | getContributorRegistration | IDNO → status |
| 18 | Info plătitori — solduri 1 ianuarie + sumele obligatorii calculate (inclusiv majorări de întârziere) + sumele modificate/corectate | Annual contributor financial state | SFS / treasury | getContributorBalances | IDNO, year → balances |
| 19 | Furnizarea/returnarea listei IDNP cu CPAS pentru perioada indicată + atribuirea/returnarea codului CPAS în baza IDNP în lipsa acestuia | CPAS lookup + on-demand assignment | Cross-gov | getOrAssignCpas | IDNP → CPAS |
| 20 | Beneficiarii indemnizației de șomaj cu decizie emisă în SI CNAS (pentru perioada plății indemnizației, înregistrați la STOFM) | Unemployment benefit recipients | ANOFM | getUnemploymentBeneficiariesWithDecision | period → list |
| 21 | Diferite date în baza acordurilor internaționale | International agreement data | Foreign social security bodies | various per agreement | per agreement |
| 22 | Prestațiile stabilite din SI PS către EESSI | Electronic Exchange of Social Security Information | EESSI | exchangePrestationsToEESSI | per EESSI schema |
| 23 | Date financiare din SI PS către FMS | Financial data export | FMS (Ministry of Finance) | exportFinancialData | period → financial dataset |
| 24 | Date pentru generarea Informației privind achitarea contribuțiilor (perioada AAAA–AAAA, MCabinet) | Multi-year contribution paid-info | MCabinet | getContributionPaymentInfo | IDNP, period → info |
| 25 | După IDNP, lista prestațiilor pentru prestatori de plată — conectare la un cont bancar | List of prestations a payer can attach to a bank account | Banks | getPrestationsForPaymentProviderByIdnp | IDNP → list |

---

## Annex 5 — Web services CONSUMED by SI PS

> Mențiune: adjustable per AGE technical documents.

| # | Service | Provider | Purpose | Operations | Trigger |
|---|---|---|---|---|---|
| 1a | Date din RSP — modificarea persoanelor fizice + stoparea prestațiilor la deces | ASP (Agenția Servicii Publice) | Individual record changes; death event halts payments | subscribe/poll RSP changes | RSP events / cron |
| 1b | Date din RSP — cerere reexaminare pensie limită vârstă post-stabilire | ASP | Re-examination requests | on-demand pull | Reexamination workflow |
| 1c | Date din RSP — cerere acordare pensie limită vârstă + pensie dizabilitate + verificarea statutului cererilor | ASP | New pension applications | on-demand pull | Pension application |
| 1d | Lista autorizații de emigrare pentru perioada indicată (oprire prestații la deces) | ASP | Emigration authorizations | period query | Cron |
| 1e | Date din Registrul de Stat al Unităților de Drept (RSUD) — evidența plătitorilor | ASP | Legal-entity payer data | subscribe/poll | RSUD events / cron |
| 2a | IPC18 (date corectare) — reținerea impozit, prime AOAM, contribuții CAS | SFS | Tax return corrections | bulk import | Period / event |
| 2b | DSA18/DSA19 (date istorice) — drepturi sociale în sistemul public | SFS | Historical social rights | bulk import | Migration / event |
| 2c | IU17 (Declarația privind impozitul unic) | SFS | Unique-tax declaration | bulk import | Periodic |
| 2d | CAS18 (date istorice) — contribuțiile CAS + evidență nominală | SFS | Historical CAS data | bulk import | Migration |
| 2e | Taxi18 — calculul impozit pe venit, prime AOAM, transport rutier de pasageri în regim de taxi | SFS | Taxi sector contributions | bulk import | Periodic |
| 2f | IRM19 — Informație pentru stabilirea drepturilor sociale și medicale aferente raporturilor de muncă | SFS | Labour-relation rights data | bulk import | Periodic |
| 2g | IPC21 — reținerea impozit, prime AOAM, contribuții CAS | SFS | Current tax/AOAM/CAS withholding | bulk import | Periodic |
| 2h | Lista patente solicitate + refuzate (Legea 93-XIV/1998) | SFS | Patent applications/refusals | period query | Cron |
| 2i | Plăți BASS din vitrina cu date CNAS + confirmare recepție | SFS / vitrina date | BASS payment flows | bidirectional API | Continuous |
| 2j | Set determinat de date din IST21 + IZL21 (Hot. Guv. 316/2016 — măsuri COVID-19) | SFS | COVID-19 support measures | bulk import | Periodic |
| 2k | Lista contribuabili cu modificări în data indicată | SFS | Daily contributor changes | daily diff | Cron |
| 2l | Lista persoane fizice activitate independentă (înregistrate la SFS) | SFS | Self-employed individuals | period query | Cron |
| 3a | Date despre persoane fizice — eliberarea certificatului medical + verificarea statutului (Legea 289/2004) | MSn/CNAM (eCMND) | Medical certificate data | getCert / verifyStatus | On indemnity request |
| 3b | Evenimente naștere/deces (≤500 ids batch) — pull, confirm prelucrare; lista nașteri pe IDNP mamă; clasificatoarele eCNMD | MSn/CNAM (eCMND) | Birth/death events | listBirths, listDeaths, confirm | Cron |
| 4a | Schimbări status șomer + cuantum ajutor de șomaj | MMPS / ANOFM | Unemployment status + amount | subscribe/poll | Event |
| 4b | Liste de plată — beneficiari ajutor social + ajutor perioada rece | MMPS | Social aid lists | bulk import | Periodic |
| 4c | Beneficiari compensație la energie sub formă monetară (Art.123 Legea 241/2022) din SI "Vulnerabilitatea Energetică" | MMPS / SIVE | Energy compensation recipients | listBeneficiaries | Cron |
| 5a | Date determinării dizabilității (Hot. Guv. 357/2018) — context pensie de dizabilitate + e-Cerere | CNDDCM | Disability determination | getDisabilityFile | On request |
| 6a | Beneficiari protecție temporară (Hot. Guv. 21/2023 — strămutați Ucraina) | MAI | Temporary-protection beneficiaries | listBeneficiaries | Event |
| 7a | Persoane fizice condamnate/arestate + modificate (Sistemul "Registrul persoanelor reținute, arestate și condamnate") | ANP (Agenția Națională a Penitenciarelor) | Detention/sentencing data | subscribe/poll | Event (currently non-functional) |
| 8 | Prestații stabilite în alte sisteme transfrontaliere (EESSI etc.) | EESSI / Other | Cross-border prestation data | exchange | Per agreement |

---

## Annex 6 — Reports (preliminary list)

> Mențiune: list may be modified/extended; all reports run per stabilite criteria (input parameters).

1. **Raportul privind datoriile la plata prestațiilor sociale, neprimite de către beneficiari** — pe toate tipurile de prestații (plata pasivă). Audience: CNAS finance/operations. Frequency: ad-hoc/periodic. Parameters: tipuri prestație, perioadă.
2. **Raport sume calculate, achitate pensii peste hotarele R. Moldova** — international payments. Audience: CNAS finance.
3. **Raport restituire plăți necuvenite** — pe toate tipuri, restituiri prin MPay. Audience: CNAS finance, audit.
4. **Raport plată** — pe toate tipuri prestații.
5. **Raport plată prin instituțiile penitenciare** — pe tipuri prestații. Audience: operations + penitenciare reconciliation.
6. **Raport recuperare sume** — pe toate tipuri prestații prin MPay.
7. **Raport calcularea persoanelor aflate la întreținerea deplină a statului** — pe tipuri prestații.
8. **Raport evidența sumelor reținute în baza documentelor executorii** — pe tipuri prestații.
9. **Registrul plăților formate cartoteca** — pe toate tipuri prestații.
10. **Registrul plăților formate acordării noi** — pe toate tipuri prestații.
11. **Registrul privind suspendarea plăților** — pe toate tipuri prestații.
12. **Registrul de evidență a sumelor achitate/neachitate** — pe toate tipuri prestații.
13. **Registrul de evidență a deciziilor sumelor achitate necuvenit prin MPay** — pe toate tipuri prestații.
14. **Registrul de evidență a datoriilor față de beneficiar** — pe toate tipuri prestații.
15. **Registrul de evidență privind recuperarea sumelor reținute din plată** — pe toate tipuri prestații.
16. **Registrul de evidență a sumelor calculate pentru beneficiarii din instituțiile penitenciare** — pe tipuri prestații.
17. **Registrul de evidență a sumelor calculate pentru beneficiarii aflați la întreținerea deplină a statului**.
18. **Registrul de evidență a sumelor reținute în baza documentelor executorii**.
19. **Rapoarte MIS** — a. Statistice; b. Analitice; c. Operative. Audience: management.
20. **Fișa plătitorului** — payer info card.
21. **Lista plătitorilor înregistrați**.
22. **Lista plătitorilor lichidați**.
23. **Lista plătitorilor luați la evidență**.
24. **Bilanțul plătitorilor luării la evidență**.
25. **Lista titularilor patentei de întreprinzători recepționată de la SFS**.
26. **Lista persoanelor care desfășoară activitate independentă recepționată de la SFS**.
27. **Lista rezidenților parcurilor IT**.
28. **Lista persoanelor pentru care s-a efectuat calculul contribuțiilor de asigurări sociale de CNAS** (activitate independentă, titulari patentă, etc.) în perioada.
29. **Declarația în forma aprobată pe un plătitor și o perioadă concretă**.
30. **Informația privind prezentarea Dărilor de seamă/declarațiilor**.
31. **Lista / Numărul declarațiilor pe perioadă de raportare**.
32. **Lista / Numărul declarațiilor prezentate într-o perioadă de timp**.
33. **Darea de seamă generalizatoare** (pe republică, pe CTAS, pe plătitori).
34. **Contul curent al plătitorului** — pe tranzacții, pe clasificații bugetare, pe lună. Template detailed in 8.7.3.1.
35. **Document de plată în format aprobat**.
36. **Lista documentelor de plată**.
37. **Informația privind veniturile încasate la bugetul asigurărilor sociale de stat (pe luni) în anul**.
38. **Informația privind veniturile încasate la BASS (pe zile) în luna/anul**.
39. **Informația pe CTAS privind veniturile încasate la BASS în perioada**.
40. **Informația privind veniturile încasate la BASS de la plătitorul de contribuții la BASS în perioada**.
41. **Informația privind veniturile încasate la BASS la codul CNAS „1999" în perioada**.
42. **Informația cu privire la trecerea soldurilor de la un plătitor la altul în perioada**.
43. **Lista plătitorilor la care au fost corectate plățile de la cod fiscal "999" la cod fiscal corect**.
44. **Lista plătitorilor la care au fost corectate plățile de la un cod fiscal la altul**.
45. **Borderoul documentelor de plată prezentate spre executare din cont** — template detailed in 8.7.3.5: columns Nr d/o, Tip document, Nr. document, Data documentului, Data extr. bancar transmis trezorerie, Plătitor (cod fiscal, denumire, IBAN/cont trezorerial), Beneficiar (cod fiscal, denumire, IBAN/cont trezorerial), Suma, Destinația plății, Statutul documentului.
46. **Informație privind cererile de corectare a plăților la BASS**.
47. **Informație privind cererile de restituire a sumei din BASS în perioada**.
48. **Informație privind cererile de eșalonare a penalităților**.
49. **Informația înregistrare a contribuțiilor calculate/micșorate în baza altor documente** (acte de control, decizii de anulare, etc.) în perioada.
50. **Informația privind luarea la evidență specială a obligațiilor la BASS** — template detailed in 8.7.3.2: columns Nr d/o, Cod/ID CTAS, Cod/ID CNAS, Denumire prescurtată plătitor, Cod Fiscal, Numărul Deciziei, Data Deciziei, Data înregistrării în sistem, Codurile ECO (121100, 121310, 121410, …), Statutul plătitorului în cartela.
51. **Informația privind restabilirea de la evidența specială a obligațiilor la BASS** — same columns as #50.
52. **Informația cu privire la Deciziile de recuperare de la angajatori a sumelor plătite necuvenit sub formă de prestații înregistrate în perioada**.
53. **Informația cu privire la recuperarea de la angajatori a sumelor plătite necuvenit sub formă de prestații înregistrate în perioada**.
54. **Informația privind sumele majorărilor de întârziere calculate**.
55. **Descifrarea penalităților calculate în mod automat** — template detailed in 8.7.3.1.3.
56. **Sumele achitate după intentarea procedurii de insolvabilitate**.
57. **Informația despre perioadele pentru care s-au achitat creanțele aferente contribuțiilor în procesul de insolvabilitate**.
58. **Lista plătitori în procedura de insolvabilitate cu suma creanțelor istorice achitate**.
59. **Registrul de înregistrare a deciziilor de restituire**.
60. **Informația privind actele de control efectuate de organele abilitate în perioada**.
61. **Informația privind soldurile la BASS conform documentelor executorii privind răspunderea subsidiară**.
62. **Extrasul din cont a persoanei asigurate** — contribuții, relații de muncă, prestații de asigurări sociale.

### Standard report-formation criteria (sec. 8.7.3.1, 8.7.3.2, 8.7.3.3)
For Contul curent al plătitorului la BASS (General + Desfășurat) and Rapoarte aferente evidenței speciale, parameters:
- perioada înscrierii în SI: de la ___ până la ___
- codul ID/CNAS
- codul fiscal / IDNO / IDNP
- denumirea plătitorului (indicarea unei părți din denumire)
- cod ID/CTAS

Combined evidență specială/restabilire report: sums "luare" reflected with `+`, sums "restabilire" with `−`.

---

## Annex 7 — Modele de cereri, decizii, rapoarte

### 8.7.1 Modele de cereri
- Application form templates exist as **PDF image renderings only** (no textual content extracted):
  - PDF p306 → `images/pdf_p306_full.png`, `images/pdf_p306_1.png`
  - PDF p307 → `images/pdf_p307_full.png`, `images/pdf_p307_1.png`
  - PDF p308 → `images/pdf_p308_full.png`, `images/pdf_p308_1.png`

### 8.7.2 Modele de decizii
- Decision templates exist as PDF image renderings only:
  - PDF p309 → `images/pdf_p309_full.png`, `images/pdf_p309_1.png`

### 8.7.3 Modele de rapoarte
- **8.7.3.1 Contul curent al plătitorului la BASS**
  - 8.7.3.1.1 General (images: pdf_p310_full.png, pdf_p310_1.png)
  - 8.7.3.1.2 Desfășurat (images: pdf_p311_full.png, pdf_p311_1.png)
  - 8.7.3.1.3 Descifrarea majorării de întârziere (images: pdf_p312_full.png, pdf_p312_1.png)
- **8.7.3.2 Rapoarte aferente evidenței speciale** — inline tabular template (columns: Nr d/o, Cod/ID CTAS, Cod/ID CNAS, Denumire prescurtată plătitor, Cod Fiscal, Numărul Deciziei, Data Deciziei, Data înregistrării în sistem, Codurile ECO 121100/121310/121410/…, Total, Statutul plătitorului în cartela). Two report variants: "luarea la evidența specială" and "restabilirea". A combined report (page 314) flags `+` for luare, `−` for restabilire.
- **8.7.3.3 Rapoarte aferente corectării datelor în Registrul de stat al evidenței individuale** — image only: pdf_p314_full.png, pdf_p314_1.png.
- **8.7.3.4 Rapoarte aferente evidenței plătitorilor de contribuții la BASS** — images only: pdf_p315_full.png, pdf_p315_1.png, pdf_p315_2.png.
- **8.7.3.5 Rapoarte aferente restituirii sumelor din BASS** — inline table "Borderoul documentelor de plată prezentate spre executare din contul ___" with columns: Nr d/o, Tip document, Nr. documentul, Data documentului, Data extr. bancar transmis trezorerie, Plătitor (Cod fiscal, Denumire, IBAN/cont trezorerial), Beneficiar (Cod fiscal, Denumire, IBAN/cont trezorerial), Suma, Destinația plății, Statutul documentului, Total. Image: pdf_p316_full.png, pdf_p316_1.png.

**Note:** Most templates in Annex 7 are image-only (rendered PDF pages); textual content of cereri/decizii templates is NOT extracted into the markdown and must be reviewed visually from the PNGs.

---

## Part B — DOCX: Documentația standard

> Source: `f7r_ir-ds_servicii_omf_115_15_09_2021 dezvoltare SI Protecția Socială CNAS 2026.docx` — Anexa nr.1 la Ordinul ministrului finanțelor nr. 115 din 15.09.2021 (Documentația Standard).
>
> **CRITICAL FINDING:** Part B is **NOT a software engineering requirements catalog** (LIC, ARH, INT, PSR, FLEX, UI, MR, SEC, etc.). It is the **Moldovan public procurement standard documentation** (procurement law package, Ordin MF 115/2021). It contains the public-procurement procedure framework, qualification criteria, evaluation methodology, and the contract template for CNAS to procure the development of SI "Protecția Socială". The Caiet de sarcini (Termeni de referință) — i.e., the technical/functional requirements catalog with codes such as LIC/ARH/INT/PSR/FLEX/UI/MR/SEC — is referenced as **Anexa nr. 21 / Anexa nr. 7 of the contract** but is **NOT inlined** into Part B; it is the very Part A material already covered by other research files. Part B itself contains **no new coded engineering requirements**.
>
> Therefore, the requested "every coded requirement that does NOT also appear in Part A" yields **zero coded requirements**. Everything in Part B that has substantive content for our project is procurement/contract scaffolding — captured below as new content (it does not appear in Part A).

### B.1 Sections of Documentația Standard (Instrucțiuni pentru autorități contractante și ofertanți)

| Section | Title | Content |
|---|---|---|
| Sect. 1 | Dispoziții generale | Procurement procedure, anexele to be used; Acord-cadru ref (HG 694/2020); negociere ref (HG 599/2020); language (română); excluded/forbidden acts (Art. 42, Legea 131/2015) |
| Sect. 2 | Calificarea candidaților/ofertanților | Eligibility, DUAE (Doc Unic de Achiziții European), exclusion grounds (pct. 22, 23 — Art. 19(2)(3), Art. 16(6) Legea 131/2015), capacity (economic-financial, technical-professional), ISO 9001, EMAS, asocieri, terț susținător |
| Sect. 3 | Pregătirea/Elaborarea ofertelor | Caiet de sarcini detailing energie electrică, gaze, energie termică, apă-canalizare, produse petroliere (carduri PECO, formule de preț PECO); ofertă conține Propunere tehnică (Anexa 22 — Specificații tehnice) + Propunere financiară (Anexa 23 — Specificații de preț) + DUAE + Garanție (Anexa 9) |
| Sect. 4 | Depunerea și deschiderea ofertelor | SIA RSAP electronic submission; DUAE supporting documents on demand |
| Sect. 5 | Evaluarea și compararea ofertelor | DUAE verification, "abateri neînsemnate", corectare erori aritmetice, anormal de scăzut (Art. 70 Legea 131/2015) |
| Sect. 6 | Atribuirea contractului | Anulare procedură (Art. 71); garanție bună execuție; contract înregistrat la Trezoreria Ministerul Finanțelor; contestații la ANSC |

### B.2 Anexele Documentației Standard (formulars referenced)

| # | Anexa | Title |
|---|---|---|
| 1 | nr. 1 | Anunț de intenție |
| 2 | nr. 2 | Anunț de participare (inclusiv preselecție/negociate) |
| 3 | nr. 3 | Invitație de participare etape preselecție/negociate |
| 4 | nr. 4 | Proces-verbal rezultate preselecție |
| 5 | nr. 5 | Anunț de atribuire |
| 6 | nr. 6 | Anunț privind modificarea contractului |
| 7 | nr. 7 | Cerere de participare |
| 8 | nr. 8 | Declarație privind valabilitatea ofertei |
| 9 | nr. 9 | Scrisoare de garanție bancară (pentru ofertă) |
| 10 | nr. 10 | Garanția de bună execuție |
| 11 | nr. 11 | Informații privind asocierea |
| 12 | nr. 12 | Declarație lista principalelor livrări/prestări 3 ani |
| 13 | nr. 13 | Declarație dotări specifice, utilaj, echipament |
| 14 | nr. 14 | Declarație personal de specialitate |
| 15 | nr. 15 | Lista subcontractanților |
| 16 | nr. 16 | Angajament terț susținător financiar |
| 17 | nr. 17 | Declarație terț susținător financiar |
| 18 | nr. 18 | Angajament susținere tehnică și profesională |
| 19 | nr. 19 | Declarație terț susținător tehnic |
| 20 | nr. 20 | Declarație terț susținător profesional |
| 21 | nr. 21 | **Caiet de sarcini (Termeni de referință)** — points to Part A content of TOR |
| 22 | nr. 22 | Specificații tehnice |
| 23 | nr. 23 | Specificații de preț |
| 24 | nr. 24 | Contract — model |
| 25 | nr. 25 | Acord adițional |
| 26 | nr. 26 | Acord-cadru |

### B.3 Anunț de participare — Procurement parameters (Anexa nr. 2)

- **Autoritate contractantă:** Casa Națională de Asigurări Sociale (CNAS).
- **IDNO CNAS:** 1004600030235.
- **Sediul CNAS:** mun. Chișinău, str. Gheorghe Tudor 3.
- **Tel:** 022-257-681 / 022-257-840 / 022-257-752.
- **Email:** achizitiicnas@cnas.gov.md; web: www.cnas.gov.md.
- **Procedura:** Licitație Publică Deschisă (electronică prin SIA RSAP).
- **Cod CPV:** 72000000-5 (Servicii IT: consultanță, dezvoltare de software, internet și asistență).
- **Obiect:** Servicii de elaborare și implementare a unui nou sistem informațional „Protecția Socială" pentru anii 2026–2028.
- **Valoare estimată:** 37 500 000,00 lei fără TVA (37.5M MDL ≈ ~2.0M EUR).
- **Termen contract:** 2026–2028 (valabilitate până la 31.12.2028).
- **Garanție ofertă:** 1% din suma ofertei fără TVA.
- **Garanție bună execuție:** 5% din suma contractată cu TVA.
- **IBAN CNAS:** MD84TRPFAH518710A01691AA, Trezoreria de stat, TREZMD2X.
- **Procedură ID:** c7a17a99-ff95-4e70-b430-52dcacc2fba7.
- **Anunț ID/versiune:** c5287c6c-4295-4fd7-a69c-35d83cc98cf2-01.
- **Termen valabilitate ofertă:** 90 zile.
- **Limba:** Limba de stat (română).
- **Anunț intenție:** BAP nr. 10 din 03.02.2026.
- **Criteriu adjudecare:** cel mai mic preț fără TVA (textul inclus contradictoriu: și „cel mai bun raport calitate-preț" cu ponderi).
- **Ponderi factori evaluare:** Preț 40% / Experiență proiecte similare 30% / Capacitate tehnică-profesională 20% / Calitate propunere tehnică + plan de implementare 10%.
- **Procedură accelerată:** Reducere 5 zile (Art. 47(6) Legea 131/2015).
- **WTO GPA:** DA (intră sub incidența Acordului OMC).
- **Președinta grup de lucru:** Maia Moraru.

### B.4 Cerințe de calificare cumulative (Anunțul de participare, tabel)

- Cerere participare (Anexa 7) — semnată electronic.
- Declarație valabilitate ofertă (Anexa 8) — semnată electronic.
- Specificații de preț (Anexa 23) — semnate electronic.
- Specificații tehnice (Anexa 22) — semnate electronic.
- Garanție ofertă 1% (Anexa 9) — semnată electronic.
- DUAE (Formularul standard) — semnat electronic.
- **Lipsa datoriilor** la impozite, taxe, contribuții — verificat prin MConnect / portal.guvernului.md la deschidere oferte.
- **Capacitate economică:** ≥3 ani experiență similară; **Cifra de afaceri**: ≥10 000 000 lei pentru fiecare din ultimii 3 ani.
- **Capacitate tehnică-profesională:** experiență în proiecte similare; calitatea planului de implementare.
- Plan de implementare detaliat (etape, termene, responsabilități, resurse, comunicare, riscuri, schimbări, calitate, monitorizare, abateri, documentație).
- Plan financiar detaliat per livrabil (denumire, etapă/activitate, termen, cost, criterii acceptanță, trasabilitate).
- Fără condamnări penale (organizație criminală, corupție, fraudă, spălare bani, terorism, exploatare copii, trafic persoane) — ultimii 5 ani.
- Nu se află în insolvabilitate.
- Declarație beneficiari efectivi (Ordin MF 145/2020) — la desemnare câștigător.

### B.5 Criterii de atribuire (Anexa nr. 2 din Anunț — detaliate)

**Factor 1 — Prețul ofertei (40 puncte)**
- Formula: P(n) = (Preț minim ofertat / Preț ofertat(n)) × Pmax.
- Preț anormal de scăzut → clarificări scrise per Art. 70 Legea 131/2015; respingere dacă nu se justifică.

**Factor 2 — Experiența ofertantului în proiecte similare (30 puncte)**
- **Cerință de bază:** minim 3 proiecte TIC de complexitate similară implementate cu succes în ultimii 15 ani; per proiect: denumire, beneficiar (public/privat), domeniu (securitate socială/asigurări sociale), perioadă, valoare, descriere complexitate funcțională+tehnică, rol ofertant, stadiu, data finalizării; documente: contracte, procese-verbale recepție finală, certificate bună execuție, recomandări.
- **Sub-criterii (cumulate):**
  - **Complexitate funcțională (10p):** baremul 10/7/3/0 — administrare registre, raportare, schimb date sisteme publice, interoperabilitate (MConnect, EESSI, alte sisteme naționale), procesare batch, interfețe utilizator (GUI).
  - **Complexitate tehnică (10p):** baremul 10/7/3/0 — arhitectură, securitate informațională (autentificare, autorizare, jurnalizare/audit, criptare), integrare sisteme terțe, volum date + utilizatori, disponibilitate+performanță, continuitate operațională+disaster recovery, protecția datelor cu caracter personal.
  - **Complexitate organizațională (10p):** baremul 10/7/3/0 — număr instituții implicate, număr utilizatori finali + roluri operaționale, coordonare cu autorități publice centrale/locale, activități instruire, change management.

**Factor 3 — Capacitatea tehnică și profesională (20 puncte) — Tabel 3.1 + Tabel 3.2**

*Tabel 3.1 — Calificare/competență personal-cheie (10 puncte). Per principle Da/Nu, 74 criterii totale, formula P_i(%) = (P_i/74) × 100, scară 1–10p.*

| Rol | Studii | Cerințe-cheie |
|---|---|---|
| Manager de Proiect | master TIC/economie | ≥7 ani management proiecte TIC; ≥4 ani management echipe/proiecte (preferabil guvernamental) Waterfall/Hibrid; ≥2 proiecte referință SI similar; cert. PMP/Prince2/PM2; română+engleză; stagiu firmă ≥4 ani |
| Business Analyst | licență TIC/economie | ≥5 ani BA; metodologii moderne, standarde TIC gov MD; ≥2 proiecte similare în ultimii 3 ani; unit testing + CI; cert. CBAP avantaj; română+engleză; stagiu firmă ≥3 ani |
| System Architect | licență TIC | ≥5 ani; metodologii moderne; ≥2 proiecte similare ultimii 3 ani; testare modulară + CI + DevOps k8s; cert. TOGAF 9 / CTA avantaj; română+engleză; stagiu ≥3 ani |
| Team Leader / Senior software developer | master TIC | ≥7 ani dezvoltare SI complexitate similară pe stiva tehnologică propusă; ≥2 proiecte similare ultimii 3 ani; unit testing, CI, DevOps k8s; integrare SI prin SOAP/REST; certificare tehnologii avantaj; română+engleză; stagiu ≥3 ani |
| Dezvoltator / Administrator Bază de Date | licență TIC | ≥5 ani; ≥2 proiecte similare ultimii 3 ani; proiectare/optimizare BD; unit testing + CI; certificare BD avantaj; română+engleză; stagiu ≥3 ani |
| Dezvoltator / Specialist DevOps | licență TIC | ≥5 ani; ≥2 proiecte similare; CI/CD k8s; unit testing + DevOps; certificare CI/CD avantaj; română+engleză; stagiu ≥3 ani |
| Dezvoltator / Expert Integrare | licență TIC | ≥5 ani; ≥2 proiecte similare; CI/CD; unit testing + DevOps k8s; certificare CI/CD avantaj; română+engleză; stagiu ≥3 ani |
| Inginer Asigurare Calitate (QA) | licență TIC/relevant | ≥5 ani testare SI complexitate similară; ≥2 proiecte similare ultimii 3 ani; testare funcțională + performanță (load/stress) + securitate (OWASP Top 10); testare automatizată; cert. ISTQB avantaj; română+engleză; stagiu ≥3 ani |
| UX/UI designer | licență TIC/Design/UX/UI/Comunicare vizuală | ≥3 ani proiectare platforme web complexe / e-servicii (preferabil bancar / onboarding digital / portal gov); Figma avansat, FigJam, principii UX, wireframing, prototipare, accesibilitate WCAG; design systems; certificări Product Design/UX/User Research avantaj; familiaritate Design Thinking; gândire strategică, design centrat pe utilizator, design de servicii, documentare clară handoff (Figma specs, adnotări); română+engleză; stagiu ≥3 ani |
| Specialist securitate IT | licență TIC/relevant | ≥5 ani; ≥2 proiecte similare ultimii 3 ani; administrare sisteme + securitate OS/aplicații/BD; rețele (TCP/IP, VLAN, VPN, firewall, IDS/IPS, criptografie, PKI); identificare vulnerabilități, evaluare risc, proceduri; testare securitate (OWASP Top 10); cert. CompTIA Security+/CySA+/CISSP avantaj; română+engleză; stagiu ≥3 ani |
| Formator | licență TIC/relevant | abilități comunicare/instruire; ≥2 proiecte ultimii 3 ani cu instruire utilizatori SI; documentație instructivă; instruiri online; **română+rusă** (atenție: NU engleză); stagiu ≥3 ani |
| Personal non-cheie (echipa proiect) | — | Dezvoltare/implementare Web; proiectare/administrare BD; integrare cu SI externe; QA + automatizare testare; abilități instruire |

*Tabel 3.2 — Capacitate organizațională și metodologică (10 puncte). Cinci sub-criterii × 2p:*
- Structură organizațională clar definită (roluri, responsabilități, relații subordonare/coordonare).
- Mecanisme de planificare (plan de lucru, etape, livrabile, termene).
- Proceduri coordonare + comunicare (plan management comunicare).
- Mecanisme monitorizare, control, raportare (indicatori performanță, proceduri, frecvență, responsabilități).
- Proceduri gestionare riscuri / modificări / probleme (identificare, evaluare, mitigare, responsabilități, raportare).

**Factor 4 — Calitatea propunerii tehnice și a planului de implementare (10 puncte)**
- **Relevanța și conformitatea soluției tehnice (5p):** acoperire integrală TOR, arhitectură, componente, interdependențe, implementare, înțelegere cerințe. Barem 5/3/1/0.
- **Coerența și fezabilitatea planului de implementare (3p):** etape (inclusiv **etapa de migrare obligatorie**), activități, livrabile, termene realiste, dependențe. Barem 3/1/0.
- **Metodologia de lucru și abordarea implementării (2p):** etape metodologice, instrumente planificare/execuție/control, coordonare internă, comunicare cu autoritatea contractantă (ședințe, puncte de contact, raportare). Barem 2/1/0.

### B.6 Anexele 7–20 ale Documentației Standard (formularstandard)
- **Anexa 7 — Cerere de Participare**: text liber declarativ.
- **Anexa 8 — Declarație privind valabilitatea ofertei**: angajament durată valabilitate.
- **Anexa 9 — Scrisoare de Garanție Bancară**: condiții activare (retragere ofertă, neconstituire garanție execuție, refuz semnare contract, neîndeplinire condiții pre-semnare).
- **Anexa 10 — Garanția de bună execuție**: model bancar — irevocabilă, fără discuții/clarificări.
- **Anexa 11 — Informații privind asocierea**: lider, asociat secund, mod de asociere, contribuții, repartizare, condiții lichidare.
- **Anexa 12 — Declarație lista principalelor livrări/prestări 3 ani**: tabel Nr/Obiect contract/Beneficiar/Calitate furnizor/Preț/Perioadă.
- **Anexa 13 — Declarație dotări specifice**: tabel utilaje/echipamente — proprietate vs. chirie.
- **Anexa 15 — Lista subcontractanților**: nume + adresă, activități, valoare aprox., % din contract.
- **Anexa 16 — Angajament terț susținător financiar**: angajament irevocabil + sumă.
- **Anexa 17 — Declarație terț susținător financiar**: confirmare resurse reale + necondiționate.
- **Anexa 18 — Angajament privind susținerea tehnică și profesională**: angajament irevocabil resurse tehnice/profesionale.
- **Anexa 19 — Declarație terț susținător tehnic**: listă logistică/utilaje/echipamente proprietate/chirie.
- **Anexa 20 — Declarație terț susținător profesional**: personalul de specialitate Anul 1/2/3 + CV-uri.
- **Anexa 22 — Specificații tehnice (template)**: tabel cu Denumire serviciu, Model, Țara originii, Producător, Specificare deplină solicitată, Specificare propusă, Standarde referință. Pentru CPV 72000000-5: "În conformitate cu Caietul de sarcini (Termeni de referință)" + Moldova Standard.
- **Anexa 23 — Specificații de preț (template)**: tabel Cod CPV / Denumire / U.m. / Cantitate / Preț unitar fără+cu TVA / Sumă fără+cu TVA / Termen / IBAN / Discount %.

### B.7 Anexa nr. 24 — Contract — model

#### Part I (General — obligatoriu, nu se modifică)

**Documente componente (ordine prioritate):**
1. Specificația de preț (Anexa nr.1 la contract)
2. Plan financiar detaliat (Anexa nr.2)
3. Raportul privind servicii/livrabile prestate (Anexa nr.3)
4. Actul de confirmare a serviciilor/livrabililor prestate (Anexa nr.4)
5. Acord privind asigurarea securității datelor și utilizarea accesului de la distanță la resursele informaționale CNAS (Anexa nr.5)
6. Declarație de confidențialitate (Anexa nr.6)
7. Caietul de sarcini (Termeni de referință) (Anexa nr.7)

**Clauze cheie:**
- 1. **Obiect:** Prestator livrează conform Caiet + Plan financiar (livrabile, termene, costuri).
- 2. **Termeni prestare:** 2026–2028. Originalele documentelor — la momentul prestării.
- 3. **Preț și plată:** lei MD. Plată prin transfer bancar, în baza serviciilor/livrabilelor efectiv prestate și recepționate, **≤25 zile calendaristice** de la semnarea actelor de recepție. Plăți etapizate per livrabil. Preț fix.
- 4. **Predare-primire:** condiții cumulative — conformitate Caiet + Plan financiar + Plan implementare; criterii acceptanță; act semnat de ambele Părți. Refuz recepție în caz neconformități; termen rezonabil pentru remediere.
- 5. **Standarde:** standardele și normativele de domeniu sau alte reglementări autorizate.
- 6. **Obligații:**
  - Prestator: prestare conform; notificare în 5 zile calendaristice de la semnare prin email (achizitiicnas@cnas.gov.md); calitate + criterii acceptanță; livrabile la termen; suport verificare/recepție; remediere neconformități; securitate date; respectarea legii.
  - Beneficiar: examinare/recepție în termen; verificare/confirmare; achitare; furnizare informații + suport; notificare neconformități.
- 7. **Circumstanțe care justifică neexecutarea (forță majoră):** războaie, calamități, etc. Informare în ≤10 zile. Atestare cu aviz organ competent. Modificare prin acord adițional.
- 8. **Rezoluțiune:** acord comun sau unilaterală (refuz prestare, nerespectare termene, neplată, nesatisfacere pretenții, Art. 19 Legea 131/2015, modificare substanțială Art. 76, încălcare gravă). Notificare 5 zile lucrătoare; răspuns 5 zile lucrătoare.
- 9. **Reclamații:** cantitate la recepție; calitate în 5 zile + certificat organizație independentă; examinare în 3 zile; remediere în 5 zile.
- 10. **Sancțiuni:**
  - Garanție bună execuție: 5% din valoarea contractului.
  - **Penalitate întârziere Prestator: 0,1%/zi din valoarea serviciilor neexecutate, dar ≤5% din valoarea totală a Contractului.** ≥15 zile întârziere → justificare scrisă obligatorie. Refuz executare → reținere garanție bună execuție.
  - **Penalitate întârziere Beneficiar: 0,1%/zi din suma neachitată, dar ≤5% din suma totală.**
- 11. **Drepturi de proprietate intelectuală:** Prestator despăgubește pentru încălcare drepturi terți. **Toate rezultatele obținute aparțin Beneficiarului fără limitare în timp.**
- 12. **Confidențialitate:** acces la informații confidențiale + date cu caracter personal + informații condiții speciale acces. Conform Anexa 5 + Anexa 6.
- 13. **Dispoziții finale:** litigii pe cale amiabilă, apoi instanță R. Moldova; modificări prin acord adițional; ne-transmisibil; valabil 31.12.2028.

#### Part II (Condiții speciale)

- 1. **Condiții prestare/achitare:** etapizat per Plan implementare + Plan financiar; modificare prin acord în circumstanțe neimputabile, fără depășirea termenului general și fără ajustarea valorii.
- 2. **Obligațiile Prestatorului:** prestare conform Plan implementare + standarde; raportare incidente; trasabilitate; rapoarte periodice; echipa proiect conform calificări (notificare modificări); confidențialitate/integritate/securitate; remediere.
- 3. **Obligațiile Beneficiarului:** info + documente + acces în timp util; desemnare persoane responsabile coordonare/recepție; recepție în termen rezonabil; notificare neconformități; acces securizat infrastructură; achitare.
- 4. **Livrabile + acceptanță:** structură/conținut Plan implementare + Plan financiar; documentație justificativă (rapoarte, descriere funcțională/tehnică, manuale, **cod sursă după caz**); Act predare-primire; ajustări fără costuri suplimentare în termen convenit.
- 5. **Raportare și monitorizare:** rapoarte periodice **lunare per livrabil** sau modalitate agreată; conține activități, livrabile, riscuri/probleme; Beneficiar monitorizează și evaluează.
- 6. **Cadru normativ aplicabil:**
  - Codul muncii nr. 154/2003;
  - Legea nr. 133/2011 privind protecția datelor cu caracter personal;
  - Politica de protecție a datelor cu caracter personal în cadrul CNAS;
  - Legea nr. 186/2008 privind securitatea și sănătatea în muncă;
  - Alte acte normative și standarde domeniu IT și securitate informație conform Caiet sarcini Anexa 7.
- 7. **Dispoziții finale specifice:**
  - **Toate livrabilele realizate devin proprietatea Beneficiarului**, cu respectarea drepturilor de proprietate intelectuală prevăzute de legislație.
  - Modificare volum/livrabile doar cu acord scris ambele Părți.
  - Prestator răspunde pentru calitate + conformitate pe întreaga durată de implementare.

#### Sub-anexele contractului

- **Anexa 1 — Specificația de preț:** Preț unitate per 1 om/oră fără+cu TVA + Total fără+cu TVA.
- **Anexa 2 — Plan financiar detaliat:** Denumire livrabile / Grafic executare / Cost fără+cu TVA.
- **Anexa 3 — Raport privind servicii/livrabile prestate.**
- **Anexa 4 — Act de confirmare a serviciilor/livrabililor prestate.**
- **Anexa 5 — Acord asigurare securitate date + acces de la distanță la resurse CNAS:**
  - Login + parolă (caracter secret, ≥8 simboluri, registre diverse, schimbare lunară).
  - Administrator desemnat (în 2 zile, lista utilizatorilor aprobată de Prestator).
  - Notificare modificare listă utilizatori în 2 zile lucrătoare.
  - Pentru utilizatori noi: NPP, IDNP, funcția, resursele de accesare, domeniul de activitate.
  - Politica "ecranului curat" (finisare obligatorie sau deconectare terminal).
  - Neadmiterea persoanelor terțe la LAM ale utilizatorilor.
  - Nu copiere/difuzare nesancționată.
  - Informare imediată despre incidente de securitate.
  - Răspundere conform legislației.
- **Anexa 6 — Declarație de confidențialitate:** subsemnatul, IDNP, calitate, declar:
  1. Asigur confidențialitate datelor.
  2. Nu divulg terților.
  3. Nu folosesc info în beneficiu personal/terță parte.
  4. Informez imediat despre cazuri de divulgare.
  5. La demisie, nu divulg info confidențială colectată.
  - Răspundere administrativă/civilă/penală.
- **Anexa 7 — Caietul de sarcini (Termeni de referință) VERSIUNEA 2** — pointer to the body of TOR ("Servicii de elaborarea și implementare a unui nou sistem informațional Protecția Socială pentru anii 2026–2028 VERSIUNEA 2").

### B.8 Net new content for the TODO (Part B-only items not in Part A)

These are items that exist only in Part B (procurement + contract layer) and must be honored by the implementation team, but are not coded as engineering requirements:

1. **MVP/Procurement timeline anchor:** Contract valid through **31.12.2028**; total ceiling **37,500,000 MDL fără TVA**.
2. **Acceptance criteria are contractual artifacts**, not just engineering rules: every livrabil requires an Act de predare-primire signed by both parties; criteria de acceptanță must be defined in the Plan financiar (Anexa 2).
3. **Mandatory livrabile structure:** Each livrabil = denumire + etapă/activitate + termen + cost + criterii acceptanță. The Plan financiar is the contractual matrix that ties technical, temporal, and financial trasabilitate.
4. **Migration phase is mandatory** (Factor 4, sub-criteriu coerență plan implementare): the plan must include "etapa de migrare" — absence is a 0-point criterion.
5. **Reporting cadence:** Rapoarte periodice — lunare per livrabil sau modalitate agreată — conțin activități, livrabile, riscuri/probleme.
6. **Personnel non-changeable without notification:** Prestator must maintain the contractually proposed team; orice modificare se notifică Beneficiarului.
7. **Romanian + Russian for Formator** (NOT engleză) — formator role serves trainees who speak Russian-only.
8. **Source code as livrabil:** Anexa 7 Part II §4.2 — livrabilele vor fi însoțite de documentație justificativă (rapoarte, descriere funcțională/tehnică, manuale, **codul sursă după caz**). Implication: source code can be requested as a contractual artifact.
9. **Intellectual property:** "Toate livrabilele realizate în cadrul Contractului devin proprietatea Beneficiarului, cu respectarea drepturilor de proprietate intelectuală prevăzute de legislație." (no time limit).
10. **Cadru normativ obligatoriu** (per Part II §6):
    - Codul muncii nr. 154/2003.
    - Legea 133/2011 privind protecția datelor cu caracter personal (Moldova GDPR-equivalent).
    - Politica de protecție a datelor cu caracter personal în cadrul CNAS.
    - Legea 186/2008 privind securitatea și sănătatea în muncă.
11. **Penalty regime (financial impact):**
    - Late delivery: 0,1%/zi din valoare livrabil neexecutat, max 5% din contract total.
    - ≥15 zile întârziere = justificare scrisă obligatorie (Prestator); Beneficiar poate accepta prelungire (cu extinderea garanției bună execuție) sau considera refuz execuție (→ reținere garanție).
    - Late payment (Beneficiar): aceeași 0,1%/zi, max 5%.
12. **Security agreement (Anexa 5) requires:**
    - 8-character minimum passwords with mixed registers, monthly rotation.
    - User onboarding requires NPP + IDNP + funcție + resurse + domeniu.
    - User changes notified within 2 working days.
    - Administrator changes notified within 2 days.
    - "Ecran curat" policy enforced.
    - Incidents reported immediately.
13. **Confidentiality applies to all personnel** — each must sign Anexa 6 declaration; binding indefinitely (including post-resignation).
14. **DUAE (Documentul Unic de Achiziții European)** is the qualification mechanism (Ordin MF 72/2020).
15. **Beneficiari efectivi (UBO) declaration** (Ordin MF 145/2020) required upon designation as winner.
16. **Contestation route:** Agenția Națională pentru Soluționarea Contestațiilor (ANSC), mun. Chișinău, bd. Ștefan cel Mare 124 (et.4), MD-2001; tel 022-820 652 / 022 820-651; email contestatii@ansc.md. Termen: 5 zile (sau 10 zile) de la circumstanțele temei.
17. **WTO GPA applicability:** procedura intră sub incidența Acordului OMC privind achizițiile guvernamentale → trans-border bidders eligible without discrimination.

---

## Cross-references & ambiguities

- **Registry numbering discrepancy:** Annex 3.6-G text references "Structura Registrului documentelor executorii este prezentată în Anexă (secțiunea **1.3.8**)" but the actual structure is under **8.3.8** in the TOR. Use 8.3.8.
- **Decision-flow status "Spre plată" vs "Spre achitare":** the 3.7-B01 step extracts entries with "Spre plată", while the rest of the document uses "Spre achitare". Treat as same logical status; reconcile during design.
- **Annex 3.6 "atestare stagiu de cotizare", "eliberare certificate", "restituire sume", "dosare arhivate":** these specific adjacent services mentioned in the user's prompt do **NOT** appear in the TOR text within lines 16518–20771. They may be referenced in the TOR's earlier sections or are part of the "Servicii proactive" / other annexes. Recommend cross-checking Part A research files. Within the requested range, 3.6 services are A/B/C (șomaj), D (sportivi viageră), E (ajutor social), F (compensație energie), G (titluri executorii) only.
- **Annex 7 application-form templates:** content is image-only (PDF page renders). To extract textual fields of each cerere/decizie template, a separate visual review of `images/pdf_p306_*.png` through `pdf_p316_*.png` is required.
- **Annex 6 reports:** the list is described as "preliminară" — likely to be extended during business analysis.
- **Annex 4/5 web services:** lists may be adjusted via technical annexes signed with AGE (Agenția de Guvernare Electronică) during system development.
