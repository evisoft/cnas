# 01 - Introduction & Architecture (TOR pp. 5-20, §Introducere - §2.3)

Source: `tor/TOR.md` lines 286-1755, covering Introduction, §1.1-§1.6, §2.1-§2.3.

---

## 1. Project context

**System name:** SI „Protecția Socială" (Social Protection Information System).

**Beneficiary:** Casa Națională de Asigurări Sociale (CNAS) — the National Social Insurance House of the Republic of Moldova.

**Implementation timeframe (inferred from TOR scope):** 2026-2028 build cycle.

**Purpose:** The TOR (Termeni de referință) describes the basis for elaborating the SI „Protecția Socială", including architectures, information objects, processes, functional/non-functional constraints, and approaches to management, implementation, and maintenance.

**Current situation (§1.1):**
- CNAS currently uses several distinct IT systems/applications for digital support of operational activities.
- The base system "SI Protecția Socială" was implemented in **2007**. It has undergone multiple developments and adjustments driven by normative acts and services rendered, **without essential changes to its technology and architecture**, which are now **morally and physically obsolete**.
- This limits future development and generates significant security risks.
- Both obsolete-system security risks and legislative changes forcing process redesign drive the need to replace the obsolete applications and information system.
- The system is **NOT compatible with the MCloud governmental technological platform** and cannot ensure connectivity/interoperability with future European systems (notably **EESSI** - Electronic Exchange of Social Security Information).

**Scope (§1.2):** SI „Protecția Socială" must provide CNAS with an efficient, reliable, modern software solution serving as the **single mechanism** for:
- Recording contribution payers (plătitori) and their obligations to BASS (state social insurance budget).
- Nominal recording of insured persons and individual social-insurance contributions.
- Receiving from **Serviciul Fiscal de Stat (SFS)** and processing indicators of declarations on nominal records and on the calculation/use of mandatory state social-insurance contributions.
- Daily receipt from **Trezoreria de Stat** (State Treasury) of incoming payments to BASS and refunds.
- Establishing pensions and social-insurance benefits and other social benefits per normative framework.
- Recording beneficiaries of pensions and benefits.
- Recording payment of pensions and benefits.
- Data management / operational, statistical, and analytical reporting of BASS revenues and expenditures.
- Includes the **Registrul de stat al evidenței individuale în sistemul public de asigurări sociale** (State Register of Individual Records in the Public Social Insurance System).

**Scope boundaries:** SI „Protecția Socială" exchanges data both with internal CNAS systems (e.g., the financial system FMS) and with external public-authority systems. It is a component of the national state IS portfolio.

---

## 2. Strategic objectives (§1.3)

Objectives are derived per **HG nr. 788/2022** (the concept-approval government decision).

### General/specific objectives (verbatim Romanian + English gloss):

1. **„oferirea instrumentelor automatizate eficiente de colectare, prelucrare, actualizare și stocare a datelor despre persoanele asigurate, angajatori/plătitori de contribuții la BASS și beneficiari de pensii și prestații sociale"**
   - *Provide efficient automated tools for collecting, processing, updating, and storing data on insured persons, employers/contribution payers to BASS, and beneficiaries of pensions and social benefits.*

2. **„unificarea proceselor de administrare a conturilor personale ale persoanelor asigurate și ale plătitorilor, precum și a proceselor asigurării/oferirii pensiilor și prestațiilor sociale"**
   - *Unify the administration processes for personal accounts of insured persons and payers, as well as processes of providing pensions and benefits.*

3. **„sporirea accesibilității serviciilor publice pentru cetățeni"**
   - *Increase accessibility of public services for citizens.*

4. **„reducerea efortului operațional și financiar în procesele de administrare a contribuțiilor în BASS și de asigurare/oferire a prestațiilor sociale"**
   - *Reduce operational and financial effort in administering BASS contributions and delivering social benefits.*

5. **„evidența electronică și trasabilitatea conturilor personale de asigurări sociale ale persoanelor asigurate, a pensiilor și a prestațiilor sociale"**
   - *Electronic record-keeping and traceability of personal social-insurance accounts, pensions, and benefits.*

6. **„asigurarea interacțiunii eficiente dintre CNAS, ministere și alte autorități ale administrației publice centrale și locale interesate, instituții deținătoare de registre de stat"**
   - *Ensure efficient interaction between CNAS, ministries, other central/local public authorities, and holders of state registers.*

