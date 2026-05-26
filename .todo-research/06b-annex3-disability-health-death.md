# Annex 3.3 / 3.4 / 3.5 — Dizabilitate, Sănătate, Deces (Disability, Health, Death)

Source: `tor/TOR.md` lines 14159-16518 (PDF pages 225-272).

This annex covers three life-event groupings under the unified SI PS:
- **3.3 Dizabilitate** — disability pensions, allowances, indemnities and material aids (16 sub-services: A–P)
- **3.4 Sănătate** — temporary work incapacity indemnity and capitalized periodic payments (3 sub-services: A–C)
- **3.5 Deces** — survivor pensions, death indemnities and aids (17 sub-services: A–Q)

## Common cross-cutting patterns (apply to all services in this annex)

These business rules are repeated verbatim in every service description. They are documented here once and not re-listed per service unless the service deviates:

- For each insured person an electronic dossier (`dosar electronic`) is created in the system.
- The system journals every event in the process (start date/time, success records, error records, completion date/time).
- For each request type, templates are attached: cerere (application), document rezultativ (fișa de calcul / decizie / contract), and recipisă (receipt).
- For each request type, the list of obligatory documents to attach is configured (does NOT apply to proactive services).
- For each request type, the waiting period for missing documents is configured (does NOT apply to proactive services).
- Social prestations are awarded either as a fixed amount or calculated based on multiple factors: paid contributions, length of service (vechime în muncă), etc.
- Calculated sum is divided per expense article (clasificator de cheltuieli prezentat de CNAS).
- Documents are generated in PDF and signed with electronic signature.
- Registers referenced: Registrul persoanelor asigurate (§8.2.4), Registrul deciziilor (§8.3.9), Registrul de conturi de plată a prestațiilor (§8.3.10).
- If the beneficiary already has an active decision for the same type of prestation, the prior decision is terminated with the date of the new decision (status → "terminată").
- Online submissions use beneficiary's electronic signature; in-person submissions: user CNAS registers the cerere, beneficiary signs olograph, scanned attachment uploaded, recipisă printed.
- Missing-document waiting period = 30 days (explicitly stated for 3.3-A and 3.5-A).
- Standard process variants:
  - **Proactive variant** (no application, no document attachments, no recipisă, no waiting period) — daily scheduled job pulls a new-decisions list from an upstream IS; otherwise identical actor flow.
  - **At-request variant** (application + obligatory docs + recipisă + 30-day waiting + optional document-completion step).
  - **International-agreements variant** — adds an extra approval level (`Șeful direcției`) between Utilizator CNAS and Șeful CNAS (3 signatures total instead of 2).

---

## 3.3 Procese de lucru aferente evenimentului "Dizabilitate"

### Legal framework (Cadrul normativ) for 3.3
- Legea nr.156/1998 privind sistemul public de pensii
- Legea nr.290/2016 (modificări)
- Legea nr.1544/1993 (pensii militari și corp de comandă)
- Legea nr.909/1992 (Cernobîl)
- Legea nr.499/1999 (alocații sociale de stat)
- Legea nr.121/2001 (protecție socială suplimentară)
- Legea nr.756/1997 (accidente de muncă și boli profesionale)
- HG 929/2006 (modul de plată a pensiilor / alocațiilor / indemnizațiilor / plăți periodice capitalizate / suport financiar de stat)
- HG 165/2017 (modalitatea de calculare a pensiilor și de confirmare a stagiului de cotizare)
- HG 78/1994 (vechimea în muncă, militari, MAI, CNA, sistem penitenciar)
- HG 1101/2001 (Regulamentul prestație de dizabilitate accidente de muncă / boli profesionale)
- HG 374/1999 (compensație unică Cernobîl)
- HG 470/2006 (alocații lunare de stat)
- HG 575/1992 (înlesniri colaboratori subdiviziuni risc deosebit, boală actinică)
- Bilateral social-security agreements with: Bulgaria (2008/2009), Portugalia (2009/2010), România (2010/2011), Belarus (2019/2020), Luxembourg (2010/2012), Estonia (2011/2012), Cehia (2011/2012), Austria (2011/2012), Polonia (2013/2014), Belgia (2012/2016), Ungaria (2013/2014), Lituania (2014/2015), Germania (2017/2019), Turcia (2017/2020), Grecia (2021/2023), Italia (2021/2023), Spania (2022/2024), Letonia (2023/2024).

---

### Service: 3.3-A — Pensie de dizabilitate — Disability pension

