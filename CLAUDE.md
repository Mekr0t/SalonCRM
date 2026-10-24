# SalonCRM — CRM pre kozmetický salón

Školský projekt (FEI TUKE). Podnikový informačný systém pre kozmetický salón: online rezervácie, kalendár pracovníkov, evidencia zákazníkov, služieb a platieb, štatistiky a ML predikcia odchodu zákazníkov (churn).

- **Čo stavať** (dátový model, API, obrazovky, ML): `docs/SPEC.md` — zdroj pravdy
- **V akom poradí a čo je hotové**: `docs/PLAN.md`

## Zadanie a ako ho plníme

Každé rozhodnutie musí byť obhájiteľné voči týmto požiadavkám. Ak by zmena niektorú z nich oslabila, zastav sa a opýtaj sa.

| Požiadavka zo zadania | Riešenie |
|---|---|
| Podnikový IS, prepojenie aplikačných webových služieb | Dve REST služby, ktoré spolu komunikujú cez interné API |
| Prihlasovanie/odhlasovanie, ≥ 2 role, Sessions | Role `Admin` a `User`; server-side sessions v tabuľke `sessions`, HttpOnly cookie `salon_sid` |
| Hlavný backend obsluhuje ≥ 2 obrazovky | ASP.NET Core API — všetky obrazovky okrem ML dashboardu |
| Druhý backend v inom jazyku, nemusí mať obrazovku | Python FastAPI — iba API, žiadne UI |
| Frontend spojí oba backendy | React volá **priamo** `/api/*` (main) aj `/ml/*` (ml); niektoré obrazovky kombinujú dáta z oboch |
| Práca s 2 separátnymi SQL databázami | 2 inštancie PostgreSQL: `salon_main` a `salon_ml`; každý backend pristupuje len k svojej |
| Machine Learning (5 b) | Predikcia odchodu zákazníka (scikit-learn) v ML backende |
| BPMN (5 b) | ≥ 60 činností, 2 bazény, jeden s ≥ 3 dráhami, ≥ 1 podproces, viac brán — `docs/bpmn/` |
| Nasadenie (5 b) | Docker Compose, základ **4 kontajnery**: `db-main`, `db-ml`, `app` (.NET + zbuildený React), `ml` (viac je povolené — potvrdil vyučujúci); produkcia na Azure VM (SPEC §11.1) |

Bodovanie: IS 15 b (backend 10 + frontend 5), ML 5 b, BPMN 5 b, nasadenie 5 b.

## Architektúra

```text
 Prehliadač (React SPA)
   │  /api/*   same-origin, cookie salon_sid
   ▼
 app: ASP.NET Core :8080 ─────────────▶ db-main: PostgreSQL (salon_main)
   ▲  servíruje aj zbuildený frontend
   │
   │  interné API, hlavička X-Internal-Api-Key:
   │   • POST /api/internal/sessions/introspect   (overenie session a roly)
   │   • GET  /api/internal/ml/export             (pseudonymizované návštevy)
   │
 ml: FastAPI :8000 ───────────────────▶ db-ml: PostgreSQL (salon_ml)
   ▲
   │  /ml/*   CORS s credentials, cookie salon_sid
 Prehliadač (React SPA)
```

Toky dát:
1. **Frontend → main**: prihlásenie, rezervácie, termíny, zákazníci, používatelia, služby, štatistiky, retenčné akcie.
2. **Frontend → ml**: ML dashboard, odznaky rizika odchodu, história predikcií. Cookie `salon_sid` sa posiela aj na port 8000.
3. **ml → main**: introspect session pri chránených ML requestoch; stiahnutie dát na tréning.
4. main **nikdy** nečíta `salon_ml` a ml **nikdy** nečíta `salon_main`. Jediné prepojenie je HTTP.

## Roly

