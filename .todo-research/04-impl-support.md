# Implementation, Deployment & Support — TOR Sections 5-7

Source: `tor/TOR.md` lines 4902-7260 (PDF pages 59-88).
Scope: Section 5 (Implementation requirements), Section 6 (Maintenance & support), Section 7 (Final product & deliverables).

---

## 1. Implementation Phases (Milestones)

Per Table 5.1, the implementation plan has 7 milestones with stated durations.

### Milestone 1 — Project Preparation (6 months from contract signing)
- **Task 1.1**: Organize project kick-off meeting.
- **Task 1.2**: Develop Project Management and Implementation Plan.
- **Task 1.3**: Business analysis, develop SRS (Software Requirements Specification) document for SI "Protecția Socială".
- **Task 1.4**: Develop technical infrastructure requirements (development, test/training, production environments).
- **Task 1.5**: Configure SI "Protecția Socială" infrastructure (development and test/training environments).

Deliverables:
- **1.1**: Project Initiation Document / Project Charter — including supplier's vision, implementation approach, and project team.
- **1.2**: Project Management Plan — including Implementation Schedule, Stakeholder Engagement Plan, Change Management Plan, Risk Management Plan.
- **1.3**: SI "Protecția Socială" Project document (SRS) based on business analysis.
- **1.4**: Technical infrastructure requirements (dev, test/training, prod environments).
- **1.5**: Configured Development and Test/Training environments.

### Milestone 2 — Design and Development (20 months from Milestone 1 completion)
- **Task 2.1**: Iterative implementation using Waterfall-based project management, CI/CD practices, with technical documentation updated each iteration.
- **Task 2.2**: Internal testing (manual and automated) of functionalities implemented per iteration.
- **Task 2.3**: Periodic demo sessions with training on operating implemented functionalities and consultations on UI usability.
- **Task 2.4**: Bi-weekly project management meetings (status, issues, achievements, planned activities, milestones, deliverables, risks).

Deliverables:
- **2.1**: Iteratively updated Software Design Document (SDD).
- **2.2**: Functional SI "Protecția Socială" deployed in dev and test/training environments.
- **2.3**: Report on improvements applied based on stakeholder suggestions/requirements.
- **2.4**: Complete source code and access to version control repository.
- **2.5**: Bi-weekly project management reports and presentations for the full implementation period.

### Milestone 3 — Integration with External Systems (5 months, in parallel with Milestone 2 activities)
- **Task 3.1**: Integration with shared government IS (Msuite services family and PGD).
- **Task 3.2**: Integration with external IS.
- **Task 3.3**: Integration with CNAS financial IS (FMS).

Deliverables:
- **3.1**: Technical integration specifications (with shared government IS, external IS, CNAS financial IS).
- **3.2**: Technical specifications of APIs exposed by SI "Protecția Socială".
- **3.3**: Acceptance protocol for interoperability and data exchange mechanism.

### Milestone 4 — Data Migration and Population (6 months from Milestone 2 completion)
- **Task 4.1**: Develop data migration/population plan.
- **Task 4.2**: Develop migration/population scripts.
- **Task 4.3**: Migrate and populate data.
- **Task 4.4**: Test and reconcile migrated/populated data.

Deliverables:
- **4.1**: Data migration plan and methodology.
- **4.2**: Migration/population scripts.
- **4.3**: Acceptance protocol for data migration/population.

### Milestone 5 — User Training (1 month after Milestone 4 completion, and within demo sessions)
- **Task 5.1**: Develop training plan, schedule, materials for users.
- **Task 5.2**: Train System Administrators.
- **Task 5.3**: Train SI "Protecția Socială" trainers.
- **Task 5.4**: Train all categories of users.

Deliverables:
- **5.1**: Training plan, schedule, support materials (guides, presentations, video instructions) and brief instructions on using supplier's helpdesk service.
- **5.2**: List of qualifications/skills needed by CNAS System Administrators to operate and maintain SI "Protecția Socială".
- **5.3**: Report on training conducted.
- **5.4**: Registry of suggestions/improvement requirements received during training.
- **5.5**: Access to supplier's helpdesk solution.

### Milestone 6 — Stabilization and Acceptance (3 months after Milestone 5 completion)
- **Task 6.1**: Modify and improve SI based on training feedback and project beneficiary requests.
- **Task 6.2**: Update technical documentation per improvements.
- **Task 6.3**: Support activities for information and popularization of SI.
- **Task 6.4**: Develop Business Continuity Plan, Disaster Recovery Plan, Backup Plan, and other relevant information security procedures per national legislation and security standards.
- **Task 6.5**: Pilot/Stabilization (apply improvements, fix errors, support and technical assistance).
- **Task 6.6**: Final acceptance based on pre-approved test scenarios.
- **Task 6.7**: Handover activities.

Deliverables:
- **6.1**: Acceptance protocol for handover of SI to CNAS and associated technical documentation (e.g., documented source code, final SDD, technical documentation, user guides).
- **6.2**: BCP, DRP, Backup Plan, and other security procedures for operational period.
- **6.3**: Final acceptance test report.
- **6.4**: Report on implemented change requests, information activities, error corrections, updated technical documentation and source code during stabilization.

### Milestone 7 — Post-Implementation Technical Support and Maintenance (12 months after Milestone 6 completion)
- **Task 7.1**: Post-implementation support and maintenance after stabilization and final acceptance.
- **Task 7.2**: Solve problems/errors/deficiencies and update SI, including associated technical documentation.

Deliverables:
- **7.1**: Monthly technical support report including number of assistance requests received/resolved and achievement of SLA performance indicators.
- **7.2**: Monthly report on errors/deficiencies resolved and updated technical documentation, including updated source code.