**Service code (if any):** 3.3-A (process steps coded `3.3.1-A01..A04` for proactive, `3.3.2-A01..A04` for at-request)
**Eligibility rules:** Granted in 2 modes: (a) proactive — based on info pulled from SI "Determinarea dizabilității și capacității de muncă" (SIDDCM via MConnect); (b) at-request — based on cerere (electronic or in-person at CNAS). Requires insured-person status under Legea 156/1998.
**Required documents / data:** Cerere semnată; obligatory documents per classificator (only for at-request mode); data from RSP about the beneficiary; SIDDCM decision.
**External systems consulted:**
- MConnect (gov interoperability platform)
- SIDDCM — SI "Determinarea dizabilității și capacității de muncă"
- RSP — Registrul de stat al populației (beneficiary data)
**Calculation / amount rules:** Calculated based on contributions, vechime în muncă, and other factors; sum divided per expense article per CNAS classifier; fișa de calcul pre-filled by system, may be supplemented by user with auto-recalculation. Comparison rule: if new calculated sum is smaller than the prior decision's sum, the Utilizator CNAS may emit a refusal decision with documented reason.
**Process steps (numbered):**

*Proactive variant:*
1. `3.3.1-A01` Sistemul — launches procedure for selecting new decisions; extracts list from SIDDCM. If empty → end. Result: Lista deciziilor noi.
2. `3.3.1-A02` Sistemul — processes each record from list; applies validation and beneficiary identification rules; invalid records are ignored and logged. Generates Cererea privind acordarea prestației, assigns to territorial CTAS by beneficiary residence; generates notifications to Utilizator CNAS. Result: list processed, cereri generate, notifications sent.
3. `3.3.1-A03` Utilizator CNAS — examines cerere; system generates fișa de calcul pre-filled with beneficiary data, eligible periods and computed sum; user may complete missing fields (auto-recalc). System generates Decizia de acordare (based on fișa de calcul) or Decizia de refuz (with reason); upon refusal, user may additionally emit a different prestație type. Documents in PDF, signed electronically, forwarded to Șeful CNAS. If returned for re-examination, user re-edits fișa and re-issues decision.
4. `3.3.1-A04` Șeful CNAS — examines decision and cerere; if OK → electronic signature, finalize. If not OK → return to step `3.3.1-A03`. If beneficiary has an active decision of the same type, previous one is terminated with the new decision's date.

*At-request variant:*
1. `3.3.2-A01` Beneficiarul / Utilizator CNAS — Depunerea cererii: beneficiar authenticates, selects pension, completes cerere, attaches obligatory docs, signs (in-person: olograph signature on printed copy, scanned and attached). Recipisă generated.
2. `3.3.2-A02` Utilizator CNAS — Examinarea cererii și deciziei: signs recipisă; if docs missing → cerere held 30 days for completion; on expiry, notification triggered. System validates per type rules; generates fișa de calcul (pre-filled); user may complete; system warns if another active prestație of same/other type exists for beneficiary; if new computed sum is lower than prior decision → refusal allowed. Decizia (PDF) signed electronically, forwarded to Șeful CTAS.
3. `3.3.2-A03` Beneficiarul / Utilizator CNAS — Completarea cererii cu documentele lipsă: attaches missing documents within the 30-day window; system journalizes the event.
4. `3.3.2-A04` Șeful CNAS — examines decision; if OK → signs, finalizes; if not OK → return to `3.3.2-A02`. If new Decizia differs from prior (e.g., sum increased or new type) → in Registrul deciziilor the prior decision's period is updated to end on the new decision date and status changed to "terminată".

**Decision points:**
- Proactive: validation passes / fails per record (ignore invalid); Șeful CNAS approves / returns for re-examination.
- At-request: documents complete? (yes/no → 30-day wait); existing prior decision? (warn user); new sum < prior sum? (allow refusal); Șeful CNAS approval gate.

**Status transitions:**
- Cerere: înregistrată → în așteptare documente (optional) → în examinare → semnată Utilizator → aprobată Șef CNAS / returnată.
- Decizie: acordare | refuz; prior active decision → terminată (with end-date = new decision date).

**Diagram present:**
- Figura A3.3.1 — `images/pdf_p233_full.png` (proactive flow)
- Figura A3.3.2 — `images/pdf_p237_full.png` (at-request flow)

**Output document / decision template:** Decizia privind acordarea pensiei de dizabilitate (PDF, electronically signed) OR Decizia privind refuzul acordării (PDF with motiv); Fișa de calcul; Recipisă (at-request only); Notificare.
**Notifications:** to Beneficiar, Utilizator CNAS, Șeful CNAS — on cerere generated, decision signed, returned for re-examination, document waiting-period expired.
**Payment integration:** Decizia stocată în Registrul deciziilor → calculus per expense article → triggers Registrul de conturi de plată a prestațiilor; payment per HG 929/2006.
**Edge cases / exceptions:**
- Invalid SIDDCM record → log and skip.
- Missing obligatory documents → 30-day hold; expiry triggers notification.
- Prior active decision of same type → previous one terminated with new decision date.
- New calculated sum < prior sum → user may emit Decizia de refuz with reason.
- If refusal emitted → user can additionally emit a Decizie for a DIFFERENT prestație type with own fișa de calcul.

---