- **Zákazník** — nemá účet ani rolu. Rezervuje cez verejný formulár; identifikuje sa e-mailom (find-or-create podľa normalizovaného e-mailu).
- **User** (pracovník, napr. kozmetička) — vidí a spravuje **iba svoje** termíny a zákazníkov, ktorých obsluhoval.
- **Admin** — vidí všetko; spravuje používateľov, služby a cenník, štatistiky, ML a retenčné akcie.

Presná matica oprávnení: `docs/SPEC.md` §2.

## Tech stack

| Časť | Technológie |
|---|---|
| Main backend | .NET 10 (LTS), ASP.NET Core Web API (controllers), EF Core + Npgsql, EFCore.NamingConventions (snake_case), xUnit + Testcontainers |
| ML backend | Python 3.12+, uv, FastAPI, Pydantic v2 + pydantic-settings, SQLAlchemy 2 (sync), Alembic, httpx, pandas, scikit-learn, joblib, pytest, ruff |
| Frontend | Vite + React + TypeScript, React Router, TanStack Query, react-hook-form + zod, Tailwind + shadcn/ui, FullCalendar (iba MIT balíčky: core, daygrid, timegrid, interaction), Recharts |
| Databázy | PostgreSQL 17 (2 inštancie) |
| Nasadenie | Docker Compose |

## Štruktúra repozitára

```text
/
├─ CLAUDE.md
├─ docs/              SPEC.md, PLAN.md, bpmn/, obhajoba.md
├─ backend-main/      src/SalonCrm.Api, tests/SalonCrm.Api.Tests, Dockerfile (build context = koreň repa)
├─ backend-ml/        app/, alembic/, tests/, pyproject.toml, Dockerfile
├─ frontend/          Vite projekt
├─ docker-compose.yml
├─ .env.example
└─ .gitattributes     (*.sh text eol=lf)
```

- **Main backend** — vertikálne rezy, bez zbytočných vrstiev: `Domain/` (entity, enumy) · `Data/` (AppDbContext, Migrations, Seed) · `Auth/` (session handler, hashovanie) · `Features/<Oblasť>/` (Controller + DTO + Service).
- **ML backend** — `app/main.py`, `config.py`, `db.py`, `models.py`, `auth.py`, `clients/main_api.py`, `ml/` (features, labels, train, predict, explain), `routers/`.
- **Frontend** — `src/api/` (dva klienti: `mainApi`, `mlApi`), `src/auth/`, `src/features/<oblasť>/`, `src/components/`, `src/lib/format.ts`.

## Príkazy

> Udržuj túto sekciu aktuálnu — po scaffoldingu ju oprav podľa reality.

```bash
# Databázy (dev): salon_main na :5432, salon_ml na :5433
docker compose up -d db-main db-ml

# Main backend (dev na :5080)
dotnet run --project backend-main/src/SalonCrm.Api
dotnet test backend-main                     # integračné testy cez Testcontainers → vyžaduje bežiaci Docker
dotnet ef migrations add <Nazov> --project backend-main/src/SalonCrm.Api   # vyžaduje: dotnet tool install --global dotnet-ef

# ML backend (dev na :8000)
cd backend-ml && uv sync
uv run alembic upgrade head
uv run uvicorn app.main:app --reload --port 8000
uv run pytest && uv run ruff check .   # pytest vyžaduje bežiaci Docker (Testcontainers spúšťa PostgreSQL 17)

# Frontend (dev na :5173, /api je proxy na :5080, ML sa volá priamo na :8000)
cd frontend && npm ci && npm run dev
npm run build && npm run lint && npm test  # testy cez vstavaný Node.js runner, Node 24+

# Celý systém v Dockeri → http://localhost:8080
docker compose up --build
docker compose down -v        # zmaže aj dáta
```

## Konvencie