**Total project duration**: roughly 6 + 20 + 3 + 12 = ~41 months from contract signing to end of warranty (with Milestones 3 & 4 running in parallel with 2; Milestone 5 partly within Milestone 2 demo sessions, fully after Milestone 4).

---

## 2. Project Management Requirements (Table 5.2)

- **MG 001 (M)**: Supplier is responsible for project management per Project Management Plan and practices agreed with CNAS and Acquirer.
- **MG 002 (M)**: SI must be implemented based on **iterative Waterfall methodology**.
- **MG 003 (M)**: Supplier identifies and mobilizes resources for activities in Project Management Plan, ensuring agreed quality level.
- **MG 004 (M)**: CNAS is responsible for all administrative procedures: project launch, organizing internal project team, preparing ICT environment for implementation and operation.
- **MG 005 (M)**: Project to be managed using a widely accepted ICT industry methodology (e.g., **PRINCE2, PMBOK** etc.).
- **MG 006 (M)**: Supplier coordinates Implementation Plan with CNAS and includes project plan proposal (Project Initiation Document / Project Charter) in offer. Must include at minimum:
  - Project organigram including: Project Manager, Project Committee, supplier team roles, CNAS team roles.
  - Key responsibilities for each project role.
  - Practices for project interaction and collaboration including: Project Plan management (also through financial proposal lens), detailed costs structured by activities and implementation phases, derived from budget and deadline; detailed activity planning; resource management; communication plan; change management; risk management; deliverable quality management; progress monitoring and reporting; managing deviations from project plan; project library management.
- **MG 007 (M)**: CNAS and Supplier each designate a Project Manager to lead their respective project teams.
- **MG 008 (M)**: Supplier's Project Manager must have authority to implement project activities and is principally responsible for producing and delivering project deliverables per terms and quality standards.
- **MG 009 (M)**: Supplier may appoint a Team Leader to facilitate communication and collaboration with stakeholders by area of expertise.
- **MG 010 (M)**: Supplier must perform at minimum these specific project management activities:
  - Develop and coordinate Project Initiation Document with stakeholders; adjust as needed.
  - Develop and coordinate Project Plan; adjust as needed over implementation.
  - Develop detailed Work Plans.
  - Coordinate activities per Work Plan.
  - Implement Communication Plan.
  - Develop weekly/bi-weekly progress reports.
  - Keep project management registers throughout implementation (at minimum: Deliverables Register, Risks Register, Changes Register, Correspondence Register, Events Register).
  - Organize project management meetings per Communication Plan.
  - Send final reports for each project phase and present at project management meetings at end of each phase.
  - Complete principal project phases and submit acceptance documents to stakeholders.
  - Upload project management deliverables to Project Library.
- **MG 011 (M)**: All communications and deliverables related to project management must be in **Romanian**. An English version may be requested case-by-case.
- **MG 012 (M)**: Supplier must prepare at minimum these project management deliverables:
  - Project Charter / Project Initiation Document.
  - Project Plan and its modifications.
  - Detailed project activity plans (iterations).
  - Presentations for kick-off and periodic management meetings.
  - Weekly/bi-weekly progress reports and project registers maintained per Project Initiation Document.
  - Phase completion reports containing at minimum: overview of completed phase, project plan for next phase, risk analysis, identified issues, recorded project quality level.
  - Project deviation reports containing at minimum: description of reasons for deviations, impact, proposed solutions and overall impact, options recommended by Project Manager or Supplier.

---

## 3. Team Composition (Table 5.3)

Personnel cheie required (key personnel — CVs must be in offer):

### 1. Project Manager
- Master's in ICT or economics.
- At least **7 years** experience in ICT project management.
- At least **4 years** demonstrated experience in team/project management (preferably in government) applying Waterfall and/or Hybrid methodology.
- At least **2 reference projects** implementing IS with similar application/complexity.
- Strong analytical, leadership, motivational skills; relevant experience in business process analysis.
- Recognized certification in project management (PMP, Prince 2, PM2).
- Fluent Romanian and English.
- Tenure with the supplier/economic agent: at least **4 years**.

### 2. Business Analyst
- Bachelor's in ICT or economics.
- At least **5 years** experience as Business Analyst on similar IS projects.
- Demonstrated experience using modern IS design methodologies and applying ICT standards/initiatives specific to RM government sector.
- Demonstrated participation as Business Analyst in at least **2 projects of similar complexity in last 3 years**.
- Experience in modular testing, continuous integration, DevOps.
- Certification in business analysis (e.g., CBAP).
- Fluent Romanian and English.
- Tenure with supplier: at least **3 years**.

### 3. System Architect
- Bachelor's in ICT.
- At least **5 years** experience as System Architect on similar IS projects.
- Experience using modern IS design methodologies and government sector standards.
- Participation as System Architect in at least **2 projects of similar complexity in last 3 years**.
- Experience in modular testing, continuous integration, DevOps.
- Certification in IS design (TOGAF 9, CTA etc.) is an advantage.
- Fluent Romanian and English.
- Tenure with supplier: at least **3 years**.

### 4. Team Leader / Senior Software Developer
- Master's in ICT.
- At least **7 years** experience developing IS of similar complexity using proposed technology stack.
- Demonstrated similar-role involvement in at least **2 similar projects in last 3 years**.
- Experience in unit testing, continuous integration, DevOps (k8s).
- Experience integrating IS, designing/implementing data-exchange APIs using SOAP/REST.
- Recognized certification in proposed tech stack is an advantage.
- Tenure with supplier: at least **3 years**.
- Fluent Romanian and English.

### 5. Developer / Database Administrator
- Bachelor's in ICT.
- At least **5 years** software development experience as Developer/DBA using proposed tech stack.
- Demonstrated similar-role involvement in at least **2 similar projects in last 3 years**.
- Experience in DB design, development, optimization.
- Experience in unit testing, continuous integration.
- Recognized DB certification and proposed stack certification is an advantage.
- Tenure with supplier: at least **3 years**.
- Fluent Romanian and English.