### Service: 3.3-B — Pensie de dizabilitate militarilor și persoanelor din corpul de comandă și trupele organelor afacerilor interne — Disability pension for military personnel and command-corps/MAI personnel
Process identical to 3.3-A (per §8.3.3.2). Distinct legal basis: Legea 1544/1993, HG 78/1994. Beneficiary category: military and MAI command-corps personnel.

---

### Service: 3.3-C — Pensie de dizabilitate militarilor în termen — Disability pension for conscript soldiers
Process identical to 3.3-A. Beneficiary category: conscript soldiers (`militari în termen`).

---

### Service: 3.3-D — Pensie de dizabilitate cetățenilor care au participat la lichidarea urmărilor avariei la C.A.E. Cernobîl — Disability pension for citizens who participated in the liquidation of the Chernobyl NPP accident
Process identical to 3.3-A. Legal basis includes Legea 909/1992. Beneficiary category: Chernobyl liquidators with disability.

---

### Service: 3.3-E — Pensie de dizabilitate acordată/reexaminată în baza prevederilor acordurilor internaționale — Disability pension granted/re-examined under international agreements
**Service code:** 3.3-E (steps `3.3-E01..E04`)
**Eligibility rules:** Beneficiary covered under a bilateral social-security agreement. At-request only.
**Required documents / data:** Cerere; obligatory documents per type; agreement-specific liaison forms.
**External systems consulted:** SI PS internal; no MConnect automation in the documented at-request flow.
**Calculation / amount rules:** Per applicable agreement provisions and pension calculation regulation (HG 165/2017); fișa de calcul, divided by expense article.
**Process steps (numbered):**
1. `3.3-E01` Beneficiarul / Utilizator CNAS — Depunerea cererii (same as standard at-request).
2. `3.3-E02` Utilizator CNAS — Examinarea cererii; signs recipisă, generates fișa de calcul; signs Decizia electronically; sends to Șeful direcției.
3. `3.3-E03` Șeful direcției — examines; if OK → signs and forwards to Șeful CNAS; if not OK → returns to `3.3-E02`.
4. `3.3-E04` Șeful CNAS — examines; if OK → signs and finalizes; if not OK → returns to `3.3-E02` with notifications to Șeful direcției and Utilizator CNAS.

**Decision points:** Three approval gates (Utilizator → Șeful direcției → Șeful CNAS); return-for-re-examination at each gate.
**Status transitions:** Cerere → în examinare → semnată Utilizator → aprobată Șef direcție → aprobată Șef CNAS | returnată la oricare nivel.
**Diagram present:** Figura E3.3 — `images/pdf_p241_full.png`
**Output document / decision template:** Decizia privind acordarea prestației (sau de refuz); Recipisă; Notificare.
**Notifications:** at each transition, plus when Șeful CNAS returns to Utilizator (notifies both Șeful direcției și Utilizator CNAS).
**Payment integration:** standard via Registrul deciziilor → Registrul de conturi de plată.
**Edge cases / exceptions:** Two-tier review reflects agreement-related complexity; no waiting-for-documents period explicitly described for this variant.

---

### Service: 3.3-F — Pensie și indemnizație de dizabilitate în urma accidentului de muncă sau a unei boli profesionale acordată în baza prevederilor acordurilor internaționale — Disability pension/indemnity from work accident or occupational disease under international agreements
Process identical to 3.3-E (per §8.3.3.6). Legal basis adds Legea 756/1997, HG 1101/2001.

---

### Service: 3.3-G — Reexaminarea pensiilor de dizabilitate — Re-examination of disability pensions
Process identical to 3.3-A (per §8.3.3.7). Use case: triggered when SIDDCM emits a re-examination decision (severity grade change, recovery, etc.) — flows through proactive variant.

---

### Service: 3.3-H — Alocație socială de stat pentru persoanele cu dizabilități severe, accentuate și medii, persoanelor cu dizabilități din copilărie severe, accentuate și medie — State social allowance for persons with severe/accentuated/medium disability (incl. since childhood)
Process identical to 3.3-A (per §8.3.3.8). **Special note:** "Se acordă la cererea pentru pensii, în cazul deciziei de refuz" — granted from the same cerere submitted for pension, when the pension decision is refusal. So this is auto-routed from a refused pension cerere. Legal basis: Legea 499/1999.

---

### Service: 3.3-I — Alocație socială de stat pentru copii în vârstă de până la 18 ani cu dizabilitate severă, accentuată și medie — State social allowance for children under 18 with severe/accentuated/medium disability
Process identical to 3.3-A (per §8.3.3.9). Beneficiary: child < 18 with disability; cerere via legal representative. Legal basis: Legea 499/1999.

---

### Service: 3.3-J — Alocație lunară pentru îngrijirea, însoțirea și supravegherea persoanelor cu dizabilități severe imobilizate la pat, care au avut de suferit de pe urma catastrofei de la C.A.E Cernobîl — Monthly allowance for care/accompanying/supervision of bedridden severely disabled persons who suffered from Chernobyl
Process identical to 3.3-A (per §8.3.3.10). Legal basis: Legea 909/1992, HG 470/2006. Caregiver allowance.

