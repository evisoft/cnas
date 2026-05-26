# SI "Protecția Socială" — Use Cases & Generic Workflows (TOR pp. 20-45)

Source: `tor/TOR.md` lines 1721-3750 (PDF pages 20-45). Covers section 2.4 (system functionalities), 2.5 (generic workflows), and section 3 (UC01-UC23 functional requirements).

---

## Section 2.4 — System Functionalities (4 functional groupings)

The CNAS SI PS functionalities are partitioned into 4 groups based on the system architecture and CNAS business needs:

1. **Public data access and viewing functionalities (Funcționalități de accesare și vizualizare publică a datelor)** — Provides anonymous users with functionalities to publicly explore data from CNAS official registers and export them in editable format.

2. **Public service request processing functionalities (Funcționalități de procesare a cererilor de solicitare a serviciilor publice furnizate de CNAS)** — Provides the totality of use cases to different categories of authorized CNAS users for carrying out their service duties.

3. **Contributor and insured person data management functionalities (Funcționalități aferente gestiunii datelor despre plătitorii de contribuții și persoanele asigurate)** — Provides use cases needed for registration and management of data and documents related to contributors and insured persons.

4. **Administration and system functionalities (Funcționalități de administrare și de sistem)** — Implements all use cases needed for administering and configuring SI "Protecția Socială".

Actors identified in the UC diagram (Figure 2.4): **Utilizator autorizat** (Authorized User), **Solicitant** (Applicant), **Șeful CNAS** (Head of CNAS), **Șeful direcției** (Head of Department), **Utilizator CNAS** (CNAS User), **Utilizator Internet** (Internet User), **Administrator de sistem** (System Administrator), **Sistem** (System itself, for automated procedures).

Per the diagram, there are 23 use cases (UC01-UC23) accessible to these actors.

---

## Section 2.5 — Generic System Workflows (5 generic workflows)

The SI "Protecția Socială" must be implemented on a **transactional principle**: every addition, update, or removal of data is performed through specific electronic forms processed via specialized workflows.

### 2.5.1 Workflow for processing service-request applications (Flux de lucru pentru procesarea cererilor de prestare servicii)

Workflow involving CNAS representatives and external information systems for receiving data from official sources and validating application content by CNAS officials. Includes multi-user interaction with clear responsibilities.

Indicative scenario / instruments to be implemented:
- SI PS notifies responsible CNAS officials about the registration of a new application;
- the responsible CNAS official authenticates in the system using **Mpass** (where applicable);
- the official responsible for receiving the application verifies the data and attached documents (where applicable);
- if not all required documents have been submitted within the established term, the system **rejects the application** and SI PS sends a notification to the Applicant informing them of the need to revise the application;
- if the application has been correctly completed, the system **assigns the application** to the CNAS official responsible for processing. SI PS **generates the electronic case file** for application examination;
- the system performs the necessary work (data completion, electronic document development, distribution of certain documents from the case file to other responsible CNAS officials) depending on the requested service to process the application and provide the service to the Applicant;
- throughout the case-file examination flow, SI PS **records all traceability events** (data changes, status changes of the case file, approval/rejection events) and sends relevant notifications to all actors involved;
- when an outgoing document is to be issued, the CNAS official completes the case file with data necessary for drawing up the document to be issued to the Applicant;
- the CNAS official **electronically signs the document using Msign**;
- depending on the document delivery mode (electronic or paper), the CNAS official performs the necessary work to issue it;
- SI PS notifies the Applicant about the completion of application processing.

### 2.5.2 Workflow for managing documents issued by CNAS (Flux de lucru pentru gestiunea documentelor eliberate de CNAS)

Workflow for drafting documents issued by CNAS through which one or more authorized users collaborate. Document elaboration in SI PS uses task-based workflows:
- document elaboration;
- forwarding document for review and approval by decision-makers;
- approval/signing of the document;
- export of the electronic document.

### 2.5.3 Workflow for managing electronic registers and documents issued by CNAS (Flux de lucru pentru gestiunea registrelor electronice și a documentelor eliberate de CNAS)

Workflows involving CNAS representatives with different roles for managing data recorded in CNAS electronic registers and documents issued by CNAS. At minimum:
- modification of register data;
- extraction/export of data from the electronic register;
- suspension of a register entry;
- removal/deactivation of a register entry;
- other workflows as needed.

### 2.5.4 Workflow for monitoring the public-service provision process (Flux de lucru pentru monitorizarea procesului de prestare a serviciilor publice)

Workflow providing tools and data necessary for monitoring CNAS activity and the activity of authorized users in SI PS. The monitoring process is based, inter alia, on implementing a mechanism of approval/rejection/distribution of draft documents or other types of records in workflows. SI PS will also provide a set of **configurable reports and performance indicators** through which the progress of public-service request processing can be tracked.

### 2.5.5 Workflow for automatic dissemination of public-interest data (Flux de lucru pentru diseminarea automată a datelor de interes public)

Workflow through which open data created in SI PS business processes are disclosed. Dissemination is to be performed through SI PS itself and through the **governmental open data portal (Portalul guvernamental de date)**.

---

# Section 3 — Functional Requirements (UC01-UC23)

CF requirement notation: each requirement is indexed `CF X.Y` where X = use case number, Y = sequence number. Obligation: **M** = Mandatory, **D** = Desired, **I** = Informative.

---

## UC01: Explorez conținut interfață publică — Explore public-interface content

**Actors:** Utilizator Internet (Internet User), Utilizator autorizat (Authorized User), Solicitant (Applicant).
**Preconditions:** None (public interface available to anonymous and authenticated users).
**Main flow:**
1. User accesses the public interface of SI PS (anonymous or authenticated).
2. User browses public categories of content: service data, depersonalized data, document metadata, statistical reports, performance indicators.
3. User defines search criteria using relevant metadata values.
4. System returns results, paginated, sortable by relevance / alphabetical / creation or update date.
5. User exports results to CSV, XLS/XLSX or PDF.
**Alternate flows / exceptions:** Overly broad search criteria → system refuses to execute and asks the user to narrow values. Abusive/inappropriate usage attempts → blocked by anti-abuse mechanisms.
**Postconditions:** Public data displayed/exported; no personal data ever returned via this interface.
**CF requirements (verbatim):**
- CF 01.01: Interfața publică a SI „Protecția Socială” va fi accesibilă pentru utilizatorii Internet și pentru utilizatorii autentificați în sistem.
- CF 01.02: Interfața publică a SI „Protecția Socială" trebuie să furnizeze acces larg utilizatorilor anonimi la datele publice din sistem. Cel puțin următoarele categorii de conținut vor fi accesibile pentru utilizatorii anonimi: date specifice aferente serviciilor publice prestate de CNAS; date depersonalizate conținute în SI „Protecția Socială"; date despre documentele eliberate de CNAS (de exemplu: certificate, extrase etc.); rapoarte statistice, indicatori de performanță; alte categorii de date relevante produse în cadrul SI „Protecția Socială".
- CF 01.03: Datele publicate prin intermediul interfeței publice SI „Protecția Socială" vor fi organizate într-o manieră ergonomică, funcțională și cuprinzătoare cu date bine organizate și informative pentru cetățeni și alte categorii de beneficiari.
- CF 01.04: SI „Protecția Socială" trebuie să ofere un mecanism flexibil și eficient pentru a defini criteriile de căutare folosind valori relevante conform metadatelor gestionate de sistem.
- CF 01.05: Rezultatele căutării vor putea fi sortate în funcție de relevanța rezultatului interogării, în ordine alfabetică sau după data creării/ultimei actualizări, etc.
- CF 01.06: În cazul formulări unor criterii de căutare prea largi, sau care necesită prea mult timp și resurse pentru execuție, SI „Protecția Socială" nu va executa aceste interogări ci va solicita utilizatorului îngustarea domeniului de valori căutate.
- CF 01.07: SI „Protecția Socială" trebuie să ofere un mecanism de paginare a rezultatelor căutării pentru a evita supraîncărcarea exploratorului Web.
- CF 01.08: SI „Protecția Socială" trebuie să ofere facilități pentru descărcarea seturilor de date aferente rezultatelor căutărilor utilizatorilor anonimi în format CSV, XLS/XLSX sau PDF.
- CF 01.09: Toate datele disponibile pentru utilizatorii anonimi prin interfața publică SI „Protecția Socială" nu trebuie să conțină date personale.
- CF 01.10: SI „Protecția Socială" trebuie să implementeze mecanisme pentru a elimina potențialele abuzuri ale utilizatorilor anonimi sau utilizare necorespunzătoare a funcționalităților furnizate de UC01.

**Notes/integrations:** Export formats: CSV, XLS/XLSX, PDF. No personal data crosses this boundary. Anti-abuse (rate limiting / throttling) required.

---

## UC02: Accesez servicii informative — Access informational services

**Actors:** Utilizator Internet, Utilizator autorizat, Solicitant.
**Preconditions:** Some sub-features require authentication (Calculatorul pensiei, Statutul cererii/deciziei, Extras din contul personal, Statutul plății prestației).
**Main flow:**
1. User selects an informational service from the public interface.
2. For anonymous services (Calculatorul vârstei de pensionare, Statutul certificatului medical, Programarea online — external link, Extrage cod CNAS), user supplies inputs and receives the result.
3. For authenticated services (Calculatorul pensiei, Statutul cererii/deciziei, Extras din contul personal de asigurări sociale, Statutul plății prestației), user authenticates first.
**Alternate flows / exceptions:** Service requires authentication but user is anonymous → redirect to login (Mpass).
**Postconditions:** Informational result displayed.
**CF requirements (verbatim):**
- CF 02.01: SI „Protecția Socială" trebuie să ofere Utilizatorilor internet, Utilizatorilor autorizați și Solicitanților accesul la funcționalitățile: "Calculatorul vârstei de pensionare", "Statutul certificatului medical", "Programarea online la CNAS" (link spre sistem extern), "Extrage cod CNAS".
- CF 02.02: SI „Protecția Socială" trebuie să ofere Utilizatorilor autorizați și Solicitanților accesul la funcționalitatea "Calculatorul pensiei".
- CF 02.03: SI „Protecția Socială" trebuie să ofere Utilizatorilor autorizați și Solicitanților accesul la funcționalitatea "Statutul cererii/deciziei".
- CF 02.04: SI „Protecția Socială" trebuie să ofere Utilizatorilor autorizați și Solicitanților accesul la funcționalitatea "Extras din contul personal de asigurări sociale".
- CF 02.05: SI „Protecția Socială" trebuie să ofere Utilizatorilor autorizați și Solicitanților accesul la funcționalitatea "Statutul plății prestației".