### 6. Developer / DevOps Specialist
- Bachelor's in ICT.
- At least **5 years** software development experience using proposed tech stack.
- At least **2 similar-complexity projects in last 3 years**.
- Experience in CI/CD of similar-complexity IS (k8s).
- Experience in unit testing and DevOps.
- Recognized certification in CI/CD and proposed stack is an advantage.
- Tenure with supplier: at least **3 years**.
- Fluent Romanian and English.

### 7. Developer / Integration Expert
- Bachelor's in ICT.
- At least **5 years** software development experience using proposed tech stack.
- At least **2 similar-complexity projects in last 3 years** as Integration Expert.
- Experience in unit testing.
- Experience integrating IS, designing/implementing data-exchange APIs using SOAP/REST.
- Recognized certification in proposed stack is an advantage.
- Tenure with supplier: at least **3 years**.
- Fluent Romanian and English.

### 8. Quality Assurance Engineer
- Bachelor's in ICT or other relevant fields.
- At least **5 years** experience testing IS of similar complexity.
- At least **2 similar-complexity projects in last 3 years**.
- Demonstrated competencies in functional testing of IS.
- Demonstrated competencies in performance testing (load and stress) and security testing (at minimum **OWASP Top 10 vulnerabilities**).
- Demonstrated competencies in test automation.
- Recognized certification in software testing (ISTQB) and proposed stack is an advantage.
- Tenure with supplier: at least **3 years**.
- Fluent Romanian and English.

### 9. UX/UI Designer
- Bachelor's degree or specialization courses in: Graphic Design, UX/UI Design, Visual Communication, or other relevant disciplines.
- Minimum **3 years** designing complex web platforms or e-services, preferably with complex/multi-step/data-sensitive user flows (e.g., banking apps, digital onboarding, government service portals).
- Demonstrated knowledge and experience in:
  - Design tools: Figma (advanced level), FigJam.
  - UX principles, wireframing, prototyping, accessibility (WCAG).
- Experience collaborating within design systems and maintaining design documentation.
- Advantages: experience in public-sector digital transformation projects; certifications in Product Design, UX, or User Research; familiarity with Design Thinking frameworks.
- Competencies: strategic thinking, identification of improvement opportunities, simplifying complex flows; user-centered/service design principles; clear documentation of design decisions for engineering handoff (Figma specs, component annotations); ability to work in multidisciplinary teams and manage multiple projects; empathy and awareness of user diversity/accessibility; results orientation with measurable impact; analytical/solution-oriented thinking; collaboration with developers.
- Tenure with supplier: at least **3 years**.
- Fluent Romanian and English.

### 10. IT Security Specialist
- Bachelor's in ICT or other relevant fields.
- At least **5 years** experience in domain.
- At least **2 similar-complexity projects in last 3 years**.
- Solid knowledge of administering IS and communications, security of OS, applications (including web), and databases.
- Familiarity with network protocols/technologies (TCP/IP, VLAN, VPN, firewall, IDS/IPS, cryptography, authentication, PKI) and security mechanisms at infrastructure and application level.
- Ability to identify vulnerabilities, assess risks, develop and implement IT security measures and procedures.
- Demonstrated competencies in security testing (at minimum **OWASP Top 10**).
- Recognized certification (CompTIA Security+, CySA+, CISSP) is an advantage.
- Tenure with supplier: at least **3 years**.
- Fluent Romanian and English.

### 11. Trainer (Formator)
- Bachelor's in ICT or other relevant fields.
- Communication and user training abilities.
- Demonstrated competencies in user training for IS in at least **2 projects implemented in last 3 years**.
- Demonstrated competencies in developing documentation and instructional materials for end users.
- Experience in developing and delivering online training.
- Fluent Romanian and **Russian** (not English).
- Tenure with supplier: at least **3 years**.

### Non-Key Team Members
Other (non-key) project team members must have competencies in:
- Development/implementation of Web IT solutions.
- Database design and administration.
- Design/development/integration of data-exchange interfaces with external IS.
- Quality assurance including test automation experience.
- User training abilities.

---

## 4. Bidder Qualifications (Section 5.4)

The offeror must have solid experience in successful similar projects:
- Minimum **2 ICT projects of similar complexity** successfully implemented in the **last 5 years**.
- For both completed and in-progress projects, copies of acceptance documents for the entire software solution must be provided.
- Experience developing systems in **social security / state social insurance** domain is an advantage.
- Experience developing ICT solutions for state/public institutions (central and/or local), including the ability to integrate the system with **national platforms (MConnect, MPass, MPay, MSign etc.)**.
- Ability to develop technical requirements for IS development, a detailed development plan, and other necessary technical documents.
- Presentation of certificate of absence/existence of public budget arrears.

---

## 5. Development Process (Table 5.4)

- **DEV 001 (M)**: SI to be designed/developed based on **Waterfall methodology**. Based on business analysis results, SRS is developed and used in development iterations.
- **DEV 002 (M)**: Design and development uses Waterfall project management approach (system developed iteratively and incrementally using **CI/CD** practices).
- **DEV 003 (M)**: Iteration duration during development phase = **1 month**.
- **DEV 004 (M)**: Per iteration, supplier performs:
  - Select relevant tasks for the iteration (or carry over unfinished tasks from previous iteration).
  - Develop planned functionalities.
  - Internal testing of implemented functionalities.
  - Develop/update technical documentation related to iteration's functionalities (e.g., SDD, guides, specific procedures).