---

### Service: 3.3-K — Indemnizație de dizabilitate ca urmare a unui accident de muncă sau a unei boli profesionale — Disability indemnity from work accident or occupational disease
Process identical to 3.3-A (per §8.3.3.11). Legal basis: Legea 756/1997, HG 1101/2001. **Referenced from 3.4-C** for `Calcularea plăților periodice capitalizate`.

---

### Service: 3.3-L — Compensație unică pentru prejudiciul adus sănătății persoanelor cu dizabilități din rândul participanților la lichidarea consecințelor catastrofei de la Cernobîl și la experiențele nucleare, avariilor cu radiație ionizată și a consecințelor lor la obiectivele atomice civile sau militare — Single compensation for health damage to disabled liquidators of Chernobyl/nuclear-experiment/ionizing-radiation accidents
Process identical to 3.3-A (per §8.3.3.12). Legal basis: HG 374/1999. One-time payment (compensație unică).

---

### Service: 3.3-M — Ajutor material anual pentru persoanele cu dizabilități de pe urma participării la acțiunile de luptă din Afganistan și pentru membrii familiilor participanților căzuți la datorie în acțiunile de luptă din Afganistan (soți și unul dintre părinți) — Annual material aid for disabled Afghan-war veterans and family members (spouses + one parent) of fallen participants
Process identical to 3.3-A (per §8.3.3.13). Legal basis: Legea 121/2001, HG 470/2006. Annual fixed amount.

---

### Service: 3.3-N — Ajutor material anual pentru persoanele cu dizabilități de pe urma acțiunilor de luptă pentru apărarea integrității teritoriale și a independenței Republicii Moldova și pentru membrii familiilor participanților căzuți la datorie ... (soți și unul dintre părinți) — Annual material aid for disabled veterans of the Transnistrian conflict and family members (spouses + one parent) of fallen participants
Process identical to 3.3-A (per §8.3.3.14). Legal basis: Legea 121/2001. Annual fixed amount.

---

### Service: 3.3-O — Ajutor material anual pentru persoanele cu dizabilități a căror dizabilitate este cauzată de participarea la lichidarea consecințelor avariei de la C.A.E. Cernobîl, pentru persoanele care s-au îmbolnăvit și au suferit de boală actinică sau au devenit cu dizabilități în urma experiențelor nucleare, avariilor cu radiație ionizată și a consecințelor acestora la obiectivele atomice civile sau militare în timpul îndeplinirii serviciului militar ori special și pentru membrii familiilor participanților la lichidarea consecințelor avariei de la C.A.E. Cernobîl decedați (soți și unul dintre părinți) — Annual material aid for Chernobyl-liquidator disability beneficiaries, actinic-disease sufferers, nuclear-experiment victims, and family members of deceased Chernobyl participants (spouses + one parent)
Process identical to 3.3-A (per §8.3.3.15). Legal basis: Legea 909/1992, HG 575/1992.

---

### Service: 3.3-P — Ajutor material anual pentru participanții la Cel de-al Doilea Război Mondial din rândul categoriilor specificate la art.7 alin.(2) pct.1) lit.a)–e) din Legea nr.190/2003 cu privire la veterani, pentru persoanele cu dizabilități de pe urma Celui de-al Doilea Război Mondial din rândul categoriilor specificate la art.8 alin.(2) lit.a) din legea indicată, precum și pentru persoanele care au fost încadrate în grad de dizabilitate în urma rănirii, contuziei, schilodirii, fiind antrenate de autoritățile administrației publice locale la strângerea munițiilor și a tehnicii militare, la deminarea teritoriului și a obiectelor în anii Celui de-al Doilea Război Mondial — Annual material aid for WWII veterans and disabled WWII survivors, plus persons disabled while collecting munitions/demining during WWII
Process identical to 3.3-A (per §8.3.3.16). Legal basis: Legea 190/2003 (veterans), Legea 121/2001.

---

## 3.4 Procese de lucru aferente evenimentului "Sănătate"

### Legal framework (Cadrul normativ) for 3.4
- Legea nr.289/2004 (indemnizații incapacitate temporară de muncă și alte prestații)
- Legea nr.756/1999 (accidente de muncă și boli profesionale)
- Legea nr.290/2016
- Legea nr.123/1998 (capitalizarea plăților periodice)
- Legea nr.278/1999 (recalcularea sumei de compensare a pagubei cauzate angajaților din mutilare/vătămări sănătate)
- HG 108/2005 (condiții stabilire / calcul / plată indemnizații incapacitate temporară de muncă)
- HG 127/2000 (calcul plăți periodice capitalizate)
- HG 341/2002 (achitarea plăților periodice capitalizate din bugetul de stat în procesul de lichidare a întreprinderilor fără succesiune de drept)
- Bilateral agreements: Bulgaria, Portugalia, România, Turcia.

---

### Service: 3.4-A — Indemnizație pentru incapacitate temporară de muncă — Indemnity for temporary work incapacity (CCMI / sick leave benefit)