**Notes/integrations:** "Programarea online la CNAS" is a link to an external system. Calculatorul vârstei (retirement-age calculator), Statutul certificatului medical (medical certificate status), Extrage cod CNAS (extract CNAS code), Calculatorul pensiei (pension calculator), Statutul cererii/deciziei (application/decision status), Extras din contul personal de asigurări sociale (personal social-insurance account statement), Statutul plății prestației (benefit-payment status).

---

## UC03: Caut/vizualizez date — Search/view data

**Actors:** Utilizator autorizat (all authorized roles).
**Preconditions:** Authenticated; access is differentiated by user rights/roles and area of competence.
**Main flow:**
1. Authorized user defines complex search criteria (full text, dates, classifier values, statuses, keywords, user metadata, service metadata, applicant data, application data, contributor data, insured person data, document metadata, etc.).
2. System returns matching records across data categories (applicant, applications, electronic case files, contributors, insured persons, tasks, notifications, issued documents, workflow documents, …).
3. Results displayed paginated, sortable, with custom ordering/grouping.
4. User can trigger actions on results (open form, download attached document, approve/reject, reassign, create task, change status, electronically sign, …).
5. User exports the result table to CSV/XLS/XLSX/DOCX/PDF.
**Alternate flows / exceptions:** Overly broad search → system requests narrowing; system also suggests query modifications if result range is too wide. Diacritic and case insensitivity must produce pertinent results.
**Postconditions:** User obtains filtered view of data limited to their rights/roles; saved queries (if implemented) tied to user and shareable.
**CF requirements (verbatim):**
- CF 03.01: SI „Protecția Socială" trebuie să ofere utilizatorilor autorizați un mecanism complex de căutare și vizualizare a datelor și documentelor în întregul conținut al stocului de date. Ca rezultat al căutării, SI „Protecția Socială" va returna cel puțin următoarele categorii de date: date despre solicitant; date despre cereri de solicitări de servicii publice prestate de CNAS; date despre dosare electronic de procesare a cererilor de prestări servicii; date despre plătitorii de contribuții; date despre persoanele asigurate; date despre sarcini pe care trebuie să le execute utilizatorii; date despre notificări; date despre document eliberate de CNAS (certificate, decizii, extrase etc.); date despre documente produse în cadrul fluxurilor de lucru; alte categorii de date solicitate la etapa analizei de business.
- CF 03.02: SI „Protecția Socială" trebuie să ofere un mecanism flexibil și avansat pentru definirea criteriilor de căutare. Ca criteriu de căutare se are în vedere utilizarea următoarelor tipuri de date: full text search; date calendaristice aferente înregistrărilor în care se caută; valori ale clasificatoarelor/nomenclatoarelor; statutul înregistrărilor în care se caută; cuvinte cheie; date despre utilizatorilor autorizați care au procesat înregistrarea; date despre serviciul public prestat de CNAS; date despre solicitanți de servicii publice; date despre cererile de solicitare a serviciilor; date despre plătitori de contribuții; date despre persoanele asigurate; metadatele documentele eliberate de CNAS; alte categorii de date solicitate la etapa analizei de business.
- CF 03.03 (D): SI „Protecția Socială" ar trebui să ofere un mecanism de căutare indexată a datelor folosind platforme specializate (de exemplu, Elastic Search, Apache Solr etc.). Mecanismul de căutare a datelor ar trebui să utilizeze mijloace morfologice.
- CF 03.04: SI „Protecția Socială" va prezenta rezultatele căutării ordonate alfabetic sau după data creării/ultimei actualizări sau în funcție de relevanța rezultatului interogării formulate de utilizator (în ordinea creșterii/descreșterii relevanței înregistrărilor găsite).
- CF 03.05: Utilizatorul trebuie să poată defini criteriile de ordonare și grupare pentru lista rezultatelor căutării.
- CF 03.06 (D): SI „Protecția Socială" ar trebui să ofere funcționalități pentru a salva interogările de căutare pentru a le reutiliza ulterior. Interogările de căutare salvate ar trebui să fie legate de utilizatorul autorizat și să fie disponibile numai pentru aceștia. Utilizatorii autorizați ar trebui să poată partaja interogările de căutare salvate cu alți utilizatori.
- CF 03.07: În cazul formulării unor criterii de căutare prea largi, sau care necesită prea mult timp și resurse pentru execuție, SI „Protecția Socială" nu va executa aceste interogări ci va solicita utilizatorului îngustarea domeniului de valori căutate.
- CF 03.08: SI „Protecția Socială" trebuie să sugereze modificarea interogării dacă intervalul de rezultate este prea larg.
- CF 03.09: SI „Protecția Socială" trebuie să ofere un mecanism de paginare a rezultatelor căutării pentru a evita supraîncărcarea exploratorului Web.
- CF 03.10: SI „Protecția Socială" trebuie să afișeze în rezultate numai datele care se potrivesc cu aria de competență a utilizatorului. Accesul la date se va face diferențiat în funcție de drepturile și rolurile de care dispune utilizatorul în cadrul SI „Protecția Socială".
- CF 03.11: SI „Protecția Socială" trebuie să permită declanșarea unor procese privind rezultatele găsite sau grupul de rezultate găsite și marcate, cum ar fi: selectarea înregistrărilor rezultatelor căutării; vizualizarea detaliilor înregistrărilor găsite; descărcare directă a documentului aferent/anexat la înregistrare; deschiderea unui formular electronic aferent înregistrărilor găsite; aprobarea/respingerea; repartizarea pentru procesare către alt utilizator; crearea unei sarcini aferente înregistrării; schimbarea statutului înregistrării; semnarea electronică a formularul/documentul electronic; alte acțiuni relevante. Dacă rezultatul căutării este paginat, trebuie să fie posibil să se declanșeze procesul solicitat pentru toate înregistrările care corespund criteriilor de căutare, inclusiv pentru cele care nu sunt afișate pe pagina curentă.
- CF 03.12: Modalitățile de manipulare ulterioară a înregistrărilor/documentelor vor depinde de drepturile și rolurile de care dispune utilizatorul și de statutul în care se află înregistrarea/documentul.
- CF 03.13: Mecanismul de căutare a datelor va permite formularea de interogări sub formă de șiruri de caractere fără a se ține cont de utilizarea diacriticelor românești sau majusculelor/minusculelor furnizând rezultate pertinente interogărilor (exemplu: la căutarea șirurilor de caractere care conțin diacritice va afișa rezultate cu text fără diacritice).
- CF 03.14: Sistemul informațional trebuie să permită exportul tabelului cu rezultatele căutării în format CSV, XLS/XLSX, DOCX sau PDF.

**Notes/integrations:** Search infrastructure may use Elasticsearch / Apache Solr (desired). Diacritic-insensitive, case-insensitive, morphological search. Bulk actions on multi-page result sets must be supported.

---

## UC04: Utilizez dashboard — Use dashboard

