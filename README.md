# SalonCRM

CRM pre kozmetický salón — školský projekt (FEI TUKE). Online rezervácie, kalendár pracovníkov, zákazníci, platby, štatistiky a ML predikcia odchodu zákazníkov.

- Špecifikácia: [docs/SPEC.md](docs/SPEC.md)
- Plán a stav: [docs/PLAN.md](docs/PLAN.md)

## Architektúra

| Kontajner | Technológia | Port |
|---|---|---|
| `db-main` | PostgreSQL 17 (`salon_main`) | 5432 |
| `db-ml` | PostgreSQL 17 (`salon_ml`) | 5433 |
| `app` | ASP.NET Core (.NET 10) + zbuildený React frontend | 8080 |
| `ml` | Python FastAPI (scikit-learn) | 8000 |

## Spustenie (Docker)

```bash
cp .env.example .env
docker compose up --build      # → http://localhost:8080
docker compose down -v         # zastaví a zmaže aj dáta
```

## Vývoj

Predpoklady: Docker Desktop, .NET 10 SDK, Node.js LTS, Python 3.12+ a [uv](https://docs.astral.sh/uv/).

```bash
docker compose up -d db-main db-ml                 # iba databázy

dotnet run --project backend-main/src/SalonCrm.Api # main API → http://localhost:5080

cd backend-ml && uv sync && uv run alembic upgrade head
uv run uvicorn app.main:app --reload --port 8000   # ML API → http://localhost:8000

cd frontend && npm ci && npm run dev               # → http://localhost:5173
npm run lint && npm test && npm run build          # Node.js 24+
```

## Frontend (Fázy 6 a 7)

- `/` — verejné služby a cenník.
- `/rezervacia` — služba → pracovník alebo „ktokoľvek“ → dostupný čas → kontakt a GDPR → potvrdenie. Pri kolízii sa obnovia voľné časy a kontaktné údaje zostanú vyplnené.
- `/login` — prihlásenie personálu so serverovou session. Admin pokračuje na `/app/prehlad`, pracovník na `/app/kalendar`; sidebar a chránené trasy rešpektujú rolu.
- `/app/kalendar`, `/app/terminy` — slovenský kalendár a filtrované termíny; detail, preplánovanie, poznámka, dokončenie s platbou, zrušenie a neúčasť.
- `/app/terminy/novy` — telefonická rezervácia za zákazníka; bez hodinového predstihu, s overením dostupnosti na serveri.
- `/app/zakaznici` — vyhľadávanie, stránkovanie, kontakt a poznámky, história termínov a platieb; pracovník vidí iba svojich zákazníkov a svoju časť histórie.
- Admin: `/app/prehlad` (KPI a Recharts), `/app/pouzivatelia` (roly, služby, heslá, aktivácia a sessions), `/app/sluzby` (cenník). Deaktivácia zneplatní sessions; ďalší request s pôvodnou session vráti 401.

ML dashboard a odznaky rizika patria do Fázy 8. Podrobný postup kontroly pracovných obrazoviek: [docs/FRONTEND-QA.md](docs/FRONTEND-QA.md).

Pri štarte sa session obnovuje cez `/api/auth/me`. Anonymný návštevník môže rezervovať bez prihlásenia. Pri expirácii session alebo odpovedi 401 z main/ML API sa UI odhlási a presmeruje na prihlásenie.

Základný seed vytvorí admina `admin@salon.local` a pracovníčky vrátane `jana.kovacova@salon.local`. Heslá určuje `SEED_ADMIN_PASSWORD` / `SEED_USER_PASSWORD` v `.env` (v lokálnom .NET Development profile `Admin123!` / `User123!`). Dátumy sa zobrazujú v `Europe/Bratislava`, ceny v EUR.

Manuálna kontrola: vytvoriť anonymnú rezerváciu, v dvoch oknách skúsiť obsadiť rovnaký čas, prihlásiť obe roly, obnoviť chránenú stránku a odhlásiť sa. Automatické frontendové testy spustí `npm test`; bežia aj v CI.