**Service code (if any):** 3.4-A (steps `3.4.1-A01..A04` proactive, `3.4.2-A01..A03` at-request)
**Eligibility rules:** Two modes: (a) proactive — based on info pulled from Portalul certificatelor de concediu medical (CCMI portal) via MConnect; (b) at-request. Beneficiary must hold a valid CCMI (buletin medical / certificat de concediu medical) and be an insured person.
**Required documents / data:**
- Proactive: only CCMI data + RSP beneficiary data.
- At-request: Cerere + obligatory documents per classificator + CCMI (verified via portal).
**External systems consulted:**
- MConnect
- Portalul certificatelor de concediu medical (CCMI portal — emits buletine medicale)
- RSP
**Calculation / amount rules:** Per HG 108/2005 — based on insured income, length of CCMI, contributions, etc.; fișa de calcul pre-filled, divided per expense article; user may complete with auto-recalc.
**Process steps (numbered):**

*Proactive variant:*
1. `3.4.1-A01` Sistemul — launches procedure to select new buletine medicale from CCMI portal via MConnect. If empty → end.
2. `3.4.1-A02` Sistemul — processes each record, validates and identifies beneficiary; generates Cererea and assigns to territorial CTAS by viza de reședință; generates notifications.
3. `3.4.1-A03` Utilizator CNAS — examines cerere; system generates fișa de calcul; user may complete; system generates Decizia (PDF), signed electronically, forwarded to Șeful CNAS.
4. `3.4.1-A04` Șeful CNAS — examines; if OK → signs and finalizes; if not → returns to `3.4.1-A03`.

*At-request variant:*
1. `3.4.2-A01` Beneficiarul / Utilizator CNAS — Depunerea cererii (standard pattern + recipisă).
2. `3.4.2-A02` Utilizator CNAS — Examinarea cererii și deciziei; generates fișa de calcul; signs Decizia; forwards to Șeful CNAS.
3. `3.4.2-A03` Șeful CNAS — examines; signs / returns.

**Decision points:** record validation; Șeful CNAS approval gate.
**Status transitions:** Cerere → examined → Decizie semnată Utilizator → aprobată / returnată.
**Diagram present:**
- Figura A3.4.1 — `images/pdf_p246_full.png` (proactive)
- Figura A3.4.2 — `images/pdf_p249_full.png` (at-request)

**Output document / decision template:** Decizia privind acordarea indemnizației pentru incapacitate temporară de muncă (PDF) OR Decizia de refuz; Fișa de calcul; Recipisă (at-request); Notificare.
**Notifications:** to Beneficiar, Utilizator CNAS, Șeful CNAS on each transition.
**Payment integration:** Per HG 108/2005, HG 929/2006; stored in Registrul deciziilor; payment via Registrul de conturi de plată.
**Edge cases / exceptions:**
- Invalid buletin medical record → log + skip.
- The at-request variant does NOT include an explicit document-completion step (unlike 3.3-A); no `A03` waiting-period step described.

---

### Service: 3.4-B — Indemnizație pentru incapacitate temporară de muncă și de maternitate acordată în baza prevederilor acordurilor internaționale în domeniul securității sociale la care Republica Moldova este parte — Temporary work-incapacity and maternity indemnity under international agreements

**Service code (if any):** 3.4-B (steps `3.4-B01..B04`)
**Eligibility rules:** Beneficiary covered under bilateral agreement; both work-incapacity AND maternity scenarios included. At-request only.
**Required documents / data:** Cerere + obligatory documents per classificator; agreement-specific evidence.
**External systems consulted:** SI PS internal (no MConnect automation described for this variant).
**Calculation / amount rules:** Per applicable agreement and HG 108/2005; fișa de calcul.
**Process steps (numbered):**
1. `3.4-B01` Beneficiarul / Utilizator CNAS — Depunerea cererii (standard at-request).
2. `3.4-B02` Utilizator CNAS — Examinarea cererii; signs Decizia; forwards to Șeful direcției.
3. `3.4-B03` Șeful direcției — examines; if OK → signs and forwards to Șeful CNAS; if not → returns to `3.4-B02`.
4. `3.4-B04` Șeful CNAS — examines; if OK → signs and finalizes; if not → returns to `3.4-B02` notifying both Șeful direcției and Utilizator CNAS.

**Decision points:** Three approval gates (international-agreements variant).
**Status transitions:** standard 3-level approval.
**Diagram present:** Figura B3.4 — `images/pdf_p252_full.png`
**Output document / decision template:** Decizia privind acordarea / refuz; Recipisă; Notificare.
**Notifications:** on each transition; on return-to-Utilizator both Șeful direcției and Utilizator are notified.
**Payment integration:** standard.
**Edge cases / exceptions:** Combines two distinct benefit types (incapacity + maternity) under the international-agreements regime.

---

