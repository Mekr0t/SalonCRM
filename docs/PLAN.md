# SalonCRM — Plán implementácie

## Ako začať

1. **Predpoklady:** Docker Desktop, .NET 10 SDK, Node.js LTS, Python 3.12+ a `uv`, `dotnet-ef` (`dotnet tool install --global dotnet-ef`), Git. Na BPMN Camunda Modeler (zadarmo).
2. Prázdny priečinok → `git init` → `CLAUDE.md` do koreňa, `SPEC.md` a `PLAN.md` do `docs/` → prvý commit.
3. Spusti Claude Code v koreni repa a zadaj univerzálny prompt nižšie pre Fázu 0.

## Ako pracovať s Claude Code

- **Jedna fáza = jedna čerstvá session.** Kontext nesie CLAUDE.md, SPEC a odškrtnutý stav v tomto súbore — nepotrebuješ ťahať dlhú konverzáciu.
- **Väčšie fázy začni v plan móde**: nechaj si ukázať plán, oprav ho, až potom implementácia.
- **„Hotovo, keď“ over sám** — preklikaj si to, nestačí „testy prešli“.
- Ak Claude navrhne niečo mimo SPEC, buď to odmietni, alebo nech najprv upraví SPEC a ty to schváliš.

Univerzálny prompt (zmeň len číslo fázy):

```text
Pracujeme na Fáze N z docs/PLAN.md.
1. Prečítaj CLAUDE.md, úlohy Fázy N a sekcie docs/SPEC.md, na ktoré odkazuje.
2. Navrhni plán: súbory, kroky, nejasnosti. Ak je v SPEC niečo nejasné alebo v rozpore, opýtaj sa.
3. Po schválení implementuj. Spusti build, testy a lint a oprav chyby.
4. Odškrtni hotové úlohy Fázy N, aktualizuj sekciu Príkazy v CLAUDE.md, ak sa zmenila, a urob commit.
```

---

## Fáza 0 — Repozitár a databázy
SPEC: §11

- [x] Štruktúra priečinkov podľa CLAUDE.md, `.gitignore` (.NET, Python, Node, `.env`), `.gitattributes` (`* text=auto`, `*.sh text eol=lf`)
- [x] `docker-compose.yml` zatiaľ iba s `db-main` a `db-ml` (PostgreSQL 17, volumes, healthchecky, porty 5432 / 5433)
- [x] `.env.example`, krátky `README.md`

**Hotovo, keď:** `docker compose up -d db-main db-ml` → oba kontajnery `healthy`, dá sa pripojiť na `salon_main` aj `salon_ml`.

## Fáza 1 — Main backend: kostra a dátový model
SPEC: §3, §6 (`/api/health`)

- [x] Solution, `SalonCrm.Api` (Web API s controllermi), `SalonCrm.Api.Tests` (xUnit + Testcontainers.PostgreSql); dev port 5080 v `launchSettings.json`
- [x] EF Core + Npgsql + snake_case, všetky entity z §3, enumy ako text, `AppDbContext`
- [x] Úvodná migrácia vrátane `btree_gist` a exclusion constraintu
- [x] ProblemDetails, OpenAPI dokument + UI v Development, `TimeProvider` v DI
- [x] Migrácie pri štarte (s retry na dostupnosť DB), základný seed: admin, 4 pracovníčky, služby, `user_services`

**Hotovo, keď:** `dotnet build` a `dotnet test` prejdú, `/api/health` vráti 200, tabuľky aj constraint existujú v DB.

## Fáza 2 — Prihlásenie, sessions, roly, používatelia
SPEC: §2, §4.5, §5, §6 (auth, admin/users, admin/sessions)

- [x] `SessionAuthenticationHandler`, login / logout / me, hashovanie tokenov, nečinnosť a absolútna expirácia, rate limit na login
- [x] 401 / 403 ako JSON (bez redirectov)
- [x] Admin: používatelia (bez mazania, priradenie služieb, deaktivácia rušiaca sessions, reset hesla), zoznam a rušenie sessions
- [x] Integračné testy: login nastaví cookie; `me` funguje; po logoute 401; deaktivovaný používateľ stratí session; User na `/api/admin/*` → 403; posledný admin sa nedá deaktivovať

**Hotovo, keď:** testy prejdú a cez OpenAPI UI sa dá prihlásiť, zavolať chránený endpoint a odhlásiť.

## Fáza 3 — Rezervácie a jadro CRM
SPEC: §4, §6 (public, appointments, customers, admin/services, admin/stats)