7. **„sporirea disponibilității și diversificarea indicatorilor statistici în domeniul protecției sociale, prin aplicarea abordării inovative de calculare a indicatorilor"**
   - *Increase availability and diversify statistical indicators in social protection through innovative calculation approaches.*

8. **„asigurarea schimbului de date cu sistemele informaționale din domeniul financiar și contabil (sisteme informaționale interne ale CNAS)"**
   - *Ensure data exchange with financial and accounting IS (internal CNAS systems).*

---

## 3. Design principles (§1.4)

1. **Principiul legalității** — Legality: design/operation in conformity with national legislation.
2. **Principiul respectării drepturilor omului** — Human rights: strict conformity with national legislation, international treaties on human rights (notably right to private life).
3. **Principiul conformității prelucrării datelor cu caracter personal** — Personal-data processing per Art. 4 of Law 133/2011.
4. **Principiul integrității datelor** — Data integrity: content preserved, unequivocal interpretation, no distortion or destruction.
5. **Principiul veridicității datelor** — Data truthfulness.
6. **Principiul plenitudinii datelor** — Data completeness: ensure the volume of information necessary for granting benefits per normative acts.
7. **Principiul confidențialității informației** — Confidentiality: restrict access of unauthorized persons to limited-access info, per legislation.
8. **Principiul îndrumării procesului de utilizare a SI** — User-guidance: guarantee operative access to information within users' competence and access level.
9. **Principiul securității informaționale** — Information security: integrity, exclusivity, accessibility, and effectiveness of data protection against loss/alteration/distortion/deterioration/modification/unauthorized access. Resistance to attacks, protection of confidentiality, integrity, and readiness at both system and data levels.
10. **Principiul compatibilității** — Compatibility with existing public IS in the country.
11. **Principiul modularității și scalabilității** — Modularity and scalability: develop with minimal intervention on previously created components.
12. **Principiul neexcesivității și pertinenței** — Non-excess and pertinence: process only relevant and necessary information.
13. **Principiul interoperabilității** — Interoperability: technical capacity of IS and organizational capacity of participants to reuse data through efficient data-exchange processes.
14. **Principiul independenței de platformă** — Platform independence: user interface does not impose a particular software/hardware platform.
15. **Principiul adresării unice** — Once-only: citizens and businesses provide data once; administrations share internally to deliver services.
16. **Principiul arhitecturii bazate pe servicii (SOA)** — Service-oriented architecture: split functionality into smaller distinct services usable across a network.
17. **Principiul dezvoltării progresive** — Progressive development: ability to extend the IS with new functions or improve existing ones per technological advancement.
18. **Principiul simplității și comodității utilizării** — Simplicity and usability: all applications/technical means designed on exclusively visual, ergonomic, and logical concepts.
19. **Principiul independenței și neutralității tehnologice** — Technological independence and neutrality: implementation based on functional requirements independent of specific technologies/programs.
20. **Principiul controlului** — Control: ensemble of organizational and technical measures ensuring high quality of the information resource, reliability of preservation, and correct use per legislation.
21. **Principiul auditului informatic** — IT audit: register data about actions and events of the IS to reconstruct history of an object or its previous state.
22. **Principiul accesibilității informației cu caracter public** — Public-information accessibility: implement procedures ensuring requestor access to public information furnished by the IS.

---

## 4. Glossary (§1.5)

### Abbreviations (Table 1.1):