### Service: 3.4-C — Calcularea plăților periodice capitalizate — Calculation of capitalized periodic payments
Process identical to 3.3-K (Indemnizație de dizabilitate accident de muncă / boală profesională) per §8.3.4.3. Legal basis: Legea 123/1998, HG 127/2000, HG 341/2002. Used in the case of enterprise liquidation without legal successor — payments capitalized and paid from state budget.

---

## 3.5 Procese de lucru aferente evenimentului "Deces"

### Legal framework (Cadrul normativ) for 3.5
- Legea nr.156/1998 (sistemul public de pensii)
- Legea 290/2016
- Legea nr.1544/1993 (pensii militari și MAI)
- Legea nr.909/1992 (Cernobîl)
- Legea nr.317/2024 (veterani de război) — *referenced twice in source*
- Legea nr.499/1999 (alocații sociale de stat)
- Legea nr.121/2001 (protecție socială suplimentară)
- Legea nr.127/2020 (indemnizație urmași personal medical decedat COVID-19)
- Legea nr.289/2004 (indemnizații incapacitate)
- Legea anuală a bugetului asigurărilor sociale de stat
- HG 929/2006 (modul de plată)
- HG 165/2017 (calcularea pensiilor)
- HG 78/1994 (vechimea militari/MAI/CNA/penitenciar)
- HG 712/2019 (indemnizație în cazul decesului unuia dintre soți)
- HG 637/2020 (indemnizație urmași personal medical COVID-19)
- HG 443/2014 (compensație unică familii Cernobîl)
- HG 1442/2006 (ajutor de deces)
- HG 470/2006 (alocații lunare de stat)
- Bilateral social-security agreements: Bulgaria, Portugalia, Belarus, România, Luxembourg, Estonia, Cehia, Austria, Polonia, Belgia, Ungaria, Lituania, Germania, Turcia, Grecia, Italia, Spania, Letonia (same set as 3.3).

---

### Service: 3.5-A — Pensie de urmaș — Survivor's pension

**Service code (if any):** 3.5-A (steps `3.5-A01..A04`)
**Eligibility rules:** Survivors / authorized representative of the deceased insured person. At-request only (no proactive variant described).
**Required documents / data:** Cerere; obligatory documents per classificator (death certificate, relationship documents, etc. implied).
**External systems consulted:** SI PS internal; RSP for beneficiary/deceased data; reference to standard registers (§8.2.4, §8.3.9, §8.3.10).
**Calculation / amount rules:** Per Legea 156/1998 and HG 165/2017; based on contributions, vechime în muncă; fișa de calcul pre-filled, divided per expense article.
**Process steps (numbered):**
1. `3.5-A01` Beneficiarul (urmași / persoana împuternicită / decedatul-referință) / Utilizator CNAS — Depunerea cererii: authentication, completion, doc upload, signature (electronic online; olograph in-person → scan); recipisă generated.
2. `3.5-A02` Utilizator CNAS — Examinarea cererii și deciziei: signs recipisă; if docs missing → cerere held 30 days; on expiry, Specialistul CTAS notified. System validates; generates fișa de calcul; user may complete; system warns about prior pensions of same/other type for beneficiary; if new sum < prior → refusal allowed. Decizia (PDF) signed, forwarded to Șeful CTAS.
3. `3.5-A03` Beneficiarul / Utilizator CNAS — Completarea cererii cu documentele lipsă: attaches missing docs within the wait window; system journalizes.
4. `3.5-A04` Șeful CNAS — examines; if OK → signs and finalizes; if not → returns to `3.5-A02`.