- [x] Správa služieb (admin), verejné služby, pracovníci pre službu, dostupnosť slotov
- [x] Verejná rezervácia (find-or-create zákazníka, súhlas GDPR, rate limit), 409 pri kolízii (`23P01`)
- [x] Termíny: zoznam, detail, vytvorenie personálom, preplánovanie, complete s platbou, cancel, no-show — so scopingom pre User
- [x] Zákazníci: zoznam, detail s históriou, úprava, `lookup` — so scopingom
- [x] Štatistiky pre admina
- [x] Check constrainty pre enum stĺpce (`users.role`, `services.category`, `appointments.status`/`source`, `payments.method`, `retention_actions.channel`) — nová migrácia; DB odmietne neplatnú hodnotu aj mimo API
- [x] Testy: prekrývajúce sa rezervácie → 409; sloty rešpektujú otváracie hodiny, trvanie a existujúce termíny; User nevidí cudzí termín ani zákazníka (404); complete vytvorí platbu; neplatné prechody stavov → 409

**Hotovo, keď:** tok „rezervácia → dokončenie s platbou → štatistika“ funguje cez API a testy prejdú.

## Fáza 4 — Demo dáta a interné API
SPEC: §7, §10

- [x] Seeder histórie podľa §10 (deterministický, iba nad prázdnou DB)
- [x] Filter pre `X-Internal-Api-Key`, `POST /api/internal/sessions/introspect`, `GET /api/internal/ml/export`
- [x] Testy: export neobsahuje osobné údaje; zlý alebo chýbajúci kľúč → 401; introspect dodržiava pravidlá platnosti session

**Hotovo, keď:** po `docker compose down -v` a štarte je v DB ~300 zákazníkov a tisíce termínov, kalendáre na 3 týždne dopredu sú naplnené, žiadne prekryvy, export vráti JSON podľa §7.

## Fáza 5 — ML backend
SPEC: §8

- [x] FastAPI projekt (uv, ruff, pytest), konfigurácia cez pydantic-settings, SQLAlchemy 2 + Alembic, tabuľky §8.2
- [x] camelCase JSON, chyby vo formáte ProblemDetails, CORS s credentials
- [x] `current_user` cez introspect (+ 30 s cache), `require_role`
- [x] Pipeline: sync → snapshoty → príznaky → labely → kandidáti + baseline → výber → uloženie → predikcia → zdôvodnenia
- [x] Endpointy §8.7, OpenAPI na `/docs`
- [x] Testy: leakage test (§8.4); labely na ručne zostavenom mini datasete; hranice úrovní rizika; auth s mockovaným introspectom; pipeline end-to-end nad DB na syntetickom datasete

**Hotovo, keď:** prihlásený admin (cookie z main) spustí `POST /ml/pipeline/run` a dostane metriky víťaza aj baseline; User na admin endpointy → 403; bez cookie → 401. Ak model neprekoná baseline, Claude to má nahlásiť a vysvetliť prečo — nie potichu ladiť dáta.

## Fáza 6 — Frontend: kostra, prihlásenie, verejná rezervácia
SPEC: §9

- [x] Vite + React + TS, Tailwind + shadcn/ui, React Router, TanStack Query, react-hook-form + zod, ESLint
- [x] `mainApi` a `mlApi` (credentials include, spracovanie ProblemDetails, 401 → login), Vite proxy `/api` → `http://localhost:5080`
- [x] AuthProvider, ProtectedRoute s rolami, layout so sidebarom podľa roly, `lib/format.ts`
- [x] Stránky `/`, `/rezervacia` (sprievodca), `/login`
- [x] Odstránenie Vite šablóny (`App.css`, `src/assets/`, frontendový README a pôvodná ikona)
- [x] Frontendové testy API klienta, validácie rezervácie a dátumov (`npm test`, aj v CI)

Anonymná úvodná kontrola `/auth/me` s odpoveďou 401 ponechá verejné stránky dostupné. Pri strate existujúcej session alebo 401 z iného API sa UI odhlási a prejde na `/login`.

**Hotovo, keď:** anonymne sa dá rezervovať (kolízia → zrozumiteľná hláška), admin aj pracovníčka sa prihlásia, refresh stránky drží session, odhlásenie funguje.

## Fáza 7 — Frontend: pracovník a admin
SPEC: §9

- [x] Kalendár (FullCalendar timeGrid, slovenčina, Europe/Bratislava), detail termínu s akciami a platbou, telefonická rezervácia, zoznam a detail zákazníka
- [x] Admin: prehľad so štatistikami (Recharts), všetky termíny s filtrami, používatelia (vrátane aktívnych sessions), služby a cenník
- [x] Loading, error a empty stavy, toasty a potvrdenie deštruktívnych akcií
- [x] Frontendové testy: bratislavský čas a prechod DST, rozsahy dátumov, akcie podľa stavu/roly, platby, validácia zákazníka a stránkovanie

Obrazovky sa načítavajú po jednotlivých trasách. Telefonická rezervácia a preplánovanie overujú zadaný čas na serveri bez verejného 60-minútového predstihu. Sessions sa overujú pri requestoch a návrate do okna; automatický polling by obnovoval čas aktivity a bránil expirácii pri nečinnosti. ML prvky ostávajú vo Fáze 8. Postup ručného overenia je v `docs/FRONTEND-QA.md`.

