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

## Nasadenie na Azure

Produkcia beží na jednej Linux VM (Azure for Students) s tým istým `docker-compose.yml` ako lokálne (SPEC §11.1):

- Frontend + main API: http://saloncrm-tuke.denmarkeast.cloudapp.azure.com:8080
- ML API: http://saloncrm-tuke.denmarkeast.cloudapp.azure.com:8000/ml/health

| | |
|---|---|
| Región / veľkosť | `denmarkeast` (Azure for Students povoľuje iba niekoľko EÚ regiónov, B-séria bola dostupná len tu), `Standard_B2als_v2` (2 vCPU, 4 GB) + 2 GB swap |
| OS | Ubuntu 24.04, Docker + Compose plugin nainštalované cez `deploy/azure/cloud-init.yml` |
| NSG | povolené iba 22, 8080, 8000 — DB porty 5432/5433 sú zvonku zatvorené |
| `.env` na VM | nové náhodné heslá DB, `INTERNAL_API_KEY` a demo heslá; `VITE_ML_API_URL` a `CORS_ORIGINS` na hostname VM |

**Vytvorenie VM od nuly** (Azure CLI, Bash/Git Bash):

```bash
az login
./deploy/azure/create-vm.sh          # RG saloncrm-rg, VM saloncrm-vm, DNS label saloncrm-tuke
ssh azureuser@saloncrm-tuke.denmarkeast.cloudapp.azure.com
cd ~/SalonCRM && cp .env.example .env   # upraviť heslá, INTERNAL_API_KEY, VITE_ML_API_URL, CORS_ORIGINS
docker compose up -d --build
```

**Aktualizácia (ručné nasadenie):**

```bash
ssh azureuser@saloncrm-tuke.denmarkeast.cloudapp.azure.com
cd ~/SalonCRM && git pull && docker compose up -d --build
```

Šetrenie kreditu: `az vm deallocate -g saloncrm-rg -n saloncrm-vm` (zastaví VM, neplatí sa za výpočet) a `az vm start -g saloncrm-rg -n saloncrm-vm`. Zmazanie všetkého: `az group delete -n saloncrm-rg`.

HTTPS zatiaľ nie je (beží cez HTTP, `COOKIE_SECURE=false`).

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

## Frontend (Fázy 6 – 8)

- `/` — verejné služby a cenník.
- `/rezervacia` — služba → pracovník alebo „ktokoľvek“ → dostupný čas → kontakt a GDPR → potvrdenie. Pri kolízii sa obnovia voľné časy a kontaktné údaje zostanú vyplnené.
- `/login` — prihlásenie personálu so serverovou session. Admin pokračuje na `/app/prehlad`, pracovník na `/app/kalendar`; sidebar a chránené trasy rešpektujú rolu.
- `/app/kalendar`, `/app/terminy` — slovenský kalendár a filtrované termíny; detail, preplánovanie, poznámka, dokončenie s platbou, zrušenie a neúčasť.
- `/app/terminy/novy` — telefonická rezervácia za zákazníka; bez hodinového predstihu, s overením dostupnosti na serveri.
- `/app/zakaznici` — vyhľadávanie, stránkovanie, kontakt a poznámky, história termínov a platieb; pracovník vidí iba svojich zákazníkov a svoju časť histórie.
- Admin: `/app/prehlad` (KPI a Recharts), `/app/pouzivatelia` (roly, služby, heslá, aktivácia a sessions), `/app/sluzby` (cenník). Deaktivácia zneplatní sessions; ďalší request s pôvodnou session vráti 401.

- Admin: `/app/ml` — riziko odchodu: rozdelenie rizika, rizikoví zákazníci (mená z main cez `POST /api/customers/lookup`), model vs. baseline, dôležitosť príznakov, história behov, „Prepočítať predikcie“ a „Zaznamenať kontakt“ (retenčná akcia do main).
- Odznaky rizika v kalendári a zozname zákazníkov (`POST /ml/predictions/lookup`), v detaile zákazníka riziko, zdôvodnenie a graf vývoja; admin vidí aj retenčné akcie. Frontend volá `/api/*` aj `:8000/ml/*` priamo; pri nedostupnom ML sa odznaky len nezobrazia.

Podrobný postup kontroly pracovných obrazoviek: [docs/FRONTEND-QA.md](docs/FRONTEND-QA.md).

Pri štarte sa session obnovuje cez `/api/auth/me`. Anonymný návštevník môže rezervovať bez prihlásenia. Pri expirácii session alebo odpovedi 401 z main/ML API sa UI odhlási a presmeruje na prihlásenie.

Základný seed vytvorí admina `admin@salon.local` a pracovníčky vrátane `jana.kovacova@salon.local`. Heslá určuje `SEED_ADMIN_PASSWORD` / `SEED_USER_PASSWORD` v `.env` (v lokálnom .NET Development profile `Admin123!` / `User123!`). Dátumy sa zobrazujú v `Europe/Bratislava`, ceny v EUR.

Manuálna kontrola: vytvoriť anonymnú rezerváciu, v dvoch oknách skúsiť obsadiť rovnaký čas, prihlásiť obe roly, obnoviť chránenú stránku a odhlásiť sa. Automatické frontendové testy spustí `npm test`; bežia aj v CI.