**Actors:** Utilizator autorizat (all roles), Administrator de sistem (extended dashboard).
**Preconditions:** Authenticated.
**Main flow:**
1. User logs in; the homepage of the user UI = Dashboard.
2. Dashboard displays business-event categories relevant to the user's role: system notifications, task arrivals, workflow progress, workflow-involvement requests, items pending approval, other relevant events.
3. Events grouped as KPI indicators with aggregate values (e.g., "Unread system notifications — 20", "Documents pending approval — 41").
4. Hyperlinks on dashboard items navigate to detailed records/forms.
5. User configures dashboard look, content layout, notification preferences.
**Alternate flows / exceptions:** Administrator role sees system performance, alerts, audit reports, workflow indicators.
**Postconditions:** User has personalized landing page with KPIs and quick access to relevant work.
**CF requirements (verbatim):**
- CF 04.01: SI „Protecția Socială" va livra utilizatorilor autorizați un tablou de bord (Dashboard) pentru a organiza accesul direct la funcționalități relevante rolului cu care s-a autorizat utilizatorul și pentru a gestiona eficient activitatea acestuia (de exemplu pentru a fi notificat asupra evenimentelor de business importante, pentru a obține a accesa rapid la datele și documentele relevante utilizatorului etc).
- CF 04.02: Pot fi enumerate următoarele categorii de evenimentele de business afișate în cadrul Dashboard-ului (disponibile în funcție de rolurile și drepturile de care dispune utilizatorul autorizat SI „Protecția Socială"): notificări de sistem; notificări de parvenire a sarcinilor pe care trebuie să le execute utilizatorul; notificări privind derularea fluxurilor de lucru pe care le monitorizează utilizatorul; notificări privind necesitatea implicării utilizatorului în activitățile fluxurilor de lucru ale SI „Protecția Socială"; notificări privind documente sau procese care așteaptă aprobare de la rolurile decidente; alte evenimente relevante aferente cazurilor de utilizare ale SI „Protecția Socială".
- CF 04.03: Dashboard-ul utilizatorului SI „Protecția Socială" va afișa doar evenimente de business relevante funcționalităților și datelor disponibile drepturilor și rolurilor fiecărui utilizator autorizat în parte.
- CF 04.04: Dashboard-ul va grupa evenimentele de business afișându-le sub formă de indicatori cu valori agregate (exemplu: Notificări de sistem necitite -20; Documente spre aprobare – 41; etc.).
- CF 04.05: În calitate de Dashboard va servi pagina principală a interfeței utilizator a SI „Protecția Socială" unde vor fi amplasate toate elementele și notificările aferente utilizatorului care vor conține referință hipertext de accesare a detaliilor (înregistrările aferente).
- CF 04.06: La accesarea referinței hipertext de pe Dashboard-ul utilizatorului care duce la valori agregate sau înregistrări detaliate, SI „Protecția Socială" trebuie să asigure accesul la date detaliate legate de aceasta sau la funcționalitatea solicitată (de exemplu: vizualizarea listei sarcinilor atribuite, afișarea formularului/documentului electronic etc.).
- CF 04.07: SI „Protecția Socială" va oferi fiecărui utilizator funcționalitate de configurare individuală a aspectului și conținutului Dashboard-ului (de exemplu: configurarea preferințelor de notificare, configurarea zonelor cu conținut important la care lucrează curent utilizatorul autorizat, amplasarea categoriilor de conținut ale Dashboard-ului).
- CF 04.08: Utilizatorii autentificați (indiferent de rolurile de care dispun) vor putea să-și configureze preferințele mijloacelor de notificare.
- CF 04.09: Dashboard-ul ar trebui să afișeze indicatori cheie de performanță aferenți activității utilizator autorizat, cum ar fi: numărul de solicitări de servicii care au fost procesate într-un termen stabilit; numărul de solicitări de servicii care se află în proces de examinare; numărul total de solicitări de servicii grupate după statutul acestora (nouă, repartizată, în examinare, așteaptă aprobare, procesată etc.); alți indicatori solicitați de beneficiar.
- CF 04.10: Dashboard-ul Administratorului de Sistem trebuie să afișeze toate evenimentele de business aferente funcționării SI „Protecția Socială" (toate notificările afișate în Dashboard-ul al tuturor categoriilor de utilizatori și notificările destinate exclusiv rolului de Administrator de Sistem, ca: performanța curentă a funcționării SI „Protecția Socială"; alerte aferente funcționării sistemului; rapoarte de audit; indicatori aferenți proceselor de lucru implementate de SI „Protecția Socială"; alte date relevante.

**Notes/integrations:** Dashboard is the user's homepage. Per-user customizable. Admin dashboard is a superset including system health/audit.

---

## UC05: Execut sarcini — Execute tasks

**Actors:** Utilizator autorizat, supervisors.
**Preconditions:** Authenticated; tasks have been auto-created by workflows.
**Main flow:**
1. System auto-creates tasks based on the workflow and regulatory rules for service provision.
2. User receives task notifications via notification channel, Dashboard, and a dedicated task-management UI.
3. User uses a dedicated workspace to manage and monitor tasks: in-progress workflows, active tasks, tasks needing attention (approaching deadlines, requiring delegation), overdue tasks (showing the blocking node), other identified data categories.
4. User performs identification, prioritization, and execution of tasks.
5. System logs traceability of task execution and notifies all parties throughout the lifecycle.
**Alternate flows / exceptions:** Overdue tasks → flagged for delegation or escalation.
**Postconditions:** Tasks executed; full trace preserved; notifications dispatched at each stage.
**CF requirements (verbatim):**
- CF 05.01: SI „Protecția Socială" trebuie să implementeze funcționalități pentru ca utilizatorii autorizați să gestioneze sarcini. SI „Protecția Socială" le va crea automat în funcție de fluxul de lucru și de reglementările relevante aferente solicitării serviciilor publice prestate de CNAS.
- CF 05.02: SI „Protecția Socială" va oferi facilități de afișare a sarcinilor utilizatorului prin notificările expediate utilizatorului, Dashboard utilizatorului și prin interfața de utilizator dedicată gestiunii sarcinilor.
- CF 05.03: SI „Protecția Socială" trebuie să ofere un spațiu de lucru dedicate gestiunii și monitorizării îndeplinirii sarcinilor pentru utilizatorii implicați în procesul de executare a sarcinilor și pentru supervizorii acestora. Prin urmare, sistemul ar trebui să ofere acestor categorii de utilizatori acces la interfețe personalizabile ca: fluxuri de lucru în derulare; sarcini active; fluxuri de lucru și sarcini care necesită atenție (se apropie termenul de executare, sarcină care trebuie delegată altui utilizator etc.); sarcini care au depășit termenul limită de execuție cu indicarea verigii unde s-a blocat; alte categorii de date identificate în urma analizei de business.
- CF 05.04: SI „Protecția Socială" trebuie să implementeze funcționalități pentru a gestiona sarcinile de lucru – oferindu-le utilizatorilor autorizați funcții pentru identificarea rapidă și prioritizarea sarcinilor care le sunt atribuite.
- CF 05.05: SI „Protecția Socială" va asigura funcționalități de trasabilitate a procesului de executare a sarcinilor de către utilizatorii autorizați.
- CF 05.06: SI „Protecția Socială" va implementa funcționalități de notificate despre toate etapele executării sarcinilor pentru utilizatorilor autorizați implicați în procesele de executare ale acesteia.

**Notes/integrations:** Tasks linked to workflows; supervisor UIs; SLA/deadline indicators; full audit traceability.

---

## UC06: Depunere cerere — Submit application

**Actors:** Solicitant (Applicant), Utilizator CNAS (on behalf of Applicant).
**Preconditions:** Authenticated as Solicitant or Utilizator CNAS; service definition available.
**Main flow:**
1. Solicitant (or CNAS user on their behalf) opens the application form for the requested service.
2. System pre-fills fields from internal and external sources (RSP, RSUD, SI SFS, etc.).
3. User completes any remaining fields.
4. Form is electronically signed by the Solicitant and/or the CNAS user via **Msign**.
5. Application is submitted.
**Alternate flows / exceptions:** CNAS user submits on behalf of Solicitant (requires Mpower validation, see UC14).
**Postconditions:** Signed application registered; routed to UC07 (registration) and UC08 (examination).
**CF requirements (verbatim):**
- CF 06.01: SI „Protecția Socială" va oferi utilizatorilor cu rolurile Solicitant funcționalitatea de depunere a cererii privind solicitarea serviciului.
- CF 06.02: SI „Protecția Socială" va oferi utilizatorilor cu rolul Utilizator CNAS funcționalitatea de depunere a cererii privind solicitarea serviciului din numele Solicitantului.
- CF 06.03: La completarea cererii(formularului) de către Solicitant sau Utilizator CNAS, SI „Protecția Socială" va asigura precompletarea câmpurilor din sursele interne și externe.
- CF 06.04: Cererea va fi semnată, după caz, de către Solicitant cu semnătura sa electronică și de către Utilizatorul CNAS. În acest scop sistemul se va integra cu serviciul Msign.

**Notes/integrations:** **Msign** (electronic signature). Pre-fill from internal stores + external systems (RSP, RSUD, SI SFS, etc.).

---

## UC07: Înregistrare formular — Form registration

**Actors:** Utilizator CNAS.
**Preconditions:** CNAS user authenticated; form type has form definition + validation rules + processing rules.
**Main flow:**
1. CNAS user opens a form for registration.
2. System pre-fills fields from internal and external sources.
3. User completes remaining fields.
4. Form is electronically signed by the CNAS user via Msign (where applicable).
5. Form is registered.
**Alternate flows / exceptions:** Validation rules fail → user must correct.
**Postconditions:** Form registered with assigned form type, validation rules, and processing rules.
**CF requirements (verbatim):**
- CF 07.01: SI „Protecția Socială" va oferi utilizatorilor cu rolul Utilizator CNAS funcționalitatea de înregistrare a formularului.
- CF 07.02: Fiecărui tip de formular i se va atribui: forma formularului și regulile de validare; regulile de procesare a formularului.
- CF 07.03: La completarea formularului de către Utilizator CNAS, SI „Protecția Socială" va asigura precompletarea câmpurilor din sursele interne și externe.
- CF 07.04: Formularul va fi semnat, după caz, de către Utilizator CNAS. În acest scop sistemul se va integra cu serviciul Msign.

**Notes/integrations:** Msign for signing; pre-fill from internal + external sources.

---

## UC08: Examinare document — Document examination

**Actors:** Utilizator CNAS, Șeful direcției (Head of Department).
**Preconditions:** Application registered; eligible CNAS user available for assignment.
**Main flow:**
1. System uniformly distributes incoming applications among CNAS users (with constraint: if application was registered by a CNAS user, it cannot be assigned to the same user for examination).
2. CNAS user receives the application validation results.
3. System generates draft resulting documents (e.g., calculation sheet, decision(s)) based on the service type.
4. CNAS user may modify resulting documents or generate new decisions (with the ability to modify them).
5. CNAS user either rejects the application (generating a refusal decision) or accepts → resulting documents forwarded to Șeful direcției for approval.
6. SI PS sends a notification to the Solicitant about the examination result.
**Alternate flows / exceptions:** Application rejected → refusal decision generated. Acceptance → forward to head of department (UC10 chain).
**Postconditions:** Application examined; draft resulting documents created and routed for approval; applicant notified.
**CF requirements (verbatim):**
- CF 08.01: SI „Protecția Socială" va oferi utilizatorilor cu rolurile Utilizator CNAS funcționalitatea de examinare a documentului (cerere).
- CF 08.02: SI „Protecția Socială" va oferi un mecanism de distribuire uniformă a cererilor parvenite către Utilizatorii CNAS. În caz dacă cererea a fost înregistrată de un funcționar CNAS (Utilizator CNAS), ea nu poate fi distribuită lui pentru examinare.
- CF 08.03: SI „Protecția Socială" va oferi utilizatorilor cu rolul Utilizator CNAS rezultatele validării cererii.
- CF 08.04: SI „Protecția Socială" va genera proiectele documentelor rezultative (de exemplu: fișa de calcul, decizia sau decizii, etc) în dependență de tipul serviciului indicat în cerere.
- CF 08.05: SI „Protecția Socială" va oferi utilizatorilor cu rolul Utilizator CNAS posibilitatea modificării documentelor rezultative și/sau generarea unor noi decizii cu posibilitatea modificării lor.
- CF 08.06: SI „Protecția Socială" va permite utilizatorilor cu rolul Utilizator CNAS respingerea cererii cu generarea deciziei de refuz. În cazul acceptării, documentele rezultative se transmit pentru aprobare șefului direcției.
- CF 08.07: Despre rezultatul examinării cererii SI Protecția Socială va expedia o notificare Solicitantului.

**Notes/integrations:** Generates fișa de calcul (calculation sheet) and decizii (decisions). Forwards to Șeful direcției. Notifies applicant.

---

## UC09: Extrag rapoarte — Extract reports

**Actors:** Utilizator autorizat (all roles), Șeful CNAS, Șeful direcției, Sistem.
**Preconditions:** Authenticated; report definitions configured.
**Main flow:**
1. User selects from a standard set of configurable reports OR requests an ad-hoc report.
2. System honors user rights/roles when offering report options and source data.
3. For long-running reports, the system generates them in background and notifies the user when ready.
4. User views the report in-system or exports to DOCX or CSV/XLSX.
**Alternate flows / exceptions:** No rights → option not displayed. Long generation → background job + notification.
**Postconditions:** Report extracted, downloadable in editable format.
**CF requirements (verbatim):**
- CF 09.01: SI „Protecția Socială" trebuie să ofere funcționalități accesibile utilizatorilor autorizați pentru generarea, vizualizarea și descărcarea documentelor tipizate specifice proceselor de business implementate și a rapoartelor predefinite și ad-hoc privind conținutul informațional al SI „Protecția Socială".
- CF 09.02: SI „Protecția Socială" trebuie să pună la dispoziția rolurilor utilizatorilor sistemului un număr standard de rapoarte configurabile și trebuie să fie ușor de autorizat producerea la necesitate a rapoartelor ad-hoc.
- CF 09.03: SI „Protecția Socială" va oferi un set de rapoarte statice (de regulă implementate fizic în Conținutul sistemului informațional) destinate auditului și analizei activității utilizatorilor sistemului.
- CF 09.04: SI „Protecția Socială" va livra funcționalități de generare a rapoarte pentru ca rolurile relevante ale sistemului informațional să poată monitoriza desfășurarea proceselor de business.
- CF 09.05: SI „Protecția Socială" va ține cont de drepturile și rolurile de care dispune utilizatorul autorizat la afișarea opțiunilor de generare a rapoartelor și datele din care sunt formate acestea.
- CF 09.06: Pentru tipurile de rapoarte care necesită un timp mai mare de generare, sistemul informațional va implementa funcționalități de generare a acestora în fondal și va notifica utilizatorii relevanți atunci când rapoartele sunt gata de descărcat.
- CF 09.07: Un utilizator care vizualizează un raport în cadrul sistemului, trebuie să-l poată exporta într-un fișier extern redactabil (DOCX sau CSV/XLSX).

**Notes/integrations:** Predefined + ad-hoc reports. Background generation + notification.

---

## UC10: Aprob/resping — Approve/reject

**Actors:** Șeful direcției, Șeful CNAS (decision-maker roles).
**Preconditions:** A document is in approval state on a workflow.
**Main flow:**
1. Decision-maker receives the document for approval as part of a workflow.
2. Reviews the document.
3. Approves → electronically signs with Msign → workflow advances.
4. Rejects → workflow returns to the previous step; document returned to the user who sent it for modification per the reviewer's observations.
5. SI PS notifies authorized users involved in the workflow of approval/rejection.
**Alternate flows / exceptions:** Rejection → return to previous step.
**Postconditions:** Document approved (signed) or rejected (returned). Notifications dispatched.
**CF requirements (verbatim):**
- CF 10.01: SI „Protecția Socială" va furniza funcționalități pentru rolurile decidente (Șeful direcției, Șeful CNAS) un mecanism de aprobare sau respingere a documentelor în cadrul fluxurilor de lucru.
- CF 10.02: Fluxul de lucru va evolua în funcție de decizia utilizatorului cu rol decident.
- CF 10.03: Atunci când un proiect este respins, SI „Protecția Socială" trebuie să-l returneze automat la etapa anterioară (sa returneze documentul utilizatorului care l-a trimis spre aprobare pentru a fi modificat conform observațiilor decidentului).
- CF 10.04: SI „Protecția Socială" trebuie să notifice utilizatorii autorizați implicați în fluxul de lucru atunci când un document este aprobat sau respins.
- CF 10.05: În cazul aprobării, documentul va fi semnat de către utilizatorul cu rolul decident cu semnătura sa electronică. În acest scop sistemul se va integra cu serviciul Msign.

**Notes/integrations:** Msign for electronic signature on approval.

---

## UC11: Descarc document — Download document

**Actors:** Utilizator autorizat (per access rights), Solicitant.
**Preconditions:** Document is finalized and ready; user has download rights.
**Main flow:**
1. User is notified the document is ready for download.
2. User accesses the document.
3. Document is authenticated via Msign electronic signature (PDF automatically signed by SI PS via Msign).
4. User downloads the document (electronic) or, alternatively, receives a paper copy from a CNAS territorial subdivision.
5. System preserves all data related to issuance (document data, issuing CNAS officer, authorized representative of Solicitant, etc.).
**Alternate flows / exceptions:** No rights → access denied. Paper-track requested → routed via subdivision.
**Postconditions:** Document downloaded; issuance metadata preserved; all parties notified.
**CF requirements (verbatim):**
- CF 11.01: SI „Protecția Socială" va implementa funcționalități pentru utilizatorii autorizați de descărcare a documentelor electronice produse în cadrul fluxurilor de lucru implementate în SI „Protecția Socială" în conformitate cu drepturile de acces stabilite în sistem per tipurile de utilizatori.
- CF 11.02: SI „Protecția Socială" va permite autentificarea documentelor prin intermediul semnăturii electronice. În calitate de soluție de aplicare a semnăturii digitale va fi folosit sistemul informațional partajat Msign.
- CF 11.03 (I): Eliberarea documentelor emise de CNAS va fi realizată prin următoarele metode: în format electronic prin intermediul SI "Protecția Socială"; pe suport de hârtie de la subdiviziunile teritoriale ale CNAS.
- CF 11.04: Toate documentele produse în cadrul fluxurilor de lucru vor fi generate în conformitate cu formularele tipizate aprobate de CNAS.
- CF 11.05: SI „Protecția Socială" va implementa funcționalități de accesare și păstrare a tuturor datele aferente procesului de eliberare a documentului ca: date aferente documentului eliberat de CNAS; funcționarul CNAS care a eliberat documentul; reprezentantul Solicitantului care este împuternicit pentru a ridica documentul; alte categorii de date.
- CF 11.06: Autentificarea documentelor electronice, inclusiv a datelor, eliberate de CNAS implică semnarea digitală a fișierelor PDF (fișierul trebuie să fie semnat automat de SI „Protecția Socială" prin intermediul sistemul informațional partajat Msign).
- CF 11.07: SI „Protecția Socială" va notifica toate părțile interesate atunci când documentul este gata pentru descărcare.

**Notes/integrations:** Msign (PDF signing). Two delivery channels: electronic via SI PS; paper at territorial subdivisions. CNAS official, representative metadata preserved.

---

## UC12: Explorez registru — Explore register

**Actors:** Utilizator autorizat.
**Preconditions:** Authenticated; user has rights to the target register.
**Main flow:**
1. User opens a register view.
2. System provides search by register fields and filtering by field values.
3. User exports search/filter results to XLS, CSV or PDF.
4. User opens a record (double-click) → predefined tabbed interface per register type opens, with the ability to launch register-specific processes (e.g., start a service, generate a report).
5. Each tab is independently exportable to XLS, CSV or PDF.
**Alternate flows / exceptions:** None specific.
**Postconditions:** User has viewed/exported register data; may have launched downstream processes.
**CF requirements (verbatim):**
- CF 12.01: SI „Protecția Socială" va oferi o interfață de vizualizare a Registrelor formate de sistem. Această interfață va oferi instrumente de căutare a înregistrărilor după câmpurile din registru și de filtrare după valorile câmpurilor. Rezultatele căutării sau filtrării la fel vor putea fi exportate într-un fișier XLS, CSV sau PDF.
- CF 12.02: La accesarea înregistrării din registrul sau din rezultatul căutării (dublu click), sistemul va oferi o interfață cu o serie de file predefinite pentru fiecare tip de registrul. Din cadrul interfeței pot fi lansate procese aferente tipului Registrului (de exemplu: startarea serviciului, generarea rapoartelor, etc). Pentru orice filă din această interfață va fi prevăzută posibilitatea de exportare într-un fișier XLS, CSV sau PDF.

**Notes/integrations:** Export formats: XLS, CSV, PDF. Tabbed per-record view per register type.

---

## UC13: Gestionez profilul solicitantului — Manage applicant profile

**Actors:** Utilizator CNAS, Solicitant (via self-service forms), Sistem (via external data exchange).
**Preconditions:** Two applicant profile categories: natural person; legal entity.
**Main flow:**
1. System manages applicant profiles for natural persons and legal entities.
2. Profile contains identification + contact data (legal entity data, natural person data, legal entity representative data, contact data, service applications and their statuses, documents issued by CNAS, other identified data).
3. Profile management uses three strategies:
   - dedicated SI PS UI to add/update/deactivate profile data;
   - electronic service-request forms during workflows;
   - external data exchange (RSP, RSUD, SI SFS, etc.).
4. System never deletes a profile that has at least one DB record using its identifier.
**Alternate flows / exceptions:** Profile deletion attempted while in use → blocked.
**Postconditions:** Profile created/updated/deactivated; references preserved.
**CF requirements (verbatim):**
- CF 13.01: SI „Protecția Socială" trebuie să furnizeze funcționalități necesare pentru gestionarea a două categorii de profiluri de Solicitanți de servicii, asigurând gestionarea datelor specifice acestora: persoane fizice; entități juridice.
- CF 13.02: Profilul Solicitantului trebuie să conțină toate datele de identificare și de contact ale entității pe care o descrie, ca: date despre entitatea juridică; date despre persoană fizică; date despre reprezentantul entității juridice; date de contact; date despre cererile de solicitare a serviciilor electronice și statuturile lor; date despre documente eliberate de CNAS; alte categorii de date identificate în urma analizei de business.
- CF 13.03: SI „Protecția Socială" trebuie să ofere funcționalități pentru gestionarea profilurilor Solicitanților de servicii folosind trei strategii: prin funcționalități dedicate SI „Protecția Socială" pentru a adăuga/actualiza/dezactiva date aferente profilurilor; prin formulare electronice de solicitare a serviciilor publice prestate de CNAS în cadrul fluxurilor de lucru; prin funcționalitatea de schimb de date cu sisteme externe (datele de profil pot fi preluate din RSP, RSUD, SI SFS etc.).
- CF 13.04: SI „Protecția Socială" nu trebuie să permită ștergerea niciunui profil dacă există cel puțin o înregistrare a bazei de date în care este utilizat identificatorul de profil.

**Notes/integrations:** External systems: RSP (Registrul de stat al populației), RSUD (Registrul de stat al unităților de drept), SI SFS (sistem fiscal). Soft-delete only when referenced.

---

## UC14: Schimb date cu sisteme externe — Exchange data with external systems

**Actors:** Sistem; Administrator de sistem (manual sync); all external information systems.
**Preconditions:** Mconnect platform available; APIs exposed.
**Main flow:**
1. SI PS exchanges data with external systems via **Mconnect** (interoperability platform), publishing/consuming events via **MconnectEvents** for proactive services.
2. If Mconnect cannot be used, integration goes through APIs exposed by the external subsystems.
3. SI PS exposes its own APIs (per Annex 4) for external systems via Mconnect.
4. Automatic + manual data synchronization functionality is provided.
5. Automated planned synchronization occurs during low-load hours of SI PS and partner systems.
**Alternate flows / exceptions:** Mconnect unavailable → fallback to direct APIs.
**Postconditions:** Data synchronized with external systems; events published/consumed.
**CF requirements (verbatim):**
- CF 14.01: SI „Protecția Socială" trebuie dezvoltat pe baza unei arhitecturi capabile să implementeze facilități de interoperabilitate cu sisteme informaționale externe.
- CF 14.02: SI „Protecția Socială" trebuie să asigure schimbul de date cu sistemele informaționale externe prin intermediul platformei de interoperabilitate Mconnect. Toate componentele informatice din perimetrul SI „Protecția Socială" vor furniza și consuma servicii Web destinate comunicării cu sisteme informaționale externe pentru efectuarea schimbului reciproc de date. Pentru publicarea și consumul evenimentelor, în vederea realizării serviciilor proactive se va utiliza componenta MconnectEvents (Overview – egov4dev).
- CF 14.03: În cazul în care platforma de interoperabilitate Mconnect nu poate fi utilizată pentru integrarea cu sistemele informaționale externe, integrarea cu SI „Protecția Socială" se va face prin intermediul API-urilor expuse de aceste subsisteme informaționale.
- CF 14.04: SI „Protecția Socială" trebuie integrat cu următoarele sisteme externe: Sistemului informațional automatizat „Registrul de stat al unităților de drept" – pentru accesarea datelor despre persoane juridice (RSUD); Sistemului informațional automatizat „Registrul de stat al populației" – pentru accesarea datelor despre persoane fizice (RSP); Sistemul informațional automatizat al Serviciului Fiscal de Stat (SIA SFS); Sistemul informațional „Determinarea dizabilității și capacității de muncă" (SIDDCM); Portalul certificatelor de concediu medical (PCCM); Sistemul informațional „Constatarea medicală a nașterii și a decesului" (eCMND); Sistemul informațional automatizat de Înregistrare cu Statut de Șomer (SIAÎSȘ); Sistemul informațional automatizat „Vulnerabilitatea Energetică" (SIVE); Sistemul Informațional Automatizat „Asistența Socială" (SIAAS); Sistemul informațional financiar al CNAS (FMS); Electronic Exchange of Social Security Information (EESSI); Alte sisteme informaționale identificate la etapa proiectării sistemului.
- CF 14.05: SI „Protecția Socială" trebuie integrat cu sistemul informațional partajat Mpass în vederea implementării procedurii de autentificare a utilizatorilor prin intermediul semnăturii electronice sau semnăturii mobile.
- CF 14.06: SI „Protecția Socială" trebuie integrat cu sistemul informațional partajat Msign în scopul implementării procedurilor de semnare electronică a documentelor.
- CF 14.07: SI „Protecția Socială" trebuie integrat cu sistemul informațional partajat Mnotify în scopul notificării utilizatorilor autorizați în legătură cu evenimentele de business aferente proceselor implementate în SI „Protecția Socială".
- CF 14.08: SI „Protecția Socială" trebuie integrat cu sistemul informațional partajat Mlog în scopul jurnalizării evenimentelor de business critice.
- CF 14.09: SI „Protecția Socială" trebuie integrat cu sistemul informațional partajat Mpay în scopul achitării contribuții calculate/restante la BASS.
- CF 14.10: SI „Protecția Socială" trebuie integrat cu sistemul informațional partajat Mpower în scopul validării împuternicirilor de reprezentare de către persoanele fizice și persoanele juridice.
- CF 14.11: SI „Protecția Socială" trebuie integrate cu Portalul guvernamental de date în scopul publicării seturilor de date cu caracter public (date din registrele oficiale ale CNAS, indicatori de performanță, statistici, rapoarte etc.) produse în cadrul fluxurilor de lucru ale SI „Protecția Socială".
- CF 14.12: SI „Protecția Socială" va implementa și expune API-urile necesare prin intermediul Mconnect pentru a furniza date sistemelor informaționale externe. Lista API-urilor este menționat în Anexa 4 la prezentul document.
- CF 14.13: SI „Protecția Socială" va furniza funcționalitate automată și manuală de sincronizare a datelor/documentelor cu sisteme informaționale externe.
- CF 14.14: SI „Protecția Socială" va dispune de mecanism de sincronizare și preluare planificată automată a datelor din surse externe în orele de minimă solicitare a SI „Protecția Socială" și sistemele informaționale partenere.

**Notes/integrations:** External business systems: RSUD, RSP, SIA SFS, SIDDCM, PCCM, eCMND, SIAÎSȘ, SIVE, SIAAS, FMS, EESSI. Shared MGov services: Mconnect, MconnectEvents, Mpass, Msign, Mnotify, Mlog, Mpay, Mpower. Open data: Portalul guvernamental de date. APIs listed in **Annex 4**. BASS = Bugetul Asigurărilor Sociale de Stat.

---

## UC15: Configurez serviciu electronic — Configure electronic service

**Actors:** Administrator de sistem (administrative roles).
**Preconditions:** Authenticated as administrator.
**Main flow:**
1. Admin manages system settings and operational parameters for configuring electronic services.
2. Admin edits the **Pașaportul serviciului** (service passport): description, application conditions, examination procedure and terms, workflow-specific configurations for application examination.
3. Admin defines business rules for service eligibility criteria (e.g., applicant type — natural/legal person, and other validation rules identified in business analysis).
4. Configuration changes do not affect applications already in examination.
**Alternate flows / exceptions:** Config change attempted on a live service → does not break in-flight applications.
**Postconditions:** Service passport updated; new applications use new configuration.
**CF requirements (verbatim):**
- CF 15.01: SI „Protecția Socială" trebuie să permită rolurilor administrative să gestioneze setări de sistem și parametrii de funcționare în vederea configurării serviciilor electronice prestate de CNAS.
- CF 15.02: Administratorul sistemului informațional va dispune de funcționalități necesare pentru redactarea pașaportul serviciului public. Pașaportul va conține date cu privire la descrierea serviciului, condiții de aplicare, procedura și termenii de examinare a cererii, alte configurări specifice fluxurilor de lucru de examinare a cererilor de prestări servicii etc.
- CF 15.03: SI „Protecția Socială" va oferi funcționalități pentru definirea regulilor de business în vederea stabilirii criteriilor de solicitare a serviciul electronic prestat de CNAS, cu ar fi: tipul Solicitantului (persoane fizică/juridică); alte reguli de validare identificated în urma analizei de business.
- CF 15.04: Modificarea parametrilor de configurare a serviciului electronic nu trebuie să afecteze procesarea cererilor care sunt în proces de examinare.

**Notes/integrations:** Service passport (Pașaportul serviciului) — see Section 2.3 informational object. Versioning required (changes do not retroactively affect in-flight cases).

---

## UC16: Configurez flux de lucru — Configure workflow

**Actors:** Administrator (administrative role).
**Preconditions:** Authenticated as administrator.
**Main flow:**
1. Admin visually configures workflows for all process scenarios via the system UI.
2. Defines states + steps (transitions) for an electronic form.
3. No upper limit on number of steps in a workflow; system is adaptable to methodology changes.
4. Defines sequential or parallel activity collections.
5. Creates predefined task lists for processing an electronic form within a workflow (based on current CNAS service-provision regulations).
6. Workflow described with: states; deadline per step; performer (preferably by role); workflow start point.
7. Defines business rules for: workflow start; state transitions; workflow completion.
8. System records and displays: steps already traversed; receipt date in each step; actors (roles, external systems, named persons); completion date per step; messages exchanged; current step (traceability); SLA deadline.
9. Admin configures access rights by role, service or workflow-specific conditions.
10. Admin assigns permissions to individual users so they can reassign tasks (delegation in case of leave/medical/etc.).
11. Workflow participants: users, user groups, roles.
12. Admin configures electronic forms (states, transitions).
13. Admin configures notification strategy for workflow business events.
**Alternate flows / exceptions:** Performer unavailable → delegation per CF 16.11.
**Postconditions:** Workflow definition saved and active for new instances.
**CF requirements (verbatim):**
- CF 16.01: SI „Protecția Socială" va furniza mecanism de configurare a fluxurilor de lucru pentru toate scenariile aferente proceselor de lucru și prelucrare a formularelor electronice perfectate în cadrul acestor procese.
- CF 16.02: SI „Protecția Socială" va oferi utilizatorilor cu rol Administrator un mecanism de adăugare, configurare și administrare în manieră vizuală a fluxurilor de lucru. Gestiunea fluxurilor de lucru trebuie să se poată realiza folosind interfața utilizator a sistemului informațional.
- CF 16.03: Fluxurile de lucru trebuie să poată fi definite prin specificarea stărilor în care poate trece un formular electronic și pașii de procesare (etapele sau tranzițiile de evoluție a fluxului de lucru) realizați atât de utilizatori cu roluri specifice.
- CF 16.04: Numărul de pași ce pot fi incluși într-un flux nu trebuie să fie limitat. În așa fel sistemul informațional vor fi adaptabile modificărilor metodologiei de lucru cu documentele procesate în cadrul procedurilor de gestiune și evidență a proceselor de prestări servicii publice de către CNAS.
- CF 16.05: Un flux de lucru trebuie să poată fi proiectat ca o colecție de activități ce se desfășoară fie secvențial, fie în paralel.
- CF 16.06: SI „Protecția Socială" trebuie să ofere funcționalități pentru a crea liste predefinite de sarcini pentru procesarea formularului electronic în cadrul unui flux de lucru. Această listă de sarcini se va baza pe reglementările actuale privind prestarea serviciilor publice de către CNAS.
- CF 16.07: Un flux de lucru trebuie să fie descris prin cel puțin următoarele informații: stările prin care pot trece formularele electronice și documentele ce sunt lansate spre procesare pe fluxul respectiv; termenul limită de procesare pentru fiecare etapă a fluxului de lucru; cine realizează activitatea – poate fi și nominal, dar se va urmări specificarea unor roluri în cadrul sistemului informațional, pentru generalizare; punctul de pornire a activităților ce se vor desfășura pe fluxul de lucru respectiv.
- CF 16.08: SI „Protecția Socială" va oferi posibilitatea definirii regulilor de business care trebuie satisfăcute pe parcursul fluxului de lucru. Aceste se vor referi cel puțin la următoarele etape: lansarea fluxului de lucru; trecerea la o altă etapă (statut) a fluxului de lucru; încheierea fluxului de lucru.
- CF 16.09: SI „Protecția Socială" va înregistra și prezenta utilizatorilor cel puțin următoarele informații privind fluxul de lucru: pașii din fluxul de lucru prin care a trecut pană la momentul respectiv; data primirii documentului în fiecare dintre pașii de procesare; actorii implicați în fluxul de lucru, care pot fi roluri în cadrul sistemului informațional, sisteme informaționale externe, cât și persoane nominalizate explicit; data finalizării fiecăruia dintre pașii de procesare a fluxului de lucru; mesajele transmise de către utilizatori pe parcursul procesării; pasul la care se află documentul la momentul respectiv pe fluxul de lucru (trasabilitatea parcursului documentului); termenul limită de rezolvare a sarcinii.
- CF 16.10: SI „Protecția Socială" trebuie să ofere un mecanism pentru configurarea drepturilor de acces în funcție de rolurile utilizatorilor, serviciul electronic furnizat de CNAS sau condițiile specifice aferente fluxului de lucru.
- CF 16.11: Rolurile administrative trebuie să dispună de funcționalitate de alocare permisiuni pentru utilizatori individuali, astfel încât aceștia să poată realoca sarcini/acțiuni într-un flux de lucru pentru un alt utilizator sau grup de utilizatori. Acest lucru este util în cazul indisponibilității executantului (concediu medical, delegare, concediu anual etc.) și este necesară delegarea sarcinilor de lucru altor utilizatori.
- CF 16.12: SI „Protecția Socială" trebuie să recunoască în calitate de participanți ai fluxului de lucru atât utilizatorii și grupurile de utilizatori, cât și roluri în sistemul informațional.
- CF 16.13: SI „Protecția Socială" va oferi mecanism de configurare a formularelor electronice necesare perfectării documentelor aferente gestiunii și evidenței proceselor de prestare a serviciilor publice de către CNAS (configurarea stărilor și tranzițiilor acestora).
- CF 16.14: SI „Protecția Socială" trebuie să permită configurarea strategiei de notificare a utilizatorilor relevanți pentru evenimentele de business generate de fluxul de lucru.

**Notes/integrations:** Visual workflow designer in UI. Supports sequential + parallel branches. Role-based and user-based delegation. Notification strategy configurable per workflow.

---

## UC17: Gestionez metadate și șabloane de documente — Manage metadata and document templates

**Actors:** Administrator, Sistem (auto-update from external sources).
**Preconditions:** Authenticated as administrator.
**Main flow:**
1. Admin manages nomenclatures, classifiers, and other metadata categories: official national classifiers; interoperability classifiers; internal classifiers/nomenclatures.
2. System imports official BNS classifiers (CAEM Rev.2, CUATM, CFOJ, CFP, NCM, etc.) where needed; modification rights restricted to administering institution.
3. System uses + manages interoperability metadata; auto-updates from external systems.
4. Admin dynamically defines and administers system nomenclatures + metadata.
5. Lifecycle support for each metadata category (activation, deactivation, validity start/end dates).
6. Only currently active values shown in selection lists; old values used only for historical reports.
7. Cannot delete a metadata category that is in use.
8. Manages the system of metadata + reference information: system configurations; parameters/constants; register configurations and code-assignment principles; official national nomenclatures (CUATM, CFP, CFOJ, CAEM, NCM); internal nomenclatures for documents elaborated/examined by CNAS; CNAS-specific nomenclatures and classifiers; other metadata.
9. Configures document templates with predefined structure for documents extracted from the data store.
10. Flexible aspect modification — both graphical layout and content.
11. Configures content via **balize (tags/placeholders)** populated at generation time.
12. Configures and implements templates for all CNAS service-provision documents: service-request application; electronic documents produced during workflows; system notifications; other workflow-related templates.
13. Per template type configures: template (with graphics), content data, print parameters/configs, validation rules for printed data.
14. Provides translation of metadata values + template labels in **Romanian, English, Russian**.
15. Manual XML or CSV import/export of metadata.
16. Versioning of metadata values and document templates.
17. Each service type is assigned: application form + validation rules; mandatory attachment list; receipt form; resulting document form; calculation sheet (for benefits/prestații); calculation formulas (for benefits); application processing rules; print form of the resulting document.
**Alternate flows / exceptions:** Attempt to delete used metadata → blocked. Attempt to edit official classifier without authority → blocked.
**Postconditions:** Metadata, templates, and service definitions configured and versioned.
**CF requirements (verbatim):**
- CF 17.01: SI „Protecția Socială" va dispune de mecanism de gestiune a nomenclatoarelor, clasificatoarelor și altor categorii de metadate destinate configurării sistemului și gestiunii proceselor de business ale activității CNAS.
- CF 17.02: Următoarele categorii de metadate trebuie utilizate în cadrul SI „Protecția Socială": clasificatoare naționale oficiale, a căror valori sunt gestionate de autoritățile publice specifice și adoptate de toate autoritățile publice din Republica Moldova; clasificatoare/nomenclatoare de interoperabilitate care valori sunt folosite pentru a face schimb de date cu sisteme informaționale terțe; clasificatoare/nomenclatoare interne valorile cărora vor fi folosite pentru buna funcționare a sistemului informațional (de exemplu, variabile de sistem, parametrii de configurare a sistemului, clasificatoare specifice fluxurilor de lucru implementate, roluri, evenimente specifice, surse de date etc.).
- CF 17.03: Vor fi preluate integral, în caz de necesitate, clasificatoare gestionate de Biroul Național de Statistică (CAEM Rev.2, CUATM, CFOJ, CFP, NCM, etc.) și alte clasificatoare oficiale gestionate de APC și APL din Republica Moldova cu care sistemul informațional va interacționa.
- CF 17.04: Pentru clasificatoarele oficiale se vor limita drepturile de efectuare a modificărilor. Pentru această categorie de clasificatoare vor fi efectuate modificări doar în cazul când acestea vor fi operate de instituția care le administrează.
- CF 17.05: SI „Protecția Socială" va fi capabil să folosească și gestioneze valori ale metadatelor de interoperabilitate cu sisteme informaționale terței. Dezvoltatorul trebuie să implementeze un mecanism de actualizare automată a metadatelor importate din sisteme informaționale externe.
- CF 17.06: Pentru sistemul de nomenclatoare și metadate de sistem, sistemul informațional va livra mecanism de definire și administrare dinamică a acestora.
- CF 17.07: Pentru fiecare categorie de metadate, sistemul informațional va permite definirea ciclului de viață a acesteia (activarea, dezactivarea, stabilirea datei de când devine activă categoria de metadate, stabilirea datei de când categoria de metadate își pierde valabilitate).
- CF 17.08: SI „Protecția Socială" va propune selecția în liste doar ale valorilor curent active ale metadatelor (valorile vechi vor fi ascunse și se vor utiliza doar la generarea rapoartelor de analiză unde figurează în calitate de valori ale datelor).
- CF 17.09: SI „Protecția Socială" nu va permite suprimarea unei categorii de metadate dacă aceasta este utilizată cel puțin într-o înregistrare a bazei de date.
- CF 17.10: SI „Protecția Socială" va fi capabil să gestioneze sistemul de metadate și informație de referință care cuprinde: configurații de sistem; parametri și constante necesare funcționării sistemului informațional; configurații de registre și principii de atribuire a codurilor de înregistrare pentru fiecare registru; nomenclatoare și clasificatoare oficiale ale Republicii Moldova (CUATM, CFP, CFOJ, CAEM, NCM etc.); nomenclatoare și dosare specifice documentelor elaborate și examinate în cadrul CNAS; nomenclatoarele și clasificatoare caracteristice activității CNAS; alte categorii de metadate specifice activității CNAS.
- CF 17.11: SI „Protecția Socială" va oferi mecanisme de configurare și implementare a șabloanelor de documente aferente actelor generate în baza formularelor electronice perfectate. Șabloanele documentelor vor reprezenta documente tipizate cu o structură bine definită și vor fi extrase în baza stocului de date.
- CF 17.12: SI „Protecția Socială" va oferi un mecanism flexibil de modificare a aspectului documentului extras, atât pentru partea grafică cât și conținutul acestuia.
- CF 17.13: Configurarea conținutului documentelor generate de sistemul informațional pe baza șabloanelor va fi posibilă prin inserarea unui set de balize prin intermediul cărora va fie asigurată popularea conținutului documentului cu date în timpul generării acestuia.
- CF 17.14: Dezvoltatorul va configura și implementa șabloane pentru generarea tuturor documentelor specifice proceselor de prestare a serviciilor publice de către CNAS: cerere de solicitare a serviciului; documente electronice produse în cadrul fluxurilor de lucru de prestare a serviciilor publice; notificări de sistem; alte șabloane de documente aferente fluxurilor de lucru SI „Protecția Socială" ce corespund capabilităților ce urmează să le configureze și implementeze.
- CF 17.15: Pentru fiecare tip de document extras din sistemul informațional și implementat în baza unui șablon tipizat va fi posibil de configurat următoarele: șablonul documentului (care va conține și elementele grafice); datele de conținut ale documentului; parametri și configurații pentru tipar; reguli de validare pentru datele imprimate pe document.
- CF 17.16: SI „Protecția Socială" trebuie să ofere un mecanism de traducere a valorilor metadatelor a și etichetelor de pe șabloanele de documente în limbile română, engleză și rusă.
- CF 17.17: SI „Protecția Socială" trebuie să ofere funcționalități de import/export manual de date aferent sistemului de metadate implementat în format XML sau CSV.
- CF 17.18: SI „Protecția Socială" trebuie să ofere funcționalități pentru versiunea valorilor metadatelor și șabloanelor de documente.
- CF 17.19: Fiecărui tip serviciu i se va atribui: forma cererii și regulile de validare; lista documentelor obligatorii pentru atașare la cerere (după caz); forma recipisei (după caz); forma documentului(lor) rezultativ(e) (după caz); fișa de calcul (pentru prestații); formulele de calcul (pentru prestații); regulile de procesare a cererii; formă de tipar a documentului rezultativ (după caz).

**Notes/integrations:** External classifiers: BNS (Biroul Național de Statistică) — CAEM Rev.2, CUATM, CFOJ, CFP, NCM. Trilingual UI: RO/EN/RU. Per-service config = form, validation, required attachments, receipt, resulting doc, calc sheet, calc formulas, processing rules, print form. Document templates use placeholder tags ("balize").

---

## UC18: Administrez utilizatori și controlul accesului — Administer users and access control

**Actors:** Administrator de sistem.
**Preconditions:** Authenticated as administrator.
**Main flow:**
1. Admin dynamically defines and manages roles, user groups, users, and rights.
2. Role = generic name + brief description + active/disabled status; user can have multiple roles; disabled roles hidden from configuration UIs.
3. Roles specify the UI functionalities available to the authorized user.
4. User groups managed similarly (name, description, status). Cannot delete a group if attached to any user.
5. Defines user access rights by criteria: geography, document categories, available workflow categories, organizational subdivision, other relevant criteria.
6. Admin blocks/unblocks user accounts.
7. User account can be physically deleted only if no journaled events were produced by it and no data was introduced by it or linked to it.
8. Granular access management for user interfaces, specific actions, workflows, and business events generated by them.
9. Access rights granted at three levels: explicit user, user group, role. Group can contain subgroups/roles. User can belong to multiple groups/roles. Rights determined cumulatively.
10. Permissions categories per business event: view, add, modify, status change, generate/download documents, others.
11. Functions accessible only after successful authentication.
12. UI and content displayed only per user/group rights.
13. Authorization principle: **"everything not permitted is forbidden"** (deny by default).
**Alternate flows / exceptions:** Attempt to delete user with audit history → blocked (must use deactivation).
**Postconditions:** Roles, groups, users configured; access enforced.
**CF requirements (verbatim):**
- CF 18.01: SI „Protecția Socială" va dispune de un mecanism de definire și gestiune dinamică a rolurilor, grupurilor de utilizatori, utilizatorilor și drepturilor acestora.
- CF 18.02: SI „Protecția Socială" va furniza mecanism de gestiune a rolurilor. Un rol este definit prin denumire generică, descriere succintă și statutul de activ/dezactivat. Un utilizator poate avea atașate mai multe roluri. Rolurile dezactivate nu vor fi afișate la configurarea drepturilor de acces la resursele aplicației sau a drepturile utilizatorilor.
- CF 18.03: Rolurile specifică funcționalitățile interfeței utilizator la care are acces utilizatorul autorizat.
- CF 18.04: SI „Protecția Socială" va furniza mecanism de gestiune a grupurilor de utilizatori. Un grup de utilizator este definit prin denumire generică, descriere succintă și statutul de activ/dezactivat.
- CF 18.05: SI „Protecția Socială" nu va permite suprimarea unui grup de utilizatori dacă acesta este atașat măcar unui utilizator.
- CF 18.06: SI „Protecția Socială" va furniza mecanism de definire pentru utilizatori a drepturilor de acces la date în funcție de criterii geografice, categorii de documente, categorii de fluxuri de lucru disponibile, subdiviziune în care activează, alte criterii relevante.
- CF 18.07: SI „Protecția Socială" va permite blocarea/deblocarea accesului utilizatorului.
- CF 18.08: Un cont de utilizator poate fi suprimat fizic doar în cazul când nu există evenimente jurnalizate produse de utilizatorul suprimat sau date introduse de acesta sau legate de contul utilizator.
- CF 18.09: SI „Protecția Socială" trebuie să permită o gestionare granulară a drepturilor de acces la interfețele utilizator, acțiuni specifica furnizate de acestea, fluxuri de lucru și evenimentele de business generate de acestea.
- CF 18.10: SI „Protecția Socială" trebuie să permită acordarea drepturilor de acces la nivel explicit de utilizator, grup de utilizatori și rol. Un grup de utilizatori poate cuprinde mai multe subgrupuri/roluri. Un utilizator poate fi asociat cu unul sau mai multe grupuri și roluri, iar drepturile de acces ale utilizatorului sunt determinate cumulativ.
- CF 18.11: Mecanismul de administrare a drepturilor și rolurilor utilizatorilor va permite formularea principiilor de acces la interfața utilizator și conținutul informațional al sistemului informațional pentru fiecare utilizator în parte sau grup de utilizatori (toți utilizatorii aferenți grupului de utilizatori).
- CF 18.12: SI „Protecția Socială" va putea defini permisiunile în cadrul evenimentelor de business disponibile utilizatorilor cu acces la componentele interfeței utilizator. Cel puțin configurarea următoarelor categorii de permisiuni disponibile utilizatorilor trebuie implementată: vizualizare înregistrări; adăugare înregistrări; modificare înregistrări; schimbare statut înregistrare; generare și descărcare documente; alte acțiuni relevante.
- CF 18.13: SI „Protecția Socială" trebuie să permită accesarea funcțiilor specifice numai după autentificarea cu succes a utilizatorului.
- CF 18.14: SI „Protecția Socială" va afișa interfața utilizator și conținutul informațional doar în baza drepturilor/rolurilor de care dispun utilizatorii și grupului de utilizatori din care aceștia fac parte.
- CF 18.15: Metoda de autorizare implementată în sistemul informațional trebuie să se bazeze pe principiul „tot ce nu este permis este interzis".

**Notes/integrations:** RBAC + ABAC (geography, subdivision, document, workflow filters). Deny-by-default. Soft-delete pattern for users with audit history.

---

## UC19: Generez rapoarte și documente — Generate reports and documents

**Actors:** Utilizator autorizat (relevant roles).
**Preconditions:** Authenticated; report platform configured.
**Main flow:**
1. Authorized user generates reports for monitoring business processes.
2. Generates typed documents in electronic format per CNAS-approved standard forms.
3. All workflow-produced documents follow CNAS-approved templates.
4. Uses the full data collection: nomenclatures and classifiers; DB records; user activity; access/security permissions.
5. ETL process uses metadata values valid for the selected period to ensure coherence/relevance.
6. Implementation via specialized reporting platform (Jasper Reports, Pentaho Reporting, BIRT, FineReport, etc.).
7. Developer integrates **up to 150 reports** into the system content.
8. Long-running reports → background generation + user notification when ready.
**Alternate flows / exceptions:** Background generation only triggered for slow reports.
**Postconditions:** Report/document generated; user notified.
**CF requirements (verbatim):**
- CF 19.01: SI „Protecția Socială" va livra funcționalități de generare a rapoarte pentru ca rolurile relevante ale sistemului informațional să poată monitoriza desfășurarea proceselor de business modelate și să se asigura că sistemul informațional este utilizat în condiții optime.
- CF 19.02: SI „Protecția Socială" va livra funcționalități de generare a documentele tipizate eliberate de CNAS în format electronic în conformitate cu formularele tipizate aprobate de CNAS.
- CF 19.03: Toate documentele produse în cadrul fluxurilor de lucru implementare în SI „Protecția Socială" trebuie să fie generate în conformitate cu formularele tipizate aprobate de CNAS.
- CF 19.04: Pentru extragerea rapoartelor și documentelor, SI „Protecția Socială" va face uz de întreaga colecție de date disponibilă, incluzând: nomenclatoarele și clasificatoarele; înregistrări ale bazei de date; activitatea utilizatorului autorizat; permisiunile de acces și securitate.
- CF 19.05: În procesul de generare a rapoartelor de analiză și vizualizarea datelor prelucrate anterior, sistemul informațional va utiliza valorile metadatelor aferente perioadei selectate de date, asigurând astfel coerența și relevanța informațiilor extrase în cadrul etapelor de extragere, transformare și încărcare (ETL)." Această formulare reflectă integrarea metadatelor în fluxul ETL, care susțin gestionarea corectă a datelor pentru raportare și analiza lor conform perioadei vizate.
- CF 19.06: Dezvoltatorul va implementa mecanismul de raportare prin intermediul unei platforme specializate de configurare și implementare a rapoartelor (de exemplu: Jasper Reports, Pentaho Reporting, Birt, FineReport, etc.). Accesul la rapoartele configurate se va asigura prin intermediul interfeței utilizator.
- CF 19.07: Dezvoltatorul va integra în conținutul sistemul informațional până la 150 de rapoarte.
- CF 19.08: Pentru tipurile de rapoarte care necesită un timp mai mare de generare, sistemul informațional va implementa funcționalități de generare a acestora în fondal și va notifica utilizatorii relevanți atunci când rapoartele sunt gata de descărcat.
- CF 19.09: SI „Protecția Socială" va notifica utilizatorii relevanți atunci când rapoartele generate în fondal sunt gata.

**Notes/integrations:** Reporting platforms: Jasper Reports / Pentaho / BIRT / FineReport. Target: ~150 reports. ETL with period-aware metadata.

---

## UC20: Execut proceduri automate — Execute automated procedures

**Actors:** Sistem, Administrator de sistem.
**Preconditions:** Procedure schedules and parameters preconfigured.
**Main flow:**
1. System auto-executes routine procedures to ensure efficient operations.
2. Procedures compute aggregate values for complex statistical reports and monitoring KPIs (pre-computed for later access).
3. System launches procedures per scheduled itinerary + preset parameters; dispatches notifications per the notification strategy.
4. Automatically generates tasks for authorized users per workflow configuration.
5. Reassigns non-destinatar tasks (where initial assignee refused or delayed).
6. Implements automated procedures to receive data from external systems.
7. Provides UI to view active automated procedure state.
8. Admin can configure + manually start any automated procedure per preset parameters.
**Alternate flows / exceptions:** Task assignee refuses/delays → automatic reassignment.
**Postconditions:** Procedures executed; aggregates available; tasks generated/reassigned; data ingested.
**CF requirements (verbatim):**
- CF 20.01: SI „Protecția Socială" trebuie să ofere un mecanism de executare automat a procedurilor de rutină pentru a asigura derularea eficientă a proceselor de lucru și funcționarea în condiții optime a sistemului.
- CF 20.02: SI „Protecția Socială" va implementa proceduri automate pentru calcularea valorilor agregate aferente rapoartelor statistice complexe sau a KPI-urilor de monitorizare (indicatorii și rapoartele complexe vor fi generate în prealabil pentru a fi accesate ulterior la nevoie).
- CF 20.03: SI „Protecția Socială" trebuie să lanseze automat procedurile conform itinerarului stabilit și parametrilor presetați și să trimită notificări utilizatorilor/rolurilor relevante în conformitate cu strategia de notificare.
- CF 20.04: SI „Protecția Socială" trebuie să genereze automat sarcini pentru utilizatorii autorizați în conformitate cu parametrii de configurare a fluxurilor de lucru.
- CF 20.05: SI „Protecția Socială" trebuie să ofere funcționalități de reatribuire a sarcinilor non-destinatar (al căror destinatar inițial a refuzat sau a întârziat să efectueze sarcinile).
- CF 20.06: SI „Protecția Socială" trebuie să implementeze proceduri automate pentru a primi date de la sistemele informaționale externe.
- CF 20.07: SI „Protecția Socială" trebuie să ofere o interfață pentru a vizualiza starea procedurilor automate active.
- CF 20.08: SI „Protecția Socială" trebuie să permită administratorilor de sistem să configureze și să pornească manual orice procedură automată conform parametrilor presetați.

**Notes/integrations:** Background job processor; scheduler (cron-like); KPI pre-computation; external system polling; task auto-reassignment.

---

## UC21: Procesare cerere/formular — Process application/form

**Actors:** Utilizator CNAS (Funcționar CNAS).
**Preconditions:** Application/form registered with processing rules defined.
**Main flow:**
1. CNAS official views electronic forms with service-request applications in an ergonomic interface.
2. System processes the application/form per the rules defined at form/workflow level.
3. CNAS official performs processing actions on the form.
**Alternate flows / exceptions:** Per workflow definitions.
**Postconditions:** Application/form processed per rules.
**CF requirements (verbatim):**
- CF 21.01: SI „Protecția Socială" va procesa cererile/formulare în conformitate cu regulile de procesare stabilite la nivel de cerere/formular sau flux.
- CF 21.02: Funcționarul CNAS va avea posibilitate să vizualizeze într-o formă comodă și să proceseze formularele electronice cu cereri de prestate a serviciilor.

**Notes/integrations:** Processing rules defined at form-type level (CF 07.02) and workflow level (CF 16.x).

---

## UC22: Notific utilizatori — Notify users

**Actors:** Sistem, Mnotify.
**Preconditions:** Notification triggers defined; user profile contains notification preferences.
**Main flow:**
1. System detects a business event of interest to the user (task assignment, deadline overrun, workflow involvement needed, approval action needed, action result, performance issue, etc.).
2. Per user profile, dispatches via: Email (Mnotify); user dashboard notification; or any combination.
3. Dashboard notification carries a direct reference to the related application/document/form.
4. Mnotify also enables Viber, instant messages, push.
**Alternate flows / exceptions:** Multiple delivery channels combined per user preference.
**Postconditions:** User notified; notification persists in dashboard with hyperlink.
**CF requirements (verbatim):**
- CF 22.01: SI „Protecția Socială" va furniza funcționalitate de notificare a utilizatorilor privind evenimentele de business de care sunt interesați utilizatorii sau care necesită implicarea acestora.
- CF 22.02: În funcție de utilizator (datele de configurare a profilului acestuia), vor fi utilizate următoarele strategii de notificare: notificare prin Email (Mnotify); notificare în dashboard-ul utilizatorului autorizat; oricare din categorii de mai sus.
- CF 22.03: SI „Protecția Socială" trebuie să notifice utilizatorii relevanți atunci când au loc evenimente de business specifice activităților în care sunt implicați. Pot fi menționate un șir de evenimente care presupune expedierea de notificări: asignarea de sarcini utilizatorului; depășire termen de realizare sarcini; necesitatea implicării utilizatorului în activitățile fluxurilor de lucru; necesitate efectuării de acțiuni de aprobare; rezultatul acțiunii solicitate de utilizator (acceptare, înregistrare document, aprobare document, refuz aprobare document etc.); probleme ce afectează performanța de funcționare a SI „Protecția Socială"; alte categorii relevante.
- CF 22.04: SI „Protecția Socială" va genera și expedia în mod automat notificările în funcție de evenimentele supuse notificării.
- CF 22.05: Notificarea stocată în dashboard-ul utilizatorului va dispune de referință de acces direct la cerere/documentul/formularul electronic aferent notificării.
- CF 22.06: SI „Protecția Socială" se va integra cu sistemul informațional partajat Mnotify în scopul expedierii notificării utilizatorilor interni și externi și implementării altor mijloace de notificare oferite de Mnotify (Viber, Mesaj instant, Push etc.).

**Notes/integrations:** Mnotify (Email, Viber, instant message, push). Dashboard notifications with direct deep links.

---

## UC23: Jurnalizez evenimente — Journal events

**Actors:** Sistem, Administrator de sistem, Mlog.
**Preconditions:** Journaling/audit infrastructure configured.
**Main flow:**
1. System journals all business events related to its use.
2. Categories journaled (at minimum): user authentication/disconnection; record add/modify/suppress/access; events for receiving an application; events for examining an application; events for executing a task; events for issuing documents; approval/rejection events on forms/documents; other specific business events; changes to electronic-service configuration parameters; changes to system configurations, parameters/constants, nomenclatures/classifiers, other CNAS-specific metadata; user account activation/deactivation, role and group assignment; data/document synchronization/exchange events with external systems; report/document generation/access events; DB queries; other specific business events.
3. Per event, stores (per event nature): journal-event identifier; user identifier who generated the event; event category; journal timestamp; system component that generated the event; record affected by the event; details of user action; reference to the information object affected.
4. Journal retains enough data to clearly understand the nature of modified/deleted data and locate affected records.
5. Each journaled event contains a direct reference to the related information object (application, document, electronic form, etc.).
6. Critical business events also journaled to **Mlog**.
7. Administrator can configure additional business event categories to be journaled via Mlog.
**Alternate flows / exceptions:** Admin-configurable extension of categories.
**Postconditions:** Full audit trail maintained internally and via Mlog.
**CF requirements (verbatim):**
- CF 23.01: SI „Protecția Socială" va conține mecanism de jurnalizare a tuturor evenimentelor de business aferente utilizării sale.
- CF 23.02: Vor fi jurnalizate cel puțin următoarele categorii de evenimente: autentificare/deconectare utilizator; adăugare/modificare/suprimare/accesare înregistrare; evenimente aferente procesul de recepționare a unei cereri; evenimente legate de procesul de examinare a unei cereri; evenimente legate de procesul de executare a unei sarcini; evenimente legate de procesul de eliberare a documentelor; evenimente de aprobare/respingere a formularelor electronice/documentelor; alte evenimente de business specifice; modificări efectuate în parametrii de configurare a serviciilor electronice; modificări efectuate în configurațiile de sistem, parametrii și constante, nomenclatoare și clasificatoare, alte categorii de metadate specifice activității CNAS; evenimente de activare/dezactivare a conturilor de utilizator, atribuirea rolurilor și grupurilor de utilizatori; evenimente de sincronizare/schimb date și documente cu sisteme externe; generare/accesare raport sau document; interogări la baza de date; alte evenimente de business specifice.
- CF 23.03: Evenimentele jurnalizate vor salva următoarele categorii de date (în funcție de natura evenimentului jurnalizat: identificatorul evenimentului jurnalizat; identificatorul utilizatorului care a generat evenimentul; categoria evenimentului jurnalizat; momentul jurnalizării evenimentului; componenta sistemului informațional care a generat evenimentul de business; înregistrarea afectată de evenimentul de business; detaliile acțiunii efectuate de utilizator; referință spre obiectul informațional afectat etc.
- CF 23.04: Jurnalizarea va păstra un set suficient de date încât să fie clară natura datelor modificate sau suprimate și să poată fi regăsite ușor înregistrările afectate de evenimentele de creară sau modificare a entităților informaționale sistemului informațional.
- CF 23.05: Evenimentul jurnalizat va conține o referință de acces direct la obiectul informațional (cerere, document, formular electronic etc.) aferent evenimentului de business.
- CF 23.06: SI „Protecția Socială" se va integra cu sistemul informațional partajat Mlog în scopul jurnalizării evenimentelor de business critice.
- CF 23.07: Administratorul de sistem va putea configura categoriile evenimentelor de business care vor fi jurnalizate suplimentar prin intermediul Mlog.

**Notes/integrations:** Internal audit log + Mlog integration for critical events. Admin-configurable categories.

---