**Decision points:** Documents complete? Prior decision exists? New sum < prior? Approval gate.
**Status transitions:** standard cerere → în așteptare documente → examinare → Decizie semnată Utilizator → aprobată Șef CNAS / returnată.
**Diagram present:** Figura A3.5 — `images/pdf_p258_full.png`
**Output document / decision template:** Decizia privind acordarea pensiei de urmaș (PDF) sau Decizia de refuz; Fișa de calcul; Recipisă; Notificare.
**Notifications:** on cerere generated, decision signed, returned, doc waiting-period expired (to Specialistul CTAS specifically).
**Payment integration:** standard (Registrul deciziilor → Registrul de conturi de plată; HG 929/2006).
**Edge cases / exceptions:**
- Beneficiary may be: surviving relative ("urmași"), authorized representative ("persoana împuternicită"), or referenced via the deceased ("decedatul" — i.e., the deceased's record used as identifier).
- 30-day document-completion window.
- Prior active decision of same type → terminated with new date.
- If new sum < prior sum → refusal option.

---

### Service: 3.5-B — Pensie în cazul pierderii întreținătorului militarilor, persoanelor din corpul de comandă și din trupele organelor afacerilor interne — Survivor's pension for losing a breadwinner (military / command-corps / MAI personnel)
Process identical to 3.5-A (per §8.3.5.2). Legal basis: Legea 1544/1993, HG 78/1994.

---

### Service: 3.5-C — Pensie în cazul pierderii întreținătorului militar în termen — Survivor's pension when breadwinner was a conscript soldier
Process identical to 3.5-A (per §8.3.5.3). Beneficiary: family of fallen conscript.

---

### Service: 3.5-D — Pensie în cazul pierderii întreținătorului membrilor familiilor persoanelor care au decedat în urma schilodirii sau îmbolnăvirii provocate de avaria de la C.A.E. Cernobîl — Survivor's pension for family members of persons who died from Chernobyl-related disability/illness
Process identical to 3.5-A (per §8.3.5.4). Legal basis: Legea 909/1992.

---

### Service: 3.5-E — Pensie de urmaș în baza prevederilor acordurilor internaționale — Survivor's pension under international agreements

**Service code (if any):** 3.5-E (steps `3.5-E01..E04`)
**Eligibility rules:** Survivors of beneficiaries covered by bilateral agreement.
**Required documents / data:** Cerere + obligatory documents per classificator.
**External systems consulted:** SI PS internal; coordination via foreign social-security institution per agreement.
**Calculation / amount rules:** Per applicable agreement.
**Process steps (numbered):**
1. `3.5-E01` Beneficiarul / Utilizator CNAS — Depunerea cererii.
2. `3.5-E02` Utilizator CNAS — Examinarea cererii; fișa de calcul; Decizia (PDF) signed and forwarded to Șeful direcției.
3. `3.5-E03` Șeful direcției — examines; signs and forwards to Șeful CNAS, or returns to `3.5-E02`.
4. `3.5-E04` Șeful CNAS — examines; signs and finalizes, or returns to `3.5-E02` notifying both Șeful direcției and Utilizator CNAS.

**Decision points:** Three approval gates.
**Status transitions:** 3-level approval flow.
**Diagram present:** Figura E3.5 — `images/pdf_p262_full.png`
**Output document / decision template:** Decizia privind acordarea prestației / de refuz; Recipisă; Notificare.
**Notifications:** at each transition; both intermediate and final approvers notified on returns.
**Payment integration:** standard.
**Edge cases / exceptions:** No explicit document-completion window in this variant; agreement-related conformance checks.

---

### Service: 3.5-F — Alocație socială de stat pentru copii și persoanele cu dizabilități severe din copilărie care au pierdut întreținătorul — State social allowance for children and severely disabled-since-childhood persons who lost the breadwinner
Process identical to 3.5-A (per §8.3.5.6). Legal basis: Legea 499/1999. Beneficiary: minor / disabled-since-childhood survivor.

---

### Service: 3.5-G — Indemnizație de deces ca urmare a unui accident de muncă sau a unei boli profesionale — Death indemnity due to work accident or occupational disease
Process identical to 3.5-A (per §8.3.5.7). Legal basis: Legea 756/1999, HG 1101/2001 implied.

---

### Service: 3.5-H — Indemnizație unică de deces beneficiarilor instituțiilor de forță — Single death indemnity for force-institution beneficiaries (military / MAI)
Process identical to 3.5-A (per §8.3.5.8). Legal basis: Legea 1544/1993. One-time payment.

---

### Service: 3.5-I — Indemnizație în cazul decesului unuia dintre soți — Indemnity in case of spouse death
Process identical to 3.5-A (per §8.3.5.9). Legal basis: HG 712/2019. Beneficiary: surviving spouse.

---

### Service: 3.5-J — Indemnizație urmașilor personalului medical decedat ca urmare a desfășurării activității medicale în lupta cu COVID-19 — Indemnity for survivors of medical staff who died fighting COVID-19
Process identical to 3.5-A (per §8.3.5.10). Legal basis: Legea 127/2020, HG 637/2020. Beneficiary: survivors of healthcare workers who died from COVID-19-related medical duty.

---

### Service: 3.5-K — Compensația unică familiilor ce și-au pierdut întreținătorul în urma catastrofei de la C.A.E Cernobîl — Single compensation for families that lost the breadwinner due to Chernobyl
Process identical to 3.5-A (per §8.3.5.11). Legal basis: Legea 909/1992, HG 443/2014.

---

### Service: 3.5-L — Ajutor de deces persoanelor asigurate — Death aid (funeral benefit) for insured persons
Process identical to 3.5-A (per §8.3.5.12). Legal basis: HG 1442/2006. Beneficiary: person paying funeral expenses for insured deceased.

---

### Service: 3.5-M — Ajutor de deces persoanelor neasigurate — Death aid (funeral benefit) for uninsured persons
Process identical to 3.5-A (per §8.3.5.13). Legal basis: HG 1442/2006. Beneficiary: person paying funeral expenses for uninsured deceased; eligibility checks differ (citizenship / residency criteria).

---

### Service: 3.5-N — Ajutor de deces în baza prevederilor acordurilor internaționale — Death aid under international agreements
Process identical to 3.5-E (per §8.3.5.14) — international-agreements variant with 3-level approval (Utilizator CNAS → Șeful direcției → Șeful CNAS).

---

### Service: 3.5-O — Ajutor de deces acordat beneficiarilor instituțiilor de forță — Death aid for force-institution beneficiaries
Process identical to 3.5-A (per §8.3.5.15). Legal basis: Legea 1544/1993, HG 1442/2006.

---

### Service: 3.5-P — Ajutor material unic anual copiilor care au pierdut întreținătorul (de pe urma catastrofei de la C.A.E Cernobîl) — Annual single material aid for children who lost the breadwinner due to Chernobyl
Process identical to 3.5-A (per §8.3.5.16). Legal basis: Legea 909/1992, HG 470/2006.

---

### Service: 3.5-Q — Ajutor material anual pentru soții supraviețuitori ai participanților la Cel de-al Doilea Război Mondial căzuți la datorie sau ai persoanelor cu dizabilități de pe urma celui de-al Doilea Război Mondial decedate — Annual material aid for surviving spouses of WWII participants who fell in duty or of deceased disabled WWII survivors
Process identical to 3.5-A (per §8.3.5.17). Legal basis: Legea 317/2024 (veterani de război), Legea 121/2001.

---

## Implementation implications for SI PS (consolidated)

1. **One generic "Decision processing" engine** drives all three event groups. Differences are configuration-driven:
   - Periodicity: `proactive` vs `at-request`.
   - Source IS: SIDDCM (disability), CCMI Portal (health), ANOFM/SIAÎSȘ (unemployment, out of scope here), survivor cereri only (death).
   - Approval depth: 2-level (standard) or 3-level (international agreements — Utilizator → Șeful direcției → Șeful CNAS).
   - Document-completion window: 30 days (only on certain at-request services: 3.3-A and 3.5-A explicitly).
   - Prior-decision comparison: warn user + allow refusal when new sum < prior (3.3-A, 3.5-A).
   - Auto-fallback: refused pensie → alocație socială (3.3-H rule).

2. **Required external integrations (via MConnect)**:
   - SIDDCM (disability determinations) — daily pull for 3.3-A (and family).
   - Portalul certificatelor de concediu medical (CCMI portal) — daily pull for 3.4-A.
   - RSP — beneficiary identification across all services.
   - (Out of scope of this chunk but mentioned: SIAÎSȘ for 3.6 unemployment.)
   - Foreign social-security institutions per bilateral agreement (manual liaison, not automated).

3. **Configuration per service-type required**:
   - Cerere template (PDF).
   - Document rezultativ template (fișa de calcul, decizie, contract).
   - Recipisă template.
   - List of obligatory documents.
   - Waiting period for missing documents.
   - Expense-article classifier (clasificator de cheltuieli).
   - Calculation rules (sum fix vs. formula based on contributions/vechime).
   - Approval depth (2 vs 3 levels).
   - Periodicity (proactive cron vs at-request trigger).

4. **Registers touched**: Registrul persoanelor asigurate (§8.2.4), Registrul deciziilor (§8.3.9), Registrul de conturi de plată a prestațiilor (§8.3.10), plus per-process journal/audit logs.

5. **Documents and signatures**: All decisions generated as PDF, signed with electronic signature; in-person submissions require olograph signature on the cerere + scanning + attachment.

6. **Status / lifecycle**: cerere → înregistrată → (în așteptare documente) → în examinare → semnată Utilizator → (semnată Șef direcție, if 3-level) → aprobată Șef CNAS → finalizată; or returnată at any approval gate; corresponding Decizie types: acordare / refuz / terminată (when superseded by a new active decision of the same type).

7. **Notifications**: Beneficiar (cerere primită, decizie emisă), Utilizator CNAS (cerere atribuită, decizie returnată), Șeful direcției (decizie returnată, if 3-level), Șeful CNAS (decizie de aprobat). On return-to-Utilizator from Șeful CNAS in the 3-level flow, both Șeful direcției și Utilizator CNAS are notified.

8. **Payment**: All decisions feed Registrul de conturi de plată; payment per HG 929/2006 (general payment regulation for pensions, social allowances, indemnities, capitalized periodic payments and state financial support).

9. **Diagrams (BPMN-style flows)** present in source:
   - `images/pdf_p226_full.png` — Figura AE3.2 (preceding context — info-only request flow, referenced as analogous template).
   - `images/pdf_p233_full.png` — Figura A3.3.1 (3.3-A proactive).
   - `images/pdf_p237_full.png` — Figura A3.3.2 (3.3-A at-request).
   - `images/pdf_p241_full.png` — Figura E3.3 (3.3-E international agreements).
   - `images/pdf_p246_full.png` — Figura A3.4.1 (3.4-A proactive).
   - `images/pdf_p249_full.png` — Figura A3.4.2 (3.4-A at-request).
   - `images/pdf_p252_full.png` — Figura B3.4 (3.4-B international agreements).
   - `images/pdf_p258_full.png` — Figura A3.5 (3.5-A survivor's pension at-request).
   - `images/pdf_p262_full.png` — Figura E3.5 (3.5-E survivor's pension under international agreements).