- **DEV 005 (M)**: Supplier prepares periodic **bi-weekly** project management reports briefly informing stakeholders about: completed tasks; tasks to be completed in next period; tasks planned for next period; problems/questions on current activities; current risks and mitigation actions.
- **DEV 006 (M)**: Supplier performs periodic **demo presentations** of implemented functionalities, collecting stakeholder comments/suggestions for incorporation into development.
- **DEV 007 (M)**: Supplier must be able to deliver specific SI functionalities to production if needed (stakeholders may decide which functional modules go to production before complete end of development phase).
- **DEV 008 (M)**: Architectural and technological solutions developed **independently from existing system architecture** (no mandatory adoption of existing system's architecture/technology) — **greenfield approach**. Existing system may be used only for analyzing business processes and data, identifying constraints and deficiencies, but not as architectural/technological reference model.

---

## 6. Deployment Requirements (Table 5.5)

- **DEP 001 (M)**: SI must be installable in **virtualized environments**.
- **DEP 002 (M)**: SI must provide **containerized infrastructure** for deployment (e.g., Docker Engine, Kubernetes).
- **DEP 003 (D — Desirable)**: **Kubernetes (k8s)** is welcomed as deployment and load balancing technology.
- **DEP 004 (M)**: SI must be capable of initiating deployment to multiple environments simultaneously (dev, test/training, production) from zero.
- **DEP 005 (M)**: Deployment performed via specialized tooling.
- **DEP 006 (M)**: Deployment mechanism must define the container component to update (e.g., new platform version, updated functional module, etc.).
- **DEP 007 (M)**: Deployment mechanism manages container content.
- **DEP 008 (M)**: Deployment mechanism adds new components to container content.
- **DEP 009 (M)**: Deployment mechanism specifies cluster (dedicated server or Cloud) for deployment.
- **DEP 010 (M)**: Deployment mechanism provides workflow for code or registry compilation.
- **DEP 011 (M)**: Deployment mechanism provides functionality for IS delivery and third-party actions (e.g., installing additional packages, configuring notifications) using existing tooling.
- **DEP 012 (M)**: Production environment must be **auto-updatable with manual intervention possibility** (e.g., manual build approval).
- **DEP 013 (M)**: Deployment mechanism transfers specific records from test/training environment to production (e.g., configuration parameters, classifier/nomenclature values, UI labels/messages/texts).
- **DEP 014 (M)**: Developer delivers to CNAS all tooling and scripts necessary for automated deployment of SI.

**MCloud context** (introduced in section 5.6 prose):
- The supplier must consider current requirements of the **e-Government Agency of Republic of Moldova** for deploying IS within the shared government platform **MCloud** and for load balancing.

Environments required: **development, test/training, production**.

---

## 7. Data Migration & Population (Table 5.6)

- **MIG 001 (M)**: CNAS prepares and delivers datasets and metadata needed for primary data population of SI. Format of migrated data agreed jointly with supplier.
- **MIG 002 (M)**: Supplier converts specific metadata values from external datasets per CNAS metadata system.
- **MIG 003 (M)**: Supplier includes in technical offer their approach to migration/initial data population procedure.
- **MIG 004 (M)**: Supplier develops mechanism for automated population of SI database with relevant metadata (nomenclatures, classifiers, variables) and primary datasets from external sources to consolidate initial data stock.
- **MIG 005 (M)**: Supplier is responsible for, during migration/population implementation:
  - Defining methodology used for migration/population process.
  - Developing detailed migration and population plans.
  - Providing software mechanisms for migration/population.
  - Defining quality requirements for datasets to be migrated/populated and processing them via migration/population mechanisms.
  - Mapping values of metadata received from external sources (in case of divergences).
  - Defining reconciliation criteria for migrated/populated data.
  - Participating in data cleansing and enrichment.
  - Verifying and validating quality of datasets.
  - Populating SI database based on datasets provided by CNAS.
  - Identifying and resolving exceptions/errors during migration/population.
- **MIG 006 (M)**: Supplier proposes data migration/population methodology to CNAS. Methodology must contain:
  - Methodology for preparing data to be migrated/populated.
  - Methodology for mapping migrated/populated data.
  - Methodology for cleansing/enriching migrated data and ensuring quality.
  - Methodology for filling in mandatory data values missing from supplied datasets.
  - Automated migration/population procedure.
  - Principles for reconciliation of migrated/populated data.
  - Recovery plan in case of failure (for each migration/population stage).
  - Migration/population mechanism delivery plan.
- **MIG 007 (M)**: Supplier prepares and delivers detailed plan for initial migration/population (data migration and conversion strategy). Plan must align with implementation plan.
- **MIG 008 (M)**: Supplier delivers software solution for automation of migration/initial data population processes.
- **MIG 009 (M)**: All migration/initial data population activities performed in CNAS-controlled operating environment. **Data never leaves CNAS ICT infrastructure.**
- **MIG 010 (M)**: During migration/population, supplier conforms to CNAS security policy.
- **MIG 011 (M)**: Supplier demonstrates correctness of migration/population tooling to CNAS specialists (an acceptance protocol for migration/initial population is signed between supplier and CNAS).

**Note**: User training and acceptance occur only **after** SI is populated with initial relevant data provided to supplier by CNAS.

---

## 8. User Training (Table 5.7)

- **UTD 001 (M)**: Supplier ensures all facilities for organizing training for relevant categories of authorized users:
  - Training space.
  - Workstations connected to network.
  - Technical equipment for training.
- **UTD 002 (M)**: Supplier ensures: preparation of training environment; support materials (in **Romanian and Russian**); training-effectiveness evaluation tests (in Romanian and Russian).
- **UTD 003 (M)**: Supplier develops and delivers training programs for all categories of authorized users.
- **UTD 004 (M)**: Supplier, jointly with CNAS, develops the user training plan.
- **UTD 005 (M)**: Supplier trains users per plan and programs agreed with CNAS. Training sessions held in **Romanian and Russian**.
- **UTD 006 (M)**: Training involves:
  - Training on operating key functionalities of SI (for non-administrator role users).
  - Training on configuration and administration of SI (for administrator-role users).
  - Training of trainers who will subsequently provide support and training to users after production deployment.
  - Guides and instructions for all categories of users involved in administering or operating SI.
- **UTD 007 (M)**: Supplier trains at least **2 System Administrators** from CNAS. Program designed for at least **64 hours**. Topics:
  - Operational procedures including Archive/Backup/Restore.
  - Security (Physical, Access Control, Networks, DB and applications). Improvement of access control management and generation of system reports (logging records, review of application control mechanisms etc.).
  - Routine maintenance procedures (scheduled maintenance of software and hardware components, server infrastructure security including software updates/patching, troubleshooting procedures, system register management).
  - Use of admin/config/monitoring consoles.
- **UTD 008 (M)**: Supplier trains at least **4 trainers** who will continue training users. Trainer program: at least **56 hours**.
- **UTD 009 (M)**: Authorized user training program: at least **40 hours**. Up to **100 users** with different roles in SI to be trained. Training consists of:
  - Familiarization sessions with SI functionalities and characteristics.
  - Practical exercise sessions.
  - Q&A sessions.
- **UTD 010 (M)**: Supplier prepares and delivers support documents for training:
  - SI architecture document.
  - SI administration guide.
  - User guides for each role in SI.
  - Support materials for admin and non-admin user training (video instructions, textual instructions, PowerPoint presentations).
- **UTD 011 (M)**: Administrator guides in **Romanian and English**. Other guides in **Romanian** (mandatory for all categories of deliverables).
- **UTD 012 (M)**: Supplier delivers user guides in **electronic format** (including online tutorials/multimedia presentations). Guides must be convenient to access and navigate; information easy to identify.
- **UTD 013 (M)**: Supplier prepares and delivers operational guides:
  - Guide for removing defects detected during operation.
  - Installation/deployment/configuration guide.
  - Maintenance guide.
  - Developer guide (for future SI developers).
  - Data archiving and restoring guide.
  - Information security management system documentation.
- **UTD 014 (M)**: Supplier delivers complete source code and libraries necessary for code compilation. Code must contain sufficient comments to be understood by CNAS IT staff. Supplier provides access to version control repository.

---

## 9. UAT (Acceptance Testing) (Table 5.8)

- **UAT 001 (M)**: Before acceptance testing begins, all SI components must be implemented and configured per functional and non-functional specs. Supplier populates system with dataset enabling testing on real cases.
- **UAT 002 (M)**: Supplier organizes acceptance testing including:
  - Defining test strategy and procedures.
  - Developing detailed Test Plan (delivered to stakeholders for coordination and acceptance).
  - Preparing test scenarios for all categories of tests (unit, integration, performance, functional, etc.) for stakeholder coordination/acceptance.
  - Documenting errors/deficiencies detected during test scenario verification and removing them.
  - Final test results Report including status of all errors/deficiencies (delivered for stakeholder coordination and acceptance).
- **UAT 003 (M)**: Before deploying SI to production, supplier develops test scenarios in collaboration with stakeholders and performs **5 types of tests**:
  - **Unit Testing**: Each component/module functions per design.
  - **Integration Testing**: Modules function as expected when interacting.
  - **Performance Testing (Load and Stress Testing)**: How SI performs under various loads. May require optimization of web server, application software, DB server, or network configurations.
  - **Recovery Testing**: How well SI recovers from blocks and hardware failures.
  - **Security Testing**: Tests against attacks (SQL injection, DDoS etc.) using software detection tools for security threats and vulnerabilities.
- **UAT 004 (M)**: Three additional types of tests performed **jointly by CNAS and Supplier**:
  - **Usability Testing**: Verify ease of use of SI UI; navigation mechanism verified. UI may be redesigned/modified during this phase based on CNAS feedback.
  - **Functional Testing**: All real processes and key services of CNAS simulated end-to-end to verify SI stores data and generates reports properly.
  - **Acceptance Testing**: Verify SI meets TOR requirements per supplier-prepared test scenarios. Acquirer may request additional test scenarios. CNAS performs this to determine acceptance.
- **UAT 005 (M)**: Test coverage of SI functionalities must be at least **90%**.
- **UAT 006 (M)**: Final test results and SI accepted if **no critical (blocking) errors and fewer than 3 major errors/deficiencies**.
- **UAT 007 (M)**: SI acceptance dated on the day all errors/deficiencies detected during testing are remedied.

---

## 10. Go-Live & Stabilization

### Production Deployment (Table 5.9)
- **COM 001 (M)**: Supplier proposes and justifies optimal go-live strategy (phased, big-bang, parallel operation, pilot launch).
- **COM 002 (M)**: Supplier participates in all production deployment stages:
  - Develop production deployment plan.
  - Develop roll-back plan if applicable.
  - Update datasets generated/modified in current CNAS IS after initial data population.
  - Provide active support during plan implementation.
  - Quickly eliminate errors and deficiencies detected during operation.
- **COM 003 (M)**: Supplier develops and coordinates production deployment plan with CNAS.
- **COM 004 (M)**: SI can be put into production if available and operational for all authorized users, **and** SI Acceptance Protocol is signed by Supplier and CNAS.

### Stabilization (Table 5.10)
- **STAB 001 (M)**: **3-month stabilization period** begins immediately after production deployment.
- **STAB 002 (M)**: During stabilization, supplier provides **on-site support** to resolve errors and deficiencies detected during operation.
- **STAB 003 (M)**: During stabilization, supplier develops fixes for errors/deficiencies, analyzes logs to prevent issues, makes UI and critical-module adjustments.
- **STAB 004 (M)**: Final acceptance attested via Final Acceptance Protocol signed by supplier and CNAS, conditional on:
  - Stabilization period expired.
  - All **Level 1** errors and deficiencies eliminated.
  - Fewer than **10 Level 2** errors and deficiencies remaining.
  - No test scenarios show data integrity or consistency problems.
- **STAB 005 (I — Informational)**: A **Level 1** error/deficiency = blocks or impedes use of key SI functionalities.
- **STAB 006 (I — Informational)**: A **Level 2** error/deficiency = blocks or impedes use of functionalities for which alternative options (workarounds) exist.

---

## 11. Support Tiers & SLAs

### General Warranty (Table 6.1)
- **PIR 001 (M)**: Supplier provides maintenance and tech support for entire warranty period = **12 months** after stabilization period ends.
- **PIR 002 (M)**: Financial offer includes estimated cost for warranty, support, post-implementation maintenance, except additional development outside SDD objectives.
- **PIR 003 (M)**: All SI operation errors detected during warranty are remedied at supplier's cost (not considered additional development).
- **PIR 004 (M)**: After warranty/support/maintenance, CNAS may request extension for additional fee. Supplier obliged to accept further service provision for at least **1 year** under TOR terms and offer estimates.

### Service Quality Parameters
- **Availability**: capacity of IS and components to receive queries from authorized entities and respond in a timely manner.
- **Usability**: ability of IS to function correctly, delivering expected services.
- **Performance**: ability to respond to legitimate queries at set parameters.
- **Security**: ability to ensure confidentiality, integrity, availability of stored/managed data.

### Support Services Requirements (Table 6.2)
- **PIR 005 (M)**: Supplier provides tech support to authorized users for incidents regardless of cause (errors, deficiencies, problems from external apps). For each incident, supplier:
  - Receives incident info and context.
  - Localizes incident, identifies immediate actions to mitigate impact.
  - Identifies root cause and defines remediation actions.
  - Assists CNAS in actions to mitigate impact and resolve within set time.
  - Presents detailed info on causes, rationale, and planned actions to prevent recurrence.
  - Considers registering new problem (managed per SLA).
- **PIR 006 (M)**: Supplier provides assistance for application-level problems:
  - Receive/collect info about problem, symptoms, effects, specific conditions.
  - Analyze and localize at component level; identify interdependencies.
  - Identify temporary workarounds and guide CNAS to apply them.
  - Identify solutions and provide periodic progress communication.
  - For configuration-related solutions, assist CNAS in performing configurations.
  - For code-level changes, supplier implements within set time as part of maintenance services.
- **PIR 007 (M)**: Supplier offers consulting services for SI operation:
  - Receive and register tech support requests with context.
  - Identify and validate solutions in test environment.
  - Provide complete, precise responses on how CNAS should react during SI operation.

### Maintenance Services Requirements (Table 6.3)
- **PIR 008 (M)**: Supplier offers SI update services and delivers new versions in certain cases.
- **PIR 009 (M)**: Supplier prepares software packages and documentation for updates as well as new versions.
- **PIR 010 (M)**: All updates and new versions implemented per change management section requirements.

### Development Services During Warranty (Table 6.4)
- **PIR 011 (M)**: Supplier provides modification and development services. Scope at minimum:
  - Modifications at presentation layer.
  - Modifications at business logic layer.
  - Modifications at data layer.
- **PIR 012 (M)**: As part of modification/development services, supplier performs:
  - Receive modification request with functional spec description.
  - Develop SDD for the request and coordinate with CNAS.
  - Make modifications/developments at SI component level.
- **PIR 013 (M)**: Implementation per change management section requirements.
- **PIR 014 (M)**: Supplier describes proposed model for managing modification/development requests and methods for effort/price estimation (transparency and fairness).
- **PIR 015 (M)**: Development services include: modification of existing functionalities; implementation of new functionalities.
- **PIR 016 (M)**: Any development initiated based on CNAS request with functional specs. Implementation through agreed change management process. For application-level modifications, process includes at minimum:
  - Implementation in CNAS test environment with unit testing.
  - Implementation in CNAS test environment with acceptance testing involving users.
  - Implementation in CNAS production environment per change management procedure.
  - Final review and final acceptance of modification.
- **PIR 018 (M)**: Offer must include **50 person-days** of unplanned development effort consumed during warranty.
- **PIR 019 (M)**: Additional development services beyond the included may be requested by CNAS and offered by supplier via additional agreements.

### Request Importance Classification (Table 6.5)

| Class | Impact on Quality Parameters |
|---|---|
| **Critică (Critical)** | Availability: IS unavailable to all/majority of users. Important transactions need execution ASAP (within hours). Usability: Key business functions unusable; no alternatives. Performance: Response time makes IS practically unusable. Security: Major risks to confidentiality, integrity, or availability. |
| **Înaltă (High)** | Availability: IS unavailable to a good portion of users. Important transactions/operations needed by start of next day. Usability: Key business functions usable in limited way. Performance: Response time significantly affects key business processes. Security: High risks to CIA. |
| **Ordinară (Ordinary)** | Availability: IS unavailable to a portion of users. Transactions/operations to execute within 3 days. Usability: Business functionality usable in limited way. Performance: Response time moderately affects business processes. Security: Risks to CIA exist. |
| **Joasă (Low)** | Availability: IS unavailable to limited users. No transactions to execute within 3 days. Usability: Business functionality insignificantly affected; alternatives exist. Performance: Response time higher than usual; business processes not affected. Security: Minor risks to CIA. |

- CNAS sets classification per request; can reclassify based on context changes.
- Support hours: **business days per RM legislation, 08:00–18:00**.

### SLA Response & Resolution Times (Table 6.6)

| ID | Classification | Response Time (TR) | Resolution Time (TS) |
|---|---|---|---|
| PIR 020 | Critică (Critical) | **5 min** | **60 min** |
| PIR 021 | Înaltă (High) | **60 min** | **End of day** |
| PIR 022 | Ordinară (Ordinary) | **24h** | **3 days** |
| PIR 023 | Joasă (Low) | **3 days** | **Best effort\*** |

\* Supplier exercises best effort; resolution deadline communicated and accepted by CNAS. Subsequent changes to deadline only with CNAS acceptance.

### Maintenance Service Level (Table 6.7)
- **PIR 022 (M)** [renumbered, duplicate ID]: Supplier minimizes frequency of updates. Policy allows CNAS to apply new updates **monthly**. Exception: critical and security updates.
- **PIR 023 (M)** [renumbered, duplicate ID]: Policy of non-obligatory new-version implementation. CNAS implements new versions **once every 3 years**.
- **PIR 024 (M)**: Supplier communicates update/new-version schedule. Updates: notify CNAS at least **1 month** in advance. New versions: notify CNAS at least **6 months** in advance.
- **PIR 025 (M)**: For maintaining SI in functional state, supplier may perform maintenance work:

| Type of Maintenance | Beneficiary Notification | Period and Duration |
|---|---|---|
| **Ordinary maintenance** | 5 days in advance | Outside guaranteed availability period. Duration ≤ **4 hours**. |
| **Major maintenance** | 10 days in advance | Outside guaranteed availability period. Duration ≤ **24 hours**. |
| **Urgent maintenance** | Immediately when need arises | Any period. Duration ≤ **2 hours**. |

### Development Service Level (Table 6.8)
- **PIR 026 (M)**: Supplier reacts to CNAS development request in **max 3 days**.
- **PIR 027 (M)**: Supplier provides budget estimates and solution concept in **max 10 days**.
- **PIR 028 (M)**: Supplier delivers solution in time agreed with CNAS using best-effort principle.
- **PIR 029 (M)**: Supplier allows CNAS to set priorities for development requests and to revise them later. Priority revision may revise delivery deadlines.

---

## 12. Change Management Process (Table 6.9)

- **PIR 030 (M)**: In offer, supplier includes proposed change management approach for applications.
- **PIR 031 (M)**: Supplier proposes change management procedure for applications to CNAS. Coordinated and accepted by CNAS.
- **PIR 032 (M)**: Procedure must include at minimum these supplier responsibilities:
  - Testing modifications in SI test environment.
  - Preparing modification implementation plan.
  - Preparing rollback plan for failed modifications.
  - Preparing technical documentation: scope, affected components, implementation guide, rollback application guide, follow-up guide.
  - Preparing detailed technical documentation (description of modifications, affected components, installation instructions, rollback plan, follow-up procedures).
  - Updating user and technical documentation and transmitting to CNAS.
  - Providing software packages for modifications.
  - Providing source code files for modifications (authenticity and integrity ensured by supplier digital signature — **code signing**).
  - Reacting immediately when errors are detected in implemented modifications and correcting in shortest time.
- **PIR 033 (M)**: During maintenance phase, supplier may make various modifications. All implemented per agreed change management process. Modifications with significant impact on quality parameters require **CNAS authorization**. Mandatory elements for such modifications:
  - Testing in SI test environment.
  - Implementation plan.
  - Rollback plan.
  - Post-implementation review.
- Supplier keeps Modifications Register; CNAS has access.

### Support Quality Assurance (Table 6.10)
- **PIR 034 (M)**: Supplier presents Quality Assurance Plan (approved by CNAS) containing performance indicators, risks, preventive actions, residual-risk mitigation.
- **PIR 035 (M)**: Offer includes supplier's approach to quality assurance plan.
- **PIR 036 (D — Desirable)**: Supplier audits capability to provide services at agreed level. Audits by independent entities; methodology aligned with best practices (SAS 70, ITIL, ISACA standards). Reports submitted to CNAS with remediation action plans.
- **PIR 037 (M)**: Supplier develops and maintains Quality Plan covering:
  - Operational risks (loss of supplier capacity, internal process risks).
  - Technological risks (affecting availability, accessibility, performance, security).
- **PIR 038 (M)**: Quality Plan contains detailed identified risks, prevention measures, residual risks, reaction measures.
- **PIR 039 (M)**: Quality Plan updated at least **annually** or on any major change in components/processes. Supplier presents latest version to CNAS.
- **PIR 040 (M)**: At offer stage, supplier describes how Quality Plan will be produced. Competitive advantage if Plan annexed.

### Contract End (Table 6.11)
- **PIR 041 (M)**: If contract effect ceases, supplier ensures at minimum:
  - Source codes (or configuration files for COTS solutions) transferred to CNAS.
  - Transferred codes/configs match what runs in production at contract end (authenticity/integrity confirmed by supplier digital signature).
  - All SI documentation updated and transferred.
  - All records of CNAS requests handled by supplier (incidents, problems, consulting, modifications, developments) exported in mutually agreed format (CSV, XLS) and transferred.
  - Supplier retains all records, source codes, and documentation for **1 calendar year**.
- **PIR 042 (M)**: For **1 calendar year** after contract expiry, supplier cooperates with third parties authorized by CNAS to provide post-implementation support and maintenance, providing any information held that would help improve services.
- **PIR 043 (M)**: Supplier includes in offer the proposed approach for terminating post-implementation support.

---

## 13. Final Deliverables Checklist (Table 7.1)

### DEL 001 (M) — Project Management deliverables:
- Project Initiation Document / Project Charter.
- Project Management Plan.
- Weekly/bi-weekly project management reports.
- Report on implemented change requests and improvements based on stakeholder suggestions.
- Project phase completion reports.
- Project deviation reports.

### DEL 002 (M) — Technical documentation and key artifacts:
- Technical Architecture Document.
- SRS (Software Requirements Specification) and SDD (Software Design Document).
- Technical infrastructure requirements (dev, test/training, production).
- Integration documentation with shared government services, State Registers, RM public administration IS, and CNAS IS.
- API documentation for APIs exposed by SI.
- Complete source code (developed during project).
- Data migration plan and methodology.
- Production deployment plan.
- Access to version control repository.
- Data migration/population scripts.
- Deployment scripts and mechanisms.

### DEL 003 (M) — User guides:
- Administration guide.
- Installation/deployment/configuration guide.
- Current maintenance guide.
- Guide for removing common defects.
- User guide (per each application).
- Developer guide.
- Video instructions describing key business processes.
- Information security management system documents (BCP, DRP, Backup Plan, and other security procedures for operational period).

### DEL 004 (M) — Training deliverables:
- Training plan.
- Training schedule.
- Support materials for all categories of users (guides, presentations, video instructions).
- List of qualifications/skills for CNAS System Administrators.
- Final report on conducted training, including improvement suggestions received.

### DEL 005 (M) — Acceptance testing deliverables:
- SI test strategy and procedure.
- SI test plan.
- SI test scenarios.
- Acceptance protocol for interoperability and data exchange mechanism.
- Migration/population acceptance protocol.
- Test results protocol (functional, integration, performance, security, etc.).
- Final acceptance protocol.
- Handover protocol (SI to CNAS) with associated technical documentation.

### DEL 006 (M) — SLA agreement
For warranty, maintenance, technical support period, signed between supplier and CNAS.

### DEL 007 (M) — Delivery format
All SI deliverables transmitted on digital media (e.g., DVD+-R, USB stick).

### Knowledge-Transfer Services (Table 7.2)
- **DEL 008 (M)**: Supplier conducts training for CNAS trainers who will subsequently train all categories of users.
- **DEL 009 (M)**: Supplier conducts training for all categories of authorized users and System Administrators.
- **DEL 010 (M)**: Supplier provides technical assistance during stabilization period.
- **DEL 011 (M)**: Supplier assists CNAS in acceptance testing activities.
- **DEL 012 (M)**: Supplier provides services assisting CNAS in production deployment processes.
- **DEL 013 (M)**: Supplier eliminates all SI deficiencies and errors identified during training, acceptance testing, and stabilization.
- **DEL 014 (M)**: Supplier provides **post-implementation technical support** (after production launch) for **12 months**, including **corrective, adaptive, and preventive maintenance** per **SM ISO/CEI 14764:2015** (Software Engineering — Software Life Cycle Processes — Maintenance).

---

## 14. Acceptance Criteria for Deliverables (Section 7.2)

The deliverable acceptance process:

1. Supplier sends deliverable for review **at least 5 working days** before planned acceptance signing.
2. During examination period, supplier presents additional documents/justifications if requested, or signs acceptance and responds to all comments/suggestions/deficiencies.
3. Supplier's Project Manager sends all finalized deliverables to Approvers (Acquirer and CNAS authorized representative).
4. If deliverable must be rejected/returned due to deficiencies, Approvers must formulate non-conformity comments and explicitly indicate non-conformity issues or areas for supplier to address.
5. At no cost to Acquirer, supplier examines and resolves reported deficiencies within **maximum 5 working days** from rejection date.
6. Supplier resends adjusted deliverable to Approvers for examination and approval.
7. Approvers either accept or reject resent deliverable within **5 working days**. Deliverable considered accepted when signed by Approvers.
8. If Approvers neither accept nor reject within specified interval [text cuts at line 7260; final cycle continues but the explicit clause text is truncated at the read window].

---

## Cross-Cutting Notes / Key Findings

- **Methodology**: Iterative Waterfall, with CI/CD practices on top. PRINCE2 or PMBOK as PM framework. Iteration length: 1 month.
- **Greenfield mandate (DEV 008)**: No reuse of existing system architecture or technology — only used to analyze processes/data and identify constraints.
- **Language requirements**:
  - All PM communications and deliverables: Romanian (English on request).
  - Training materials and evaluation tests: Romanian and Russian.
  - Training sessions: Romanian and Russian.
  - Administrator guides: Romanian and English.
  - All other guides: Romanian mandatory.
- **Data sovereignty (MIG 009)**: Data never leaves CNAS ICT infrastructure during migration/population.
- **Test coverage target (UAT 005)**: ≥ 90% of functionalities covered.
- **Acceptance bar (UAT 006)**: 0 critical/blocking errors, fewer than 3 major errors/deficiencies.
- **Stabilization exit (STAB 004)**: All Level 1 fixed; <10 Level 2; no integrity/consistency issues.
- **Warranty support included**: 50 person-days of unplanned development effort during 12-month warranty (PIR 018).
- **Code signing**: Supplier must digitally sign source code packages and modifications.
- **Critical SLA**: 5-minute response, 60-minute resolution.
- **Maintenance windows**: Ordinary ≤ 4h with 5-day notice; Major ≤ 24h with 10-day notice; Urgent ≤ 2h, immediate notice.
- **Update/version cadence**: Updates monthly cap (except critical/security); new versions every 3 years; notification 1 month / 6 months in advance respectively.
- **Total team size**: At least 11 key roles defined; non-key team members for web dev, DB admin, integration, QA automation, training. Minimum tenures: 3-4 years at supplier.
- **Required certifications**: PM (PMP/Prince2/PM2), BA (CBAP), advantages: TOGAF/CTA, ISTQB, CompTIA Security+/CySA+/CISSP.
- **Training scale**: 2 System Admins (≥64h), 4 trainers (≥56h), up to 100 users (≥40h).
- **Integration scope**: Shared government IS (Msuite family, PGD), external IS, CNAS financial IS (FMS), and national platforms (MConnect, MPass, MPay, MSign).