- Identifikátory, DB, API a commit správy po anglicky; **texty v UI po slovensky s diakritikou**.
- DB: snake_case, primárne kľúče `uuid`, enumy uložené ako text, peniaze `numeric(10,2)` v EUR.
- JSON: camelCase v **oboch** backendoch (vo FastAPI Pydantic `alias_generator=to_camel`, `populate_by_name=True`), enumy ako stringy, časy ISO 8601 v UTC.
- Čas: v DB `timestamptz` v UTC. Otváracie hodiny, sloty a zobrazovanie v `Europe/Bratislava` (pozor na prechod letného času). V .NET používaj `TimeProvider`, nie `DateTime.Now`.
- Chyby: oba backendy vracajú ProblemDetails (`type`, `title`, `status`, `detail`). 401 = neprihlásený, 403 = zlá rola, 404 = neexistuje **alebo** nepatrí Userovi (neprezrádzame existenciu), 409 = kolízia termínu alebo neplatný prechod stavu.
- Autorizácia explicitne na každom endpointe (`[Authorize(Roles = ...)]` / `[AllowAnonymous]`, vo FastAPI dependency). Obmedzenie Usera na vlastné dáta rieš v databázovom dotaze, nie vo frontende.
- Konfigurácia a tajomstvá iba cez env premenné; každú novú premennú pridaj do `.env.example`.

## Pravidlá práce

1. `docs/SPEC.md` je zdroj pravdy. Ak meníš kontrakt API alebo DB schému, uprav SPEC v tom istom commite.
2. Pracuj po fázach z `docs/PLAN.md`. Nerob veci z budúcich fáz; po dokončení odškrtni úlohy.
3. Ak je niečo nejasné alebo v rozpore so zadaním, **opýtaj sa** — nevymýšľaj požiadavky.
4. Drž to jednoduché: žiadna Clean Architecture/CQRS/MediatR, žiadny repository pattern nad EF Core, žiadny AutoMapper, žiadny message broker. Ďalší kontajner (zadanie určuje minimum, nie maximum) pridaj iba s jasným dôvodom (napr. reverse proxy pre HTTPS) a po odsúhlasení.
5. Novú knižnicu mimo stacku pridaj až po odsúhlasení.
6. Pred tým, ako povieš „hotovo“, spusti build, testy a lint dotknutých častí a napíš výsledok. Ak niečo padá, povedz to.
7. Migrácie generuj nástrojmi (`dotnet ef`, `alembic revision --autogenerate`), skontroluj ich a nikdy neupravuj už aplikovanú migráciu.
8. Napis mi commit message po každej ucelenej časti (Conventional Commits).

## Pasce

- **Npgsql + `timestamptz`**: zapisuj iba `DateTime` s `Kind=Utc` (alebo `DateTimeOffset` s offsetom 0), inak Npgsql vyhodí výnimku.
- **Prekrývanie termínov** stráži DB exclusion constraint (vyžaduje `CREATE EXTENSION btree_gist`, pridať cez `migrationBuilder.Sql`). Porušenie = `PostgresException.SqlState == "23P01"` → vráť 409.
- **Cookie medzi portmi**: `localhost:8080` a `localhost:8000` sú rôzne originy, ale rovnaký site → cookie so `SameSite=Lax` sa na ml pošle pri `fetch(..., { credentials: 'include' })`. ML musí mať CORS s konkrétnymi originmi a `allow_credentials=True` (nie `*`).
- **ML a leakage**: príznaky k dátumu T počítaj iba z dát známych k T (vrátane `created_at`/`cancelled_at` budúcich termínov). Na to existuje test — nevypínaj ho. Detail: SPEC §8.4.
- **SPA fallback**: `MapFallbackToFile("index.html")` nesmie zachytiť `/api/*` — neznáma API cesta musí vrátiť 404 JSON, nie HTML.
- **Časové pásmo v kontajneri**: použi štandardný (nie chiseled/alpine) aspnet image. Ak `TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava")` v kontajneri zlyhá, chýba tzdata.
- **Windows**: `.sh` súbory musia mať LF (`.gitattributes`), inak entrypoint v Linux kontajneri padne.