**Hotovo, keď:** pracovníčka vidí iba svoje termíny a zákazníkov; admin všetko; deaktivácia používateľa ho okamžite odhlási.

## Fáza 8 — ML v UI (frontend spája oba backendy)
SPEC: §6 (retenčné akcie), §8.7, §9

- [x] ML dashboard `/app/ml`: rozdelenie rizika, rizikoví zákazníci s menami z main, model vs. baseline, dôležitosť príznakov, história behov, „Prepočítať predikcie“, prázdny stav bez modelu
- [x] Odznaky rizika v kalendári a v zozname zákazníkov (`/ml/predictions/lookup`); riziko, zdôvodnenie a graf vývoja v detaile zákazníka
- [x] Retenčné akcie: endpointy v main + dialóg „Zaznamenať kontakt“ z ML dashboardu (aj z detailu zákazníka)
- [x] Testy: integračné testy retenčných akcií (main), frontendové testy ML pomocných funkcií

Nedostupná ML služba nezablokuje kalendár, zoznam ani detail zákazníka — odznaky sa len nezobrazia. V kalendári je štítok iba pri strednom a vysokom riziku; presná úroveň a pravdepodobnosť sú v tooltipe.

**Hotovo, keď:** na ML dashboarde vidno v Network tabe requesty na `/api/*` aj na `:8000/ml/*` a zaznamenaný kontakt sa zobrazí v detaile zákazníka.

## Fáza 9 — Nasadenie
SPEC: §11

- [x] Dockerfile pre `app` (multi-stage: frontend build → dotnet publish → runtime s `wwwroot`), Dockerfile pre `ml`, SPA fallback mimo `/api`
- [x] Kompletný `docker-compose.yml` (4 kontajnery, healthchecky, `depends_on`, volumes, env z `.env`)
- [ ] README: spustenie jedným príkazom, demo účty, porty
- [ ] Azure VM (§11.1): vytvorenie VM + DNS label + NSG, inštalácia Dockeru, `.env` pre produkciu, nasadenie, návod v README
- [ ] Ak pribudne reverse proxy: `ForwardedHeaders` s dôveryhodnou proxy, inak rate limit prihlásenia zdieľajú všetci klienti (vidí iba IP proxy)
- [ ] Rozhodnúť HTTPS (reverse proxy ako ďalší kontajner — vyučujúci potvrdil, že viac ako 4 kontajnery je OK) a ručné vs. automatické nasadenie (CD)

**Hotovo, keď:** čistý klon → `cp .env.example .env` → `docker compose up --build` → na http://localhost:8080 funguje všetko vrátane ML; po `docker compose down -v` a novom štarte sa dáta znova vytvoria. To isté funguje na Azure VM cez jej hostname.

## Fáza 10 — BPMN

- [ ] `docs/bpmn/proces.md` — textový návrh procesu:
  - bazén **Zákazník** a bazén **Kozmetický salón** s dráhami Admin (recepcia), Kozmetička, CRM systém, ML služba
  - číslované činnosti, cieľ **≥ 65** (rezerva nad 60), tabuľka počtov po dráhach
  - podproces „Vykonanie ošetrenia“ (konzultácia, kontrola kontraindikácií, diagnostika pleti, …), ideálne aj „Analýza rizika odchodu“
  - exkluzívne aj paralelné brány, správy medzi bazénmi, štartovacie, koncové a časové udalosti
  - proces zodpovedá tomu, čo systém reálne robí: rezervácia → potvrdenie → ošetrenie → platba → evidencia → periodická analýza rizika → retenčný kontakt
- [ ] Voliteľne `docs/bpmn/salon-crm.bpmn` (BPMN 2.0 XML) ako východisko; rozloženie dorovnať ručne v Camunda Modeleri

**Hotovo, keď:** súbor sa otvorí v Camunda Modeleri bez chýb a počty spĺňajú zadanie. Over si u vyučujúceho, či sa činnosti vnútri podprocesu rátajú do 60.

## Fáza 11 — Odovzdanie a obhajoba

- [ ] README: architektúra (Mermaid), tabuľka „požiadavka zo zadania → kde je implementovaná“ (s cestami k súborom), spustenie, demo účty
- [ ] `docs/obhajoba.md`: odpovede na pravdepodobné otázky — prečo server-side sessions, ako komunikujú služby a ako ML overuje používateľa, prečo snapshoty a validácia v čase, čo znamenajú metriky, prečo syntetické dáta, aké sú obmedzenia
- [ ] Upratanie: žiadne TODO, žiadne tajomstvá v repe, všetky testy a lint zelené

**Hotovo, keď:** vieš systém predviesť od rezervácie po retenčný kontakt za 5 minút.