| # | Abbreviation | Meaning |
|---|---|---|
| 1 | AGE | Agenția de Guvernare Electronică (e-Governance Agency) |
| 2 | CNAS | Casa Națională de Asigurări Sociale |
| 3 | BASS | Bugetul Asigurărilor Sociale de Stat (State Social Insurance Budget) |
| 4 | AP | Autoritate publică |
| 5 | API | Application programming interface |
| 6 | ASP | Agenția Servicii Publice (Public Services Agency) |
| 7 | BD | Bază de date |
| 8 | BPML | Business process modelling language |
| 9 | CAEM | Clasificatorul activităților din economia Moldovei (NACE-equivalent classifier) |
| 10 | CFOJ | Clasificatorul formelor organizatorico-juridice ale agenților economici din Moldova |
| 11 | CFP | Clasificatorul formelor de proprietate |
| 12 | COTS | Commercial off-the-shelf |
| 13 | CUATM | Clasificatorul unităților administrativ-teritoriale |
| 14 | KPI | Key performance indicators |
| 15 | SFS | Serviciul Fiscal de Stat (State Tax Service) |
| 16 | MCabinet | Portalul guvernamental al cetățeanului și antreprenorului (citizen/entrepreneur portal) |
| 17 | MCloud | Platforma tehnologică guvernamentală comună |
| 18 | MPass | Serviciul electronic guvernamental de autentificare și control al accesului |
| 19 | MSign | Serviciul guvernamental de semnătură electronică |
| 20 | MPay | Serviciul guvernamental de plăți electronice |
| 21 | MPower | Serviciu guvernamental de gestiune a împuternicirilor de reprezentare (powers of attorney) |
| 22 | MNotify | Serviciul guvernamental de notificare electronică |
| 23 | MLog | Serviciul electronic guvernamental de jurnalizare (logging) |
| 24 | QBE | Query by example |
| 25 | RSP | Registrul de stat al populației (State Population Register) |
| 26 | RSUD | Registrul de stat al unităților de drept (State Register of Legal Units) |
| 27 | SDD | Software design document |
| 28 | SGBD | Sistem de gestiune a bazelor de date (DBMS) |
| 29 | SI | Sistem informațional |
| 30 | SIA | Sistem informațional automatizat |
| 31 | SLA | Service Level Agreement |
| 32 | SOA | Service Oriented Architecture |
| 33 | SPOF | Single Point Of Failure |
| 34 | STISC | Serviciul Tehnologia Informației și Securitatea Cibernetică (IT & Cybersecurity Service) |
| 35 | TI | Tehnologie informatică |
| 36 | TIC | Tehnologia informațiilor și comunicațiilor |
| 37 | TLS/SSL | Transport layer security / Secure sockets layer |
| 38 | Cod ID | Codul de identificare a Persoanei asigurate (formerly CPAS) |
| 39 | SRS | Software Requirements Specification |
| 40 | SDD | Software Design Document (duplicate of #27) |

### Definitions (Table 1.2):

1. **Bază de Date** — Set of data organized per a conceptual structure describing basic characteristics and relations among entities.
2. **Credențiale** — Set of attributes establishing identity and authenticity of users and systems within IS.
3. **Date** — Elementary informational units about persons/subjects/facts/events/phenomena/processes/objects/situations etc., in a form allowing notification, comment, and processing.
4. **Date cu caracter personal** — Any information relating to an identified or identifiable natural person (data subject); identifiable = identifiable directly or indirectly via ID number or specific elements of physical, physiological, mental, economic, cultural, or social identity.
5. **Flux de Lucru** — Administrative process of an organization in which tasks/procedures/info are processed/executed in a sequence dictated by predefined rules for producing a product or service.
6. **Integritatea datelor** — State of data when content is preserved and interpreted unequivocally under random actions; preserved if not altered or destroyed (deleted).
7. **Jurnalizare** — Function of recording event information (date/time, user, action).
8. **Metadate** — Means of assigning semantic value to data stored in a DB ("data about data").
9. **Obiect informațional** — Virtual representation of material and non-material entities.
10. **Query by Example** — DB-query method using native-text syntax; advantage: no specific requirements on query structure.
11. **Resursă informațională** — Set of documented information in the IS, maintained per requirements and legislation.
12. **Sistem informațional** — Information-processing system together with associated organizational, human, and technical resources, providing and distributing information.
13. **Software Design Document** — Director document covering data structures and constraints, IS architecture (conceptual sections), IS interface (user-interface components), and functionalities (implementation scenarios).
14. **Software Requirements Specification** — Document describing all interaction scenarios between users and the IT application.
15. **Tehnologie informatică și de comunicație** — Common term encompassing all technologies used for exchanging and manipulating information.
16. **Veridicitatea datelor** — Level of correspondence between data stored in memory/documents and the real state of objects in the system's domain.

---

## 5. Normative framework (§1.6)

### A. Acts regulating CNAS activity domains subject to automation:

1. **Legea nr. 909/1992** — Social protection of citizens affected by the Chernobyl catastrophe.
2. **Legea nr. 1544/1993** — Pension insurance for military, command-corps personnel of internal affairs bodies, and General Carabineers Inspectorate personnel.
3. **Legea nr. 544/1995** — Status of judges.
4. **Legea nr. 1111/1997** — Ensuring activity of the President of the Republic of Moldova.
5. **Legea nr. 156/1998** — Public pension system.
6. **Legea nr. 489/1999** — Public social insurance system.
7. **Legea nr. 499/1999** — State social allowances for certain citizen categories.
8. **Legea nr. 756/1999** — Insurance for work accidents and occupational diseases.
9. **Legea nr. 121/2001** — Supplementary social protection for certain population categories.
10. **Legea nr. 1591/2002** — Supplementary social protection for certain pension beneficiaries and population categories.
11. **Legea nr. 190/2003** — Veterans.
12. **Legea nr. 289/2004** — Indemnities for temporary work incapacity and other social-insurance benefits.
13. **Legea nr. 147/2014** — Modification/completion of certain legislative acts.
14. **Legea nr. 315/2016** — Social benefits for children.
15. **Legea nr. 105/2018** — Promotion of employment and unemployment insurance.
16. **Legea nr. 127/2020** — Indemnity for survivors of medical personnel who died from COVID-19 medical activities.
17. **Legea nr. 317/2024** — War veterans.
18. **Legea nr. 110/2025** — Physical education and sport.
19. **HG nr. 412/2004** — Approval of normative acts on establishing/paying pensions of civil servants.
20. **HG nr. 230/2020** — Organization and functioning of CNAS.
21. **HG nr. 316/2021** — Support measures for employers/employees under pandemic restrictions.
22. **HG nr. 788/2022** — Approval of the **Concept of SI „Protecția Socială"** (the foundational concept document).

### B. Acts regulating ICT and information security:

1. **Legea nr. 467/2003** — Informatization and state information resources.
2. **Legea nr. 71/2007** — Registers.
3. **Legea nr. 133/2011** — Protection of personal data (GDPR-equivalent).
4. **Legea nr. 142/2018** — Data exchange and interoperability.
5. **HG nr. 562/2006** — Creation of state automated IS and resources.
6. **HG nr. 733/2006** — E-Government Conception.
7. **HG nr. 656/2012** — Interoperability Framework Programme.
8. **HG nr. 1090/2013** — Regulation on **MPass** (auth & access control service).
9. **HG nr. 128/2014** — Regulation on **MCloud** (use, administration, development of common gov tech platform).
10. **HG nr. 405/2014** — Regulation on **MSign** (integrated e-signature service).
11. **HG nr. 708/2014** — Regulation on **MLog** (logging service).
12. **HG nr. 414/2018** — Measures for consolidating public-sector data centers and rationalizing administration of state IS.
13. **HG nr. 211/2019** — Regulation on **MConnect** (interoperability platform).
14. **HG nr. 375/2020** — Concept of SIA „Registrul împuternicirilor de reprezentare în baza semnăturii electronice" (**MPower**) and its register-keeping regulation.
15. **HG nr. 376/2020** — Regulation on functioning/use of **MNotify** (e-notification service).
16. **HG nr. 712/2020** — Regulation on functioning/use of **MPay** (e-payment service).
17. **HG nr. 650/2023** — Digital Transformation Strategy of Moldova 2023-2030.
18. **HG nr. 562/2025** — Cybersecurity obligations of service providers in critical sectors.
19. **HG nr. 677/2025** — Consolidating access to e-public services via the **EVO** integrated government portal; approval of unified design-model measures.

### C. Standards and good practices in ICT:

1. **OMDI nr. 78/2006** — Technical regulation "Procesele ciclului de viață al software-ului" RT 38370656-002:2006.
2. **Ordin viceministru DI nr. 94/2009** — Certain technical regulations.
3. **SM ISO/IEC/IEEE 15288:2024** — Systems and software engineering. System life-cycle processes.
4. **SM ISO/IEC/IEEE 14764:2022** — Software engineering. Software life-cycle processes. Maintenance.
5. **SM EN ISO/IEC 27001:2017** — ISMS requirements.
6. **SM EN ISO/IEC 27002:2017** — Code of practice for information security management.
7. **SM ISO/IEC 15408-1:2022** — IT security evaluation criteria. Part 1: Introduction and general model.
8. **SM ISO/IEC 15408-2:2022** — Part 2: Functional security components.
9. **SM ISO/IEC 15408-3:2022** — Part 3: Security assurance components.
10. **WCAG 2.1 level AA** — https://www.w3.org/TR/WCAG21/ — UI accessibility.
11. **W3C recommendations** — http://www.w3c.org — Web content quality, correct visualization across browsers, cross-platform compatibility.
12. **OWASP Top 10** — Web application security.
13. **W3C validator recommendations** — http://validator.w3.org — All generated web pages must be tested.
14. **egov4dev** — Official documentation library for developers working with the AGE ecosystem (gov platform integration, common tech stack, architectural principles).

---

## 6. Architecture (§2.1)

### Architectural style and constraints:

- **SOA-based**, modular reusable components with abstract interfaces.
- **Multinivel** (multi-tier) — minimum **3 architectural levels**: **Nivelul de prezentare** (presentation), **Nivelul aplicației/business logicii** (application/business logic), **Nivelul de Date** (data).
- System components are relatively independent; interaction via dedicated interfaces.
- **Web UI** delivered via internet browser supporting HTML5 (Microsoft Edge, Mozilla Firefox, Google Chrome, Safari).
- Reliable and scalable for both growth in user count and growth in data volume.
- Use widely-adopted technologies in Moldova/region, compatible with current CNAS technologies, **interoperable with external systems**, **independent of any single vendor**.
- **Secure connections required**: client-to-app-server links use **TLS/SSL cryptographic protocols**.
- **Hosting:** must be hosted on the **MCloud** governmental technological platform.

### Architectural contours/perimeters:

1. **Infrastructura TIC a CNAS** — CNAS ICT infrastructure: contains SI „Protecția Socială" components and other CNAS IS.
2. **Infrastructura TIC a AGE** — e-Governance Agency ICT infrastructure: contains **MCabinet**, **Portalul guvernamental de date** (Gov Open Data Portal / PGD), **MPass**, **MSign**, **MPower**, **MPay**, **MNotify**, **MLog**, **MConnect**, and the **MConnectEvents** component.
3. **National external IS:** **RSUD**, **RSP**, **SIA SFS**, **SIDDCM**, **PCCM**, **eCMND**, **SIAÎSȘ**, **SIVE**, **SIAAS**, and other IS.
4. **International external IS:** **EESSI** (Electronic Exchange of Social Security Information) and others.

### SI „Protecția Socială" internal functional components (per Figure 2.1):

The SI „Protecția Socială" within CNAS infrastructure consists of these modules / functional component groups (each is later expanded in §2.1 detail):
- **Administrarea și monitorizarea evidenței plătitorilor de contribuții** — administering/monitoring contribution-payer records.
- **Administrarea și monitorizarea evidenței persoanelor asigurate** — administering/monitoring insured-persons records.
- **Administrarea și monitorizarea evidenței contribuțiilor de asigurări sociale** — administering/monitoring social-insurance contributions records.
- **Cererea/Decizia de acordare/modificare a pensiei și a prestației sociale, dosarul beneficiarului** — application/decision flow + beneficiary case file.
- **Administrarea plăților pensiilor și ale prestațiilor sociale** — pension/benefit payments administration.
- **Administrarea securității** — security administration.
- **Administrare și control** — administration & control.
- **Raportarea statistică și analitică** — statistical/analytical reporting.
- **Database** — persistent data store.
- **SI Financiar (FMS)** — internal CNAS financial IS, integrated with SI „Protecția Socială".

### Per-module functional inclusions (verbatim breakdown from §2.1):

**Administrarea și monitorizarea evidenței plătitorilor de contribuții:**
- Receive info from other institutions and register/update payer data.
- Manual registration/update of contribution payers.
- Maintain and explore modification history and data-source traceability (auto-import vs manual entry).
- Detailed exploration of Registrul plătitorilor de contribuții — advanced search, dynamic filtering, visualization, analysis.
- Generate and view reports.

**Administrarea și monitorizarea evidenței persoanelor asigurate:**
- Receive info from other institutions and register/update insured-person data.
- Manual register/update of insured persons.
- Maintain change history and source traceability.
- Manage social-insurance contracts concluded with CNAS.
- Detailed exploration of Registrul persoanelor asigurate.
- Manage activity periods (work relations) of insured persons, **including periods up to 1 January 1999 based on work-book (carnet de muncă) info**.
- Manage and view reports.

**Administrarea și monitorizarea evidenței contribuțiilor de asigurări sociale:**
- Manage contribution data declared to SFS **starting 1 January 2018** — declaration intake, auto-validation, registration, payer-level and insured-person-level recording.
- Manage data of holders of *patentă de întreprinzător* (entrepreneur patent) — intake, auto-validation, registration, contribution calculation.
- Manage data of persons performing independent activities — intake, auto-validation, registration, contribution calculation.
- Manage insured-person declarations for periods **before 1 January 2018** — register/modify declarations, auto-validate, maintain insurance history.
- Manage declarations registered as unidentified, ensuring re-association with insured persons.
- Manage the process of incorrectly paid benefit sums when previously declared contributions are corrected — identify amounts due back, set recovery obligation from beneficiary or payer.
- Detailed exploration of insured-person account extract.
- Manage contribution data declared to CNAS for periods **before 1 January 2018** — register declarations, auto-validate, payer-level recording.
- Manage data on modification of obligations to BASS (contributions, late penalties, fines, admin sanctions) based on documents other than declarations (control results, special-record decisions, court rulings, subsidiary liability, admin contraventions, etc.).
- Payment administration — intake/registration/distribution of payments from State Treasury onto payer accounts; verify correctness and update records.
- Distribute payments to insured persons in correlation with registered contributions; auto-update insured-person accounts; manage unallocated payments; full history.
- Manage and execute reclassification of paid amounts between payment types or between payers (payment correction).
- Manage and execute refunds from BASS to payers in case of overpayment.
- Manage and calculate balances per period and form generalizing reports.
- Detailed exploration of Registrul declarațiilor.
- Detailed exploration of payer-account extract.
- Manage records of insolvent payers; explore Registrul plătitorilor de contribuții insolvabili.
- Administer penalty-calculation processes.
- Generate and view reports.
- Administer and monitor data delivery to SFS and State Treasury — correctness, completeness, timely transmission.

**Cererea/Decizia de acordare/modificare a pensiei și a prestației sociale, dosarul beneficiarului:**
- Manage and record submission, intake, registration, automated verification of applications.
- Manage intake/registration/verification/validation of life-event data.
- Automated creation of decisions — right determination and calculation/establishment of pension/benefit amount with associated indicators.
- Automated case-file population with: applications, decisions generated by the IS; scanned documents; other beneficiary documents; search/view of the case file by users and auditors.
- Electronic archiving (with metadata and search).
- Detailed exploration of Registrul deciziilor.
- Generate and view reports.

**Administrarea plăților pensiilor și ale prestațiilor sociale:**
- Configure automated payment processes.
- Execute and monitor payment processes.
- Record payments to/from MPay.
- View operational logs.
- Record beneficiary accounts of pensions/benefits.
- Manage benefit indexation process.
- Manage mass-recalculation of benefits.
- Manage payment-blocking process.
- Automatic suspension of benefit payments.
- Manage automated retentions/garnishments.
- Manage refunds from MPay.
- Manage suspension/termination of benefit payments.
- Generate and view reports.

**Administrarea securității:**
- User lifecycle management (create/update/deactivate/delete).
- Role and permission configuration for access control.
- Audit-log management/storage/visualization for all critical security actions.
- Generate and view reports.

**Administrare și control:**
- Manage programmatic interfaces for internal IS interconnection.
- Manage programmatic interfaces for external IS interconnection.
- Administer classifiers.
- Manage usage reports and statistics.
- System-event logging.
- Performance monitoring of SI „Protecția Socială".
- Technical support and maintenance.
- **BPM** for creating, modifying, and monitoring workflows.

**Raportarea statistică și analitică:**
- Administer aggregation/cumulation processes for payer, insured-person, and contributions data (revenues).
- Administer aggregation/cumulation for pension/benefit establishment & payment data (expenditures).
- Monitor statistical-process execution; view logs.
- View standard predefined reports for state institutions.
- Create/modify/delete report forms.
- Report generator with user-configurable export.

### Integration points / external systems (data exchange):

Per §2.1, SI „Protecția Socială" exchanges data **via MConnect** (the interoperability platform) and **MConnectEvents** (for event publication/consumption — proactive services) with:

| System | Acronym | Purpose |
|---|---|---|
| Registrul de stat al unităților de drept | RSUD | Legal-entity data |
| Registrul de stat al populației | RSP | Natural-person data |
| Sistemul informațional automatizat al SFS | SIA SFS | Tax service data |
| Determinarea dizabilității și capacității de muncă | SIDDCM | Disability/work-capacity determination |
| Portalul certificatelor de concediu medical | PCCM | Medical-leave certificates |
| Constatarea medicală a nașterii și a decesului | eCMND | Medical birth/death certification |
| Înregistrare cu Statut de Șomer | SIAÎSȘ | Unemployment-status registration |
| Vulnerabilitatea Energetică | SIVE | Energy vulnerability |
| Asistența Socială | SIAAS | Social assistance |
| Electronic Exchange of Social Security Information | EESSI | European social-security exchange |
| Other IS | — | — |

Additionally exchanges data with **CNAS Financial IS (FMS)**.

### Integration with **shared governmental services** (required):

| Service | Purpose |
|---|---|
| **MPass** | User authentication via electronic signature or mobile signature |
| **MSign** | Apply and validate electronic signatures |
| **MPay** | Pay calculated/outstanding BASS contributions |
| **MPower** | Validate powers of representation for natural and legal persons |
| **MNotify** | Notify actors involved in business processes about workflow events |
| **MLog** | Log business events produced within SI „Protecția Socială" |

Plus (per the architectural contour list): **MCabinet** (citizen/entrepreneur portal), **Portalul guvernamental de date** (PGD — open data portal).

### Architecture diagram images referenced:

- **Figura 2.1** — "Componentele SI „Protecția Socială" și interacțiunea între ele." — full page render: `images/pdf_p014_full.png`; clips `images/pdf_p014_1.png` through `images/pdf_p014_89.png`.
- **Figura 2.2** — "Utilizatori umani și sisteme informaționale care interacționează cu SI „Protecția Socială"" — full page render: `images/pdf_p017_full.png`; clips `images/pdf_p017_1.png` through `images/pdf_p017_34.png`.
- **Figura 2.3** — "Obiectele informaționale ale Sistemului informațional." — full page render: `images/pdf_p020_full.png`; clips `images/pdf_p020_1.png` through `images/pdf_p020_38.png`.

(No formal requirement codes such as `ARH XXX` or `INT XXX` appear in this section range — these likely appear in later sections.)

---

## 7. Users and roles (§2.2)

The roles are **generic** — they determine access rights of authorized users to UI and SI functionalities. At data/document level, access is supplemented by user-group configurations, explicit rights, and workflow-specific configurations.

### Categories of human actors (per Figure 2.2):

#### 1. Utilizator Internet (Anonymous Internet user)
Access pattern: **anonymous / public**.
Functionalities:
- a. Explore public-interface content of SI „Protecția Socială".
- b. Explore public data from CNAS official registers.
- c. Explore data about documents issued by CNAS (certificates, extracts, etc.).
- d. Explore data about public documents produced via SI workflows.
- e. Explore statistical reports and performance indicators.

#### 2. Utilizator autorizat (Authorized user — non-applicant)
Access pattern: **authenticated, limited**.
- A role for natural persons with limited access, per access rights (e.g., **without the possibility of submitting applications**).

#### 3. Solicitant (Applicant)
Access pattern: **authenticated; can be natural or legal person**.
Functionalities common to all applicant categories:
- a. Access services available to applicants (natural or legal persons).
- b. Submit applications and view their state.
- c. Manage profile.
- d. View active/terminated social benefits (for natural persons).
- e. View payment accounts issued for refund of amounts.
- f. View documents (decisions) issued in their name.
- g. View notifications.
- h. Search/view data stored in SI per access rights.
- i. Generate reports per access rights.

#### 4. Utilizator CNAS (CNAS employee — CTAS)
Access pattern: **authenticated CNAS staff**; key user category involved in all workflows. Can be divided into multiple distinct roles.
Functionalities:
- a. Access to all Solicitant functionalities.
- b. Process applications/requests for CNAS public services.
- c. Manage tasks tied to SI workflows.
- d. Manage Applicant profile data (contacts, notification methods, etc.).
- e. Prepare documents produced in SI workflows.
- f. Manage data on contribution payers and insured persons.
- g. Manage data on social benefits.
- h. Manage documents issued by CNAS.
- i. View notifications.
- j. Generate/download documents and reports for service duties.

#### 5. Șeful direcției (Head of department — CNAS/CTAS)
Access pattern: **authenticated decision-maker within service workflows**. Can be divided into multiple distinct roles.
Functionalities:
- a. Access to Utilizator CNAS functionalities.
- b. Distribute applications/requests for examination on public services.
- c. Supervise activity of Utilizator CNAS assigned applications.
- d. Verify/reject document/decision drafts.
- e. View notifications.
- f. Generate/download documents and reports for service duties.

#### 6. Șeful CNAS (CNAS head — CTAS)
Access pattern: **authenticated final decision-maker**. Can be divided into multiple distinct roles.
Functionalities:
- a. Access to Utilizator CNAS functionalities.
- b. Supervise activity of Utilizator CNAS assigned applications.
- c. Approve/reject document/decision drafts.
- d. View notifications.
- e. Generate/download documents and reports for service duties.

#### 7. Administrator de Sistem (System administrator)
Access pattern: **highest privilege; system-supervisor role** responsible for proper SI operation.
Functionalities:
- a. Administer user profiles, roles, and access rights.
- b. Manage classifiers, nomenclatures, and metadata.
- c. Configure parameters for SI components.
- d. Configure workflows and electronic forms.
- e. Configure reports and document templates.
- f. Configure datasets for export.
- g. Verify integrity of records in CNAS electronic registers.
- h. Monitor, diagnose, and debug operational problems.
- i. Generate reports for IS audit and DB content audit.
- j. Configure schedule for launching procedures automatically.
- k. Manually launch procedures.
- l. View notifications.

#### 8. Administrator tehnic (STISC)
Access pattern: **external technical administrator** (STISC — Serviciul Tehnologia Informației și Securitatea Cibernetică).
- Exercises duties per technical-administration normative framework for state IS.
- Ensures configuration of SI infrastructure (development environment + test environment).
- Ensures post-implementation technical support and maintenance at MCloud infrastructure level.

### External/system actors interacting (per Figure 2.2):

External IS interacting with SI „Protecția Socială": **RSUD, SIA SFS, SIDDCM, RSP, PCCM, eCMND, SIAÎSȘ, SIVE, SIAAS, FMS**, plus shared services **MPass, MSign, MConnect, MPower, MPay, MNotify, MLog**, and **PGD** (Portalul guvernamental de date).

---

## 8. Information objects (§2.3)

The TOR identifies **12 categories** of information objects. Each is identified via a **unique identifier code**, including those provided by external data sources used to form the object (e.g., **IDNP** — natural-person ID; **IDNO** — legal-entity ID).

### 1. Cerere (Application/Request)
Complex object describing the compartments of the electronic form used for preparing applications for CNAS services. Three template types (each with its own specific template):
- a. Registration in CNAS registers.
- b. Request for granting social benefits.
- c. Issuance of extracts, information, and other document categories.

### 2. Dosar (Case file)
Complex object covering all processing data for the Solicitant's request for CNAS public services. A dosar is created upon arrival of a new application that is routed to a CNAS officer for processing. Components:
- a. Solicitant (applicant).
- b. Service-request applications.
- c. Data about the Solicitant.
- d. Other data and documents relevant to the business processes of processing the service request.

### 3. Document
Complex object covering all documents elaborated or inserted within SI workflows (e.g., decision, account extract, information note, certificate, etc.).

### 4. Pașaport serviciu (Service passport)
Object containing data about the electronic service provided by CNAS.

### 5. Plătitor de contribuții (Contribution payer)
Object containing data about a legal or natural person (sourced from **RSUD** or **SI SFS**).

### 6. Persoană asigurată (Insured person)
Object containing data about a natural person (sourced from **RSP**).

### 7. Rapoarte (Reports)
Set of standard reports (physically embedded) or ad-hoc reports generated by SI PS, intended for all user levels per restricted access lists, for publication, management, and monitoring of activity of all involved in use/management of the system.

### 8. Profilul utilizatorului (User profile)
Object consisting of all data about authorized users. Contains:
- Information for authorization in the system.
- Name, surname (nume, prenume).
- Identification data.
- Postal address.
- Contact phone.
- Email.
- Economic agents to which the user is attached.
- Functionalities accessible to the user (rights and roles).
- User's activity history within SI PS.

### 9. Sarcină (Task)
Object describing tasks assigned to authorized SI users, created and managed per workflows and users' service duties.

### 10. Notificări (Notifications)
Objects forming a component of the SI's notification mechanism.

### 11. Fișiere log (Log files)
Objects for informational audit and information-security policy. Any potentially dangerous modification — creation, modification, deletion marking, status change, etc. — must be registered in special registers (log files) showing time and the user who performed the modification. When the modification does not entail physical suppression of data, for each document it must be possible to see the user who performed the last modification.

### 12. Nomenclatoare și clasificatoare (Nomenclatures and classifiers)
Category consisting of all SI metadata. Includes:
- **National classifiers (relatively static)** managed by Biroul Național de Statistică: **CAEM, CUATM, CFP, FOJ**, etc.
- Classifiers managed by **Agenția Servicii Publice (ASP)**.
- Internal SI PS nomenclatures.

---

## Notes on requirement codes

No explicit requirement codes such as `ARH XXX` or `INT XXX` appear within TOR lines 286-1721. Architectural prescriptions in §2.1 are given as descriptive bullets rather than coded requirements; the formal coded-requirement catalog presumably appears later in the document (likely in the §3+ functional/non-functional requirements chapters).
