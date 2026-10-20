# SalonCRM — Špecifikácia

Zdroj pravdy pre implementáciu. Pri zmene kontraktu API alebo DB schémy uprav aj tento súbor.

## 1. Rozsah

**MVP (povinné):** verejná online rezervácia, prihlásenie so server-side sessions, kalendár a správa termínov pracovníka, evidencia platieb, zákazníci s históriou, správa používateľov a služieb, štatistiky, ML predikcia odchodu s dashboardom, retenčné akcie, nasadenie v Dockeri, BPMN.

**Voliteľné (až po MVP):** zrušenie rezervácie zákazníkom cez odkaz s tokenom; hodnotenie návštevy cez odkaz (1–5) + príznak `avg_rating` v ML; absencie pracovníkov.

**Mimo rozsahu:** e-mailové/SMS notifikácie (mail server by bol ďalší kontajner a mimo rozsahu MVP), online platby, zákaznícke účty, viac pobočiek.

## 2. Roly a oprávnenia

| Akcia | Verejnosť | User | Admin |
|---|---|---|---|
| Zobraziť služby a voľné termíny, vytvoriť rezerváciu | ✔ | ✔ | ✔ |
| Zobraziť termíny | – | len svoje | všetky |
| Zmeniť stav termínu, preplánovať | – | len svoje | všetky |
| Vytvoriť termín za zákazníka (telefonicky) | – | pre seba | pre kohokoľvek |
| Zákazníci — zoznam a detail | – | len svoji¹ | všetci |
| Úprava kontaktu a poznámok zákazníka | – | len svoji¹ | ✔ |
| Používatelia, priradenie služieb, sessions | – | – | ✔ |
| Služby a cenník | – | – | ✔ |
| Štatistiky | – | – | ✔ |
| ML: riziko konkrétnych zákazníkov | – | ✔ | ✔ |
| ML: dashboard, spustenie pipeline, metriky modelu | – | – | ✔ |
| Retenčné akcie | – | – | ✔ |

¹ Zákazník, ktorý má s daným pracovníkom aspoň jeden termín. V detaile vidí User iba históriu **svojich** termínov.

## 3. Dátový model (`salon_main`)

Názvy v snake_case, PK `uuid`, časy `timestamptz` v UTC, enumy ako text.

### users
| Stĺpec | Typ | Poznámka |
|---|---|---|
| id | uuid PK | |
| email | text, unique | ukladaný v lowercase |
| password_hash | text | `PasswordHasher<User>` |
| full_name | text | |
| role | text | `Admin` \| `User` |
| is_active | bool | deaktivácia namiesto mazania |
| created_at | timestamptz | |
| last_login_at | timestamptz null | |

### sessions
| Stĺpec | Typ | Poznámka |
|---|---|---|
| id | uuid PK | |
| user_id | uuid FK → users | |
| token_hash | text, unique | SHA-256 (hex) hodnoty cookie; samotný token sa neukladá |
| created_at | timestamptz | |
| last_seen_at | timestamptz | |
| expires_at | timestamptz | absolútna expirácia |
| revoked_at | timestamptz null | |
| ip_address | text null | |
| user_agent | text null | |

### services
| Stĺpec | Typ | Poznámka |
|---|---|---|
| id | uuid PK | |
| name | text | |
| category | text | `Skin`, `Nails`, `BrowsLashes`, `Waxing`, `Massage`, `Makeup` |
| description | text null | |
| duration_minutes | int | násobok 15 |
| price | numeric(10,2) | EUR |
| is_active | bool | |

### user_services
`(user_id, service_id)` — kompozitný PK. Ktorý pracovník vykonáva ktorú službu.

### customers
| Stĺpec | Typ | Poznámka |
|---|---|---|
| id | uuid PK | |
| email | text | tak, ako ho zadal zákazník |
| email_normalized | text, unique | trim + lowercase |
| full_name | text | |
| phone | text null | |
| notes | text null | interné poznámky (typ pleti, preferencie) |
| gdpr_consent_at | timestamptz | |
| created_at | timestamptz | |

### appointments
| Stĺpec | Typ | Poznámka |
|---|---|---|
| id | uuid PK | |
| customer_id | uuid FK → customers | |
| employee_id | uuid FK → users | |
| service_id | uuid FK → services | |
| starts_at, ends_at | timestamptz | `ends_at = starts_at + duration_minutes` |
| status | text | `Scheduled`, `Completed`, `Cancelled`, `NoShow` |
| price | numeric(10,2) | cena služby v čase rezervácie |
| source | text | `Online`, `Staff` |
| customer_note | text null | |
| staff_note | text null | |
| created_at, updated_at | timestamptz | |
| completed_at, cancelled_at | timestamptz null | |

Indexy: `(employee_id, starts_at)`, `(customer_id, starts_at)`, `(status)`.

Check constrainty: `ck_appointments_ends_after_starts` (`ends_at > starts_at`), `ck_appointments_price` (`price >= 0`), `ck_services_duration_minutes` (`duration_minutes > 0 AND duration_minutes % 15 = 0`), `ck_services_price` (`price >= 0`), `ck_payments_amount` (`amount >= 0`; zľava znižuje sumu, ale nikdy nie pod 0). Každý enum stĺpec má check constraint s povolenými hodnotami enumu (`ck_users_role`, `ck_services_category`, `ck_appointments_status`, `ck_appointments_source`, `ck_payments_method`, `ck_retention_actions_channel`).

Okrem uvedených indexov EF vytvára index na každý cudzí kľúč (napr. `sessions.user_id`, `appointments.service_id`). Všetky cudzie kľúče sú `ON DELETE RESTRICT` — nič sa nemaže, iba deaktivuje.

Ochrana proti prekryvu (v migrácii cez `migrationBuilder.Sql`):

```sql
CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE appointments ADD CONSTRAINT ex_appointments_no_overlap
  EXCLUDE USING gist (employee_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
  WHERE (status <> 'Cancelled');
```

### payments
| Stĺpec | Typ | Poznámka |
|---|---|---|
| id | uuid PK | |
| appointment_id | uuid FK, unique | |
| amount | numeric(10,2) | |
| method | text | `Cash`, `Card` |
| paid_at | timestamptz | |
| recorded_by_user_id | uuid FK → users | |

### retention_actions
| Stĺpec | Typ | Poznámka |
|---|---|---|
| id | uuid PK | |
| customer_id | uuid FK → customers | |
| created_by_user_id | uuid FK → users | |
| channel | text | `Phone`, `Email`, `Sms`, `InPerson` |
| note | text | |
| churn_probability | numeric(4,3) null | hodnota z ML v čase kontaktu |
| created_at | timestamptz | |

## 4. Biznis pravidlá

### 4.1 Otváracie hodiny a sloty
- Konfigurácia `Salon` v appsettings: `TimeZone` (`Europe/Bratislava`), `SlotStepMinutes` (15), `MinLeadMinutes` (60), `MaxDaysAhead` (60), `OpeningHours` po dňoch v týždni (`{ "Open": "08:00", "Close": "18:00" }`): Po–Pi 8:00–18:00, So 8:00–13:00; chýbajúci deň (Ne) = zatvorené. Validuje sa pri štarte.
- Krok slotov 15 min od otvorenia. Najskorší začiatok = teraz + 60 min pre verejnosť; personál (telefonická rezervácia, preplánovanie) predstih nemá, ale nesmie ísť do minulosti (najskôr = teraz). Najneskôr 60 dní dopredu (dátumy v časovom pásme salónu).
- Slot je voľný pre službu S a pracovníka E, ak: začiatok aj koniec sú v otváracích hodinách, E je aktívny, E má S v `user_services` (bez ohľadu na rolu) a E nemá prekrývajúci sa termín so stavom ≠ `Cancelled`.
- Ak zákazník pracovníka nevyberie („ktokoľvek“), priradí sa voľný pracovník s najmenším počtom termínov (≠ `Cancelled`) v daný deň, pri zhode podľa mena, potom podľa id — deterministicky.
- Prechod letného času je v nedeľu v noci (salón zatvorený), mení sa však posun voči UTC — sloty sa počítajú v lokálnom čase a prevádzajú do UTC.
- `GET /api/public/availability` pre zatvorený deň alebo dátum mimo okna (minulosť, > 60 dní) vráti prázdny zoznam.

### 4.2 Verejná rezervácia
- Vstup: `serviceId`, `employeeId?`, `startsAt`, `fullName`, `email`, `phone?`, `note?`, `gdprConsent` (musí byť `true`).
- Zákazník: find-or-create podľa `email_normalized`. Existujúcemu sa meno neprepisuje; telefón sa doplní, ak chýbal.
- Vznikne termín `Scheduled`, `source = Online`, `price` = aktuálna cena služby.
- `gdprConsent` ≠ `true` → 400. Existujúcemu zákazníkovi ostáva pôvodné `gdpr_consent_at`.
- `startsAt`, ktorý nikdy nemôže byť slotom (mimo otváracích hodín, mimo mriežky 15 min, pred najskorším začiatkom, za 60 dní) → 400 „Neplatný čas termínu.“
- Platný slot, ktorý nie je voľný (pre zvoleného alebo žiadneho pracovníka), → 409 „Vybraný termín už nie je voľný.“ Slot sa overí znova pri zápise; súbeh zachytí exclusion constraint (`23P01`) → rovnaká 409. Súbežné vytvorenie toho istého nového zákazníka (unique `email_normalized`) sa zachytí a použije sa existujúci záznam.
- Neaktívna alebo neexistujúca služba → 404.
- Rate limit: 10 rezervácií / 10 min / IP (`RateLimiting:BookingPermitLimit`) → 429.

### 4.3 Stavy termínu

```text
Scheduled ──▶ Completed   len ak starts_at <= teraz; v tej istej transakcii vznikne payment
Scheduled ──▶ NoShow      len ak starts_at <= teraz
Scheduled ──▶ Cancelled   User pred starts_at, Admin kedykoľvek
Completed, NoShow, Cancelled = konečné stavy
```

- Pri `Completed` je povinné `paymentMethod`; `amount` je predvolene `price` (dá sa upraviť, napr. zľava alebo doplnková služba; 0–10 000 €, max. 2 desatinné miesta).
- Preplánovanie (`startsAt`, Admin aj `employeeId`) iba v stave `Scheduled`, s rovnakou kontrolou slotu ako telefonická rezervácia (bez predstihu, nie do minulosti); preplánovávaný termín sa z obsadených intervalov vynecháva. `staffNote` sa dá upraviť v ľubovoľnom stave. V `PATCH` chýbajúce pole alebo `null` = nemeniť, prázdny reťazec = vymazať.
- Neplatný prechod stavu → 409. Prechod je atomický (podmienený `UPDATE … WHERE status = 'Scheduled'`), takže pri súbehu (napr. complete a cancel naraz) uspeje práve jeden.
- Telefonická rezervácia (`POST /api/appointments`): rovnaké vstupy ako verejná (zákazník find-or-create podľa e-mailu, `gdprConsent: true` = pracovník potvrdzuje, že súhlas získal), `source = Staff`, bez predstihu 60 min, nie do minulosti. User rezervuje vždy pre seba; Admin pre zvoleného pracovníka, bez `employeeId` sa priradí podľa pravidla „ktokoľvek“ (§4.1).

### 4.4 Rozsah dát pre rolu User
- Termíny: `employee_id = aktuálny používateľ`.
- Zákazníci: tí, ktorí majú aspoň jeden termín s aktuálnym používateľom; v detaile iba história jeho termínov.
- Cudzí záznam → 404. Filter je vždy súčasťou databázového dotazu.
- User, ktorý pošle `employeeId` iného pracovníka (filter zoznamu termínov, vytvorenie, preplánovanie), dostane 403 — nejde o konkrétny záznam, ale o akciu mimo jeho roly.
- Počty návštev a útrata v zozname aj detaile zákazníka sa pre Usera počítajú iba z jeho termínov.

### 4.5 Používatelia a služby
- Mazanie nahrádza deaktivácia (`is_active = false`); deaktivácia zároveň zruší všetky sessions používateľa.
- Admin nemôže deaktivovať sám seba a vždy musí ostať aspoň jeden aktívny Admin. To isté platí pre zmenu roly: Admin si nemôže odobrať rolu Admin a posledný aktívny Admin sa nedá degradovať → 409.
- E-mail používateľa je unikátny (porovnanie v lowercase); duplicita → 409. Heslo má aspoň 8 znakov.
- Reset hesla (`POST /api/admin/users/{id}/password`) zruší všetky sessions daného používateľa; ak admin resetuje heslo sám sebe, jeho aktuálna session ostáva.
- Pri deaktivácii pracovníka s budúcimi termínmi API vráti ich počet a UI zobrazí upozornenie (termíny ostávajú, admin ich preplánuje).
- Služba sa nemaže, len deaktivuje (história ostáva konzistentná).

### 4.6 Štatistiky (admin)
- Obdobie: `from`, `to` = dátumy `YYYY-MM-DD` v časovom pásme salónu, oba vrátane; predvolene od 1. dňa aktuálneho mesiaca po dnešok; `from > to` alebo rozsah > 731 dní → 400. `months` = 1–24 (predvolene 12), kalendárne mesiace v čase salónu vrátane aktuálneho (neúplného); súvislý rad aj s nulovými mesiacmi.
- Termín patrí do obdobia podľa `starts_at` (v čase salónu), nie podľa času platby.
- **Tržby** = súčet `payments.amount` dokončených termínov v období. **Návštevy** = počet `Completed`. **Priemerná hodnota návštevy** = tržby / návštevy (`null`, ak 0 návštev).
- **Miera no-show** = `NoShow / (Completed + NoShow)` v období (zrušené a ešte neuzavreté termíny sa nerátajú; `null`, ak menovateľ 0).
- **Nový zákazník** = zákazník, ktorého prvá `Completed` návšteva (celkovo) padla do obdobia.
- **Vyťaženosť pracovníka** = súčet minút jeho termínov ≠ `Cancelled` v období / súčet minút otváracích hodín v období (rovnaké pre všetkých; absencie nie sú v MVP). Zoznam obsahuje aktívnych pracovníkov s ≥ 1 službou a každého, kto má v období termín.
- Peniaze zaokrúhlené na 2 desatinné miesta; miery ako podiel 0–1 (formátuje frontend). Agregácie sa počítajú v SQL.

## 5. Autentifikácia a sessions (main)

- **Login** `POST /api/auth/login` `{ email, password }`: overenie cez `PasswordHasher<User>`, používateľ musí byť aktívny. Chybné údaje, neexistujúci e-mail aj neaktívny účet → rovnaká 401 so všeobecnou správou „Nesprávny e-mail alebo heslo.“ (neprezrádzať, či e-mail existuje; aj pri neexistujúcom e-maile sa vykoná overenie hesla, aby to neprezradil čas odpovede). Rate limit 5 pokusov / min / IP → 429 ProblemDetails.
- Pri úspechu: náhodný token 32 B (base64url) → cookie `salon_sid` (HttpOnly, `SameSite=Lax`, `Path=/`, `Secure` podľa `Cookie:Secure`). Do DB sa ukladá iba SHA-256 hash. `expires_at` = teraz + 12 h; nečinnosť > 2 h = neplatná session.
- **Každý request**: vlastný `SessionAuthenticationHandler` prečíta cookie → hash → aktívna session (nie revoked, nie expirovaná, nečinnosť < 2 h) a aktívny používateľ → `ClaimsPrincipal` (NameIdentifier, Name, Role). `last_seen_at` aktualizovať najviac raz za minútu.
- **Logout** `POST /api/auth/logout` → `revoked_at = teraz`, zmazanie cookie. **Me** `GET /api/auth/me` → aktuálny používateľ alebo 401.
- Neprihlásený request na API → 401 JSON (žiadny redirect na login), zlá rola → 403.
- CSRF: `SameSite=Lax` + mutujúce endpointy prijímajú iba `application/json` + CORS bez wildcardu. Pre tento projekt postačuje.
- Admin vidí aktívne sessions a môže ich zrušiť — demonštrácia server-side sessions.
- Autorizácia: globálna fallback policy vyžaduje prihlásenie (poistka), každý endpoint má aj tak explicitne `[Authorize(...)]` alebo `[AllowAnonymous]`; verejné ostávajú health, OpenAPI/Scalar (iba Development) a SPA fallback.
- Texty v `detail`, ktoré zobrazí UI (chybné prihlásenie, rate limit, pravidlá o adminoch, kolízia termínu), sú po slovensky; technické chyby po anglicky. Enumy v JSON ako stringy.

## 6. API — main backend

| Metóda | Cesta | Prístup | Popis |
|---|---|---|---|
| GET | `/api/health` | verejné | stav služby a DB |
| **Auth** | | | |
| POST | `/api/auth/login` | verejné | prihlásenie, nastaví cookie |
| POST | `/api/auth/logout` | prihlásený | zruší session |
| GET | `/api/auth/me` | prihlásený | `{ id, email, fullName, role }` |
| **Verejné** | | | |
| GET | `/api/public/services` | verejné | aktívne služby |
| GET | `/api/public/services/{id}/employees` | verejné | pracovníci pre službu `[{ id, fullName }]` |
| GET | `/api/public/availability?serviceId=&date=YYYY-MM-DD&employeeId=` | verejné | `[{ startsAt, employees: [{ id, fullName }] }]` |
| POST | `/api/public/bookings` | verejné | rezervácia (§4.2) → `{ appointmentId, startsAt, serviceName, employeeName, price }` |
| **Termíny** | | | |
| GET | `/api/appointments?from=&to=&status=&employeeId=` | User (svoje), Admin | zoznam; `from` a `to` povinné, rozsah max. 92 dní; `employeeId` iba Admin |
| GET | `/api/appointments/{id}` | User (svoje), Admin | detail vrátane zákazníka a platby |
| POST | `/api/appointments` | User (pre seba), Admin | termín za zákazníka, `source = Staff`, zákazník find-or-create podľa e-mailu |
| PATCH | `/api/appointments/{id}` | User (svoje), Admin | preplánovanie, `staffNote` |
| POST | `/api/appointments/{id}/complete` | User (svoje), Admin | `{ paymentMethod, amount? }` |
| POST | `/api/appointments/{id}/cancel` | User (svoje), Admin | |
| POST | `/api/appointments/{id}/no-show` | User (svoje), Admin | |
| **Zákazníci** | | | |
| GET | `/api/customers?search=&page=&pageSize=` | User (svoji), Admin | zoznam s počtom návštev a poslednou návštevou; hľadanie v mene, e-maile a telefóne (bez ohľadu na veľkosť písmen); `pageSize` predvolene 20, max. 100; odpoveď `{ items, page, pageSize, totalCount }` |
| GET | `/api/customers/{id}` | User (svoji), Admin | detail + história termínov a platieb |
| PATCH | `/api/customers/{id}` | User (svoji), Admin | `fullName`, `phone`, `notes` |
| POST | `/api/customers/lookup` | User (svoji), Admin | `{ ids: [] }` (max. 500) → `[{ id, fullName, email }]` — mená pre ML zoznamy; cudzie a neexistujúce id sa vynechajú |
| GET | `/api/customers/{id}/retention-actions` | Admin | |
| POST | `/api/customers/{id}/retention-actions` | Admin | `{ channel, note, churnProbability? }` |
| **Admin — používatelia a sessions** | | | |
| GET | `/api/admin/users` | Admin | vrátane priradených služieb |
| POST | `/api/admin/users` | Admin | `{ email, fullName, role, password, serviceIds }` |
| PUT | `/api/admin/users/{id}` | Admin | `{ fullName, role, serviceIds }` |
| POST | `/api/admin/users/{id}/deactivate` | Admin | → `{ futureAppointments }` |
| POST | `/api/admin/users/{id}/activate` | Admin | |
| POST | `/api/admin/users/{id}/password` | Admin | `{ password }` |
| GET | `/api/admin/sessions` | Admin | aktívne sessions: používateľ, vytvorená, posledná aktivita, IP, user agent |
| DELETE | `/api/admin/sessions/{id}` | Admin | zrušenie session |
| **Admin — služby** | | | |
| GET | `/api/admin/services` | Admin | aj neaktívne |
| POST | `/api/admin/services` | Admin | |
| PUT | `/api/admin/services/{id}` | Admin | |
| POST | `/api/admin/services/{id}/deactivate` | Admin | |
| POST | `/api/admin/services/{id}/activate` | Admin | vrátenie omylom deaktivovanej služby |
| **Admin — štatistiky** | | | |
| GET | `/api/admin/stats/overview?from=&to=` | Admin | `{ from, to, revenue, completedVisits, noShowRate, newCustomers, averageVisitValue }` (§4.6) |
| GET | `/api/admin/stats/monthly?months=12` | Admin | `[{ month: "YYYY-MM", revenue, completedVisits, newCustomers }]` od najstaršieho |
| GET | `/api/admin/stats/services?from=&to=` | Admin | `[{ serviceId, name, category, completedVisits, revenue }]` podľa tržieb zostupne (aj neaktívne služby s návštevami) |
| GET | `/api/admin/stats/employees?from=&to=` | Admin | `[{ employeeId, fullName, completedVisits, revenue, bookedMinutes, openingMinutes, utilization }]` (utilization 0–1, §4.6) |

## 7. Interné API (main → používa ml)

- Ochrana: hlavička `X-Internal-Api-Key` (env `INTERNAL_API_KEY`), porovnanie v konštantnom čase (`CryptographicOperations.FixedTimeEquals`). Chýbajúci alebo zlý kľúč → 401. Bez cookie auth, nie sú v UI.

### `POST /api/internal/sessions/introspect`
Request: `{ "sessionToken": "<hodnota cookie salon_sid>" }`
Response 200: `{ "active": true, "userId": "…", "role": "Admin", "fullName": "…" }` alebo `{ "active": false }`.
Rovnaké pravidlá platnosti ako `SessionAuthenticationHandler` (aj aktualizácia `last_seen_at`).

### `GET /api/internal/ml/export`

```json
{
  "generatedAt": "2026-10-12T16:00:00Z",
  "customers": [
    { "customerId": "6f1c…", "createdAt": "2025-01-14T09:12:00Z" }
  ],
  "appointments": [
    {
      "appointmentId": "a2b9…",
      "customerId": "6f1c…",
      "employeeId": "c41d…",
      "serviceId": "9e07…",
      "serviceCategory": "Nails",
      "startsAt": "2026-09-30T08:00:00Z",
      "createdAt": "2026-09-18T17:40:00Z",
      "cancelledAt": null,
      "status": "Completed",
      "price": 30.00
    }
  ]
}
```

- Exportujú sa všetky termíny vo všetkých stavoch.
- **Žiadne** mená, e-maily, telefóny ani poznámky — ML pracuje iba s pseudonymnými ID (minimalizácia dát).

## 8. ML služba

### 8.1 Cieľ
Pre každého zákazníka s ≥ 2 dokončenými návštevami odhadnúť pravdepodobnosť, že v nasledujúcich **90 dňoch nepríde** (nebude mať žiadny `Completed` termín). Výstup: pravdepodobnosť, úroveň rizika, čitateľné zdôvodnenie.

### 8.2 Databáza `salon_ml`

| Tabuľka | Stĺpce |
|---|---|
| sync_runs | id, started_at, finished_at, status, customers_count, appointments_count, error |
| raw_customers | customer_id PK, created_at |
| raw_appointments | appointment_id PK, customer_id, employee_id, service_id, service_category, starts_at, created_at, cancelled_at, status, price |
| model_versions | id, created_at, algorithm, params jsonb, metrics jsonb, baseline_metrics jsonb, candidates jsonb, feature_importances jsonb, n_train, n_valid, positive_rate, artifact_path, is_active |
| prediction_runs | id, created_at, model_version_id, reference_date, customers_scored |
| predictions | id, run_id FK, customer_id, churn_probability, risk_level, features jsonb, explanation, created_at — index `(customer_id, created_at desc)` |

Sync nahrádza obsah `raw_*` v jednej transakcii. Predikcie sa nemažú — tvoria históriu.

### 8.3 Pipeline (`POST /ml/pipeline/run`)
1. **Sync** — stiahnuť export z main (§7), uložiť do `raw_*`.
2. **Snapshoty** — referenčný dátum R = teraz (UTC), horizont H = 90 dní. Snapshoty `T_k = R − H − k·30 dní` pre k = 0…7 (každý má celý horizont v minulosti, label je teda známy).
3. **Riadky datasetu** — pre každý `T_k`: zákazníci s ≥ 2 `Completed` návštevami so `starts_at ≤ T_k`; príznaky (§8.4) iba z dát známych k `T_k`; label `y = 1`, ak zákazník nemá žiadnu `Completed` návštevu so `starts_at ∈ (T_k, T_k + H]`.
4. **Validácia v čase s medzerou** — validácia na k = 0 (najnovší snapshot), tréning na k = 3…7, t. j. iba snapshoty, ktorých label okno skončilo najneskôr vo validačnom dátume (`T_k + H ≤ T_0` ⇔ k ≥ 3). Snapshoty k = 1, 2 by sa labelom prekrývali s validačným obdobím a metriky by boli optimistické; vo validácii sa nepoužijú (do finálneho pretrénovania áno). Žiadny náhodný split.
5. **Kandidáti** — `LogisticRegression` (so `StandardScaler`, `class_weight="balanced"`), `RandomForestClassifier` (`class_weight="balanced"`), `HistGradientBoostingClassifier`. Fixný `random_state`.
6. **Baseline** — pravidlo `recency_ratio > 2.0 → churn`, vyhodnotené na tej istej validačnej množine. ROC-AUC baseline sa počíta zo spojitého skóre `recency_ratio` (férové porovnanie s pravdepodobnosťami modelu); precision, recall, F1 a confusion matrix z pravidla s prahom 2,0.
7. **Metriky** — ROC-AUC (hlavná), precision, recall a F1 pri prahu 0,5, confusion matrix, podiel pozitívnych. Uložiť pre všetkých kandidátov aj baseline.
8. **Výber** podľa ROC-AUC → dôležitosť príznakov cez `permutation_importance` na validačnej množine → pretrénovať víťaza na všetkých snapshotoch (k = 0…7) → uložiť `joblib` do `MODEL_DIR` → záznam v `model_versions` (`is_active`).
9. **Predikcia** — príznaky k `T = R` pre všetkých oprávnených zákazníkov → `predictions` + `prediction_runs`.
10. **Odpoveď** — zhrnutie: počty, metriky víťaza vs. baseline, trvanie.

Beh je synchrónny (malé dáta, rádovo sekundy). Ak už jeden beh prebieha → 409.

### 8.4 Príznaky (vždy k dátumu T)

| Príznak | Definícia |
|---|---|
| recency_days | dni od poslednej `Completed` návštevy |
| visit_count | počet `Completed` návštev |
| tenure_days | dni od prvej `Completed` návštevy |
| avg_interval_days | priemer rozostupov medzi `Completed` návštevami |
| std_interval_days | smerodajná odchýlka rozostupov (0 pri 2 návštevách) |
| recency_ratio | `recency_days / avg_interval_days` |
| visits_last_90d, visits_last_180d | `Completed` návštevy v danom okne pred T |
| total_spent, avg_price | z `Completed` návštev |
| distinct_services | počet rôznych služieb |
| cancel_count, no_show_count | termíny so `starts_at ≤ T` v stave `Cancelled` / `NoShow` |
| cancel_rate | `(cancel_count + no_show_count) / všetky termíny so starts_at ≤ T` |
| has_future_booking | 1, ak existuje termín s `created_at ≤ T < starts_at`, ktorý k T nebol zrušený (`cancelled_at` je null alebo `> T`) |
| main_category_* | one-hot najčastejšej kategórie služby |

**Pravidlo proti leakage:** `build_features(appointments, customers, cutoff)` najprv odfiltruje všetko, čo k `cutoff` nebolo známe, a až potom počíta. Povinný test: pridanie ľubovoľných termínov s `created_at > cutoff` nesmie zmeniť výsledok.

Stav termínu známy k T (`known_at`):
- `created_at > T` → termín k T neexistoval, ignoruje sa.
- `Cancelled` s `cancelled_at > T` → k T **nezrušený** (ak `starts_at > T`, je to budúci termín).
- `Completed` a `NoShow` sa počítajú iba pri `starts_at ≤ T`. Termín so `starts_at ≤ T`, ktorý k T nebol ani zrušený, ani `Completed`/`NoShow`, sa započíta iba do menovateľa `cancel_rate`.

Spresnenia výpočtu:
- Zákazníci s < 2 `Completed` návštevami k T sa do riadkov nezaradia.
- `avg_interval_days` je zdola orezaný na 1 deň (dve návštevy v ten istý deň by inak delili nulou v `recency_ratio`); `std_interval_days` s `ddof=0`.
- Časy v dňoch ako desatinné číslo.
- `distinct_services` = počet rôznych `service_id` medzi `Completed` návštevami.
- `main_category_*` = najčastejšia kategória `Completed` návštev (pri zhode abecedne prvá); stĺpce pre všetkých 6 kategórií vždy.

### 8.5 Úrovne rizika a zdôvodnenie
- `low` < 0,33 ≤ `medium` < 0,66 ≤ `high` (prahy v konfigurácii: `RISK_MEDIUM_THRESHOLD`, `RISK_HIGH_THRESHOLD`).
- Zákazník s < 2 návštevami sa neskóruje; API preňho vracia `riskLevel: "insufficient_data"`.
- `explanation` (po slovensky) z príznakov, napr.: „Posledná návšteva pred 64 dňami, obvykle chodí každých 21 dní (3,0× dlhšie ako zvyčajne). Nemá naplánovaný ďalší termín.“

### 8.6 Autentifikácia v ML
- Dependency `current_user`: prečíta cookie `salon_sid` → `POST {MAIN_API_URL}/api/internal/sessions/introspect` (httpx, timeout 3 s). `active: false` alebo chýbajúca cookie → 401; main nedostupný → 503.
- Výsledok introspekcie sa cachuje v pamäti 30 s (kľúč = hash tokenu).
- `require_role("Admin")` pre admin endpointy.
- CORS: `CORS_ORIGINS` (čiarkou oddelené), `allow_credentials=True`.

### 8.7 API — ML backend

| Metóda | Cesta | Prístup | Popis |
|---|---|---|---|
| GET | `/ml/health` | verejné | stav + či existuje aktívny model |
| POST | `/ml/pipeline/run` | Admin | sync + tréning + predikcia (§8.3) |
| GET | `/ml/predictions?risk=&limit=&offset=` | Admin | posledná predikcia pre každého zákazníka, zoradené podľa pravdepodobnosti |
| GET | `/ml/predictions/{customerId}` | User, Admin | posledná predikcia + zdôvodnenie |
| POST | `/ml/predictions/lookup` | User, Admin | `{ customerIds: [] }` → posledné predikcie (odznaky v kalendári) |
| GET | `/ml/predictions/{customerId}/history` | User, Admin | vývoj pravdepodobnosti v čase |
| GET | `/ml/model` | Admin | aktívny model: metriky, baseline, kandidáti, dôležitosť príznakov |
| GET | `/ml/runs` | Admin | história behov (sync, tréning, predikcia) |
| GET | `/ml/stats/risk-distribution` | Admin | počty zákazníkov podľa úrovne rizika |

#### Tvary odpovedí a stavové kódy

JSON je camelCase; výnimka: kľúče objektu `features` sú názvy príznakov z §8.4 (snake_case). Chyby sú ProblemDetails (`application/problem+json`). Každý chránený endpoint: 401 bez platnej session, 403 pri zlej role, 503 ak je main nedostupný pri overovaní session. Interaktívna dokumentácia (OpenAPI): `/docs`.

- **`POST /ml/pipeline/run`** → 200 `{ syncRunId, modelVersionId, predictionRunId, referenceDate, customersCount, appointmentsCount, customersScored, algorithm, metrics, baselineMetrics, candidates: [{ algorithm, metrics }], beatsBaseline, warnings, durationSeconds }`. `metrics` aj `baselineMetrics` majú tvar `{ rocAuc (môže byť null), precision, recall, f1, confusionMatrix, positiveRate, n }`. `beatsBaseline` = ROC-AUC víťaza > ROC-AUC baseline (false, ak je jedna z hodnôt null). Chyby: **409** beh už prebieha (PostgreSQL advisory lock), **422** nedostatok histórie na tréning (sync zostane uložený, model sa nevytvorí), **502** export z main má neplatný formát (sync sa označí `failed`), **503** main je nedostupný (sync `failed`).
- **`GET /ml/health`** → `{ status, database, activeModel }`; nikdy nevyhodí chybu (pri nedostupnej DB `activeModel: false`).
- **`GET /ml/predictions?risk=&limit=&offset=`** → `{ items: [{ customerId, churnProbability, riskLevel, explanation, createdAt, predictionRunId }], total }`. Posledná predikcia každého zákazníka, zoradené podľa `churnProbability` zostupne, potom `customerId`; `total` rešpektuje filter `risk` (`low|medium|high`, inak 422). `limit` 1–500 (predvolene 50), `offset` ≥ 0.
- **`GET /ml/predictions/{customerId}`** → položka ako vyššie + `features` (objekt príznak → číslo). Ak zákazník nemá predikciu (alebo neexistuje), odpoveď je **200** s `riskLevel: "insufficient_data"` a `churnProbability`, `explanation`, `features`, `createdAt`, `predictionRunId` = `null` (ML tak neprezrádza existenciu zákazníka). Neplatné UUID → 422.
- **`POST /ml/predictions/lookup`** `{ customerIds: [uuid] }` (najviac 500, inak 422; duplicity sa zlúčia) → `{ items: [...] }`, jedna položka na každé unikátne ID v poradí požiadavky, tvar ako v zozname; ID bez predikcie majú `riskLevel: "insufficient_data"` a `null` hodnoty. Vybavuje sa jedným dotazom do DB.
- **`GET /ml/predictions/{customerId}/history`** → `{ customerId, items: [{ createdAt, churnProbability, riskLevel, predictionRunId }] }` vzostupne podľa `createdAt`; bez predikcií prázdny zoznam.
- **`GET /ml/model`** → `{ id, createdAt, algorithm, params, metrics, baselineMetrics, candidates, featureImportances: [{ feature, importance }] (zostupne), nTrain, nValid, positiveRate }`; ak ešte neexistuje model → **404**.
- **`GET /ml/runs?limit=`** (predvolene 20, najviac 100) → `{ syncRuns: [{ id, startedAt, finishedAt, status, customersCount, appointmentsCount, error }], modelVersions: [{ id, createdAt, algorithm, rocAuc, baselineRocAuc, isActive }], predictionRuns: [{ id, createdAt, modelVersionId, referenceDate, customersScored }] }`, každý zoznam od najnovšieho.
- **Neočakávané chyby** (vrátane `MODEL_DIR`, do ktorého sa nedá zapísať, pri pipeline) → **500** ProblemDetails bez interných detailov; aktívny model ostáva nezmenený.
- **`GET /ml/stats/risk-distribution`** → `{ low, medium, high, insufficientData, total, lastPredictionAt }`. Počty z poslednej predikcie každého zákazníka; `insufficientData` = zákazníci v `raw_customers` bez akejkoľvek predikcie; `total = low + medium + high + insufficientData`; `lastPredictionAt` je `null`, ak predikcie neexistujú.

## 9. Frontend

- Dva API klienti v `src/api/`: `mainApi` (base `/api`, same-origin) a `mlApi` (base `VITE_ML_API_URL`, napr. `http://localhost:8000`), oba s `credentials: 'include'`. 401 z ktoréhokoľvek → odhlásiť v UI a presmerovať na `/login`.
- Stav prihlásenia: `GET /api/auth/me` pri štarte; `ProtectedRoute` s rolami.
- Formátovanie: dátumy `sk-SK` v `Europe/Bratislava`, mena EUR.
- Po prihlásení: User → `/app/kalendar`, Admin → `/app/prehlad`.

| Cesta | Rola | Obsah | Backend |
|---|---|---|---|
| `/` | verejné | predstavenie salónu, služby s cenami, tlačidlo Rezervovať | main |
| `/rezervacia` | verejné | sprievodca: služba → pracovník alebo „ktokoľvek“ → dátum a voľný čas → kontaktné údaje + súhlas GDPR → potvrdenie | main |
| `/login` | verejné | prihlásenie | main |
| `/app/kalendar` | User, Admin | týždenný kalendár (FullCalendar timeGrid, locale `sk`); Admin s filtrom pracovníka; farebný odznak rizika zákazníka | main + ml |
| `/app/terminy` | User, Admin | zoznam termínov s filtrami (User len svoje) | main |
| `/app/terminy/novy` | User, Admin | telefonická rezervácia za zákazníka | main |
| `/app/terminy/:id` | User, Admin | detail: dokončiť s platbou, zrušiť, neprišiel, preplánovať, poznámka | main |
| `/app/zakaznici` | User, Admin | zoznam s vyhľadávaním a odznakom rizika | main + ml |
| `/app/zakaznici/:id` | User, Admin | kontakt, poznámky, história termínov a platieb; riziko, zdôvodnenie a graf vývoja; retenčné akcie (Admin) | main + ml |
| `/app/prehlad` | Admin | KPI karty, tržby a návštevy po mesiacoch, top služby, vyťaženosť pracovníkov | main |
| `/app/pouzivatelia` | Admin | správa používateľov, priradenie služieb, aktivácia/deaktivácia, reset hesla, aktívne sessions | main |
| `/app/sluzby` | Admin | služby a cenník | main |
| `/app/ml` | Admin | rozdelenie rizika, rizikoví zákazníci (mená cez `POST /api/customers/lookup`), model vs. baseline, dôležitosť príznakov, história behov, „Prepočítať predikcie“, „Zaznamenať kontakt“ (retenčná akcia do main), prázdny stav bez modelu | ml + main |

UI: sidebar podľa roly, meno používateľa a Odhlásiť; loading, error a empty stavy; toasty; potvrdenie deštruktívnych akcií; použiteľné aj na tablete.

## 10. Demo dáta (seeder v main)

Seed má dve časti:

- **Základný seed** (účty, služby, `user_services`) beží **vždy**, keď tabuľka `users` je prázdna — inak by sa nebolo ako prihlásiť. Vyžaduje `Seed__AdminPassword` a `Seed__UserPassword`; ak chýbajú, seed sa preskočí s varovaním v logu.
- **História** (zákazníci, termíny, platby) beží iba pri `Seed__DemoData=true` a iba nad DB bez zákazníkov. Fixný seed náhody (42) → rovnaké dáta pri každej inštalácii. Časy relatívne k „teraz“ → dáta sú vždy čerstvé.

Základný seed:

- Účty: `admin@salon.local` (Admin „Administrátor salónu“, heslo `Seed__AdminPassword`) a 4 pracovníčky (User, heslo `Seed__UserPassword`):

| Pracovníčka | E-mail | Služby (kategórie) |
|---|---|---|
| Jana Kováčová | `jana.kovacova@salon.local` | Skin, Makeup |
| Lucia Horváthová | `lucia.horvathova@salon.local` | Nails |
| Petra Novotná | `petra.novotna@salon.local` | BrowsLashes, Waxing |
| Mária Vargová | `maria.vargova@salon.local` | Massage, Skin |

- Služby:

| Kategória | Služba | Trvanie | Cena |
|---|---|---|---|
| Skin | Hĺbkové čistenie pleti | 60 min | 45 € |
| Skin | Hydratačné ošetrenie pleti | 60 min | 50 € |
| Skin | Anti-age ošetrenie | 75 min | 60 € |
| Makeup | Denné líčenie | 45 min | 30 € |
| Makeup | Svadobné líčenie | 90 min | 70 € |
| Nails | Gélové nechty – nová modelácia | 120 min | 40 € |
| Nails | Doplnenie gélových nechtov | 90 min | 30 € |
| Nails | Manikúra s gél lakom | 60 min | 25 € |
| BrowsLashes | Laminácia obočia | 45 min | 30 € |
| BrowsLashes | Lash lifting | 60 min | 35 € |
| Waxing | Depilácia nôh | 45 min | 30 € |
| Waxing | Depilácia podpazušia | 15 min | 10 € |
| Massage | Relaxačná masáž | 60 min | 40 € |
| Massage | Masáž chrbta a šije | 30 min | 25 € |

História:
- ~300 zákazníkov (Bogus, locale `sk`), história 24 mesiacov dozadu + naplánované termíny na 3 týždne dopredu.
- Archetypy zákazníkov:

| Archetyp | Podiel | Správanie |
|---|---|---|
| Verní | 35 % | pravidelný interval podľa typu služby (nechty ~3 týž., pleť ~5 týž., masáže ~6 týž.) s rozptylom ±15 %, chodia dodnes, väčšina má budúci termín |
| Nepravidelní | 25 % | náhodné rozostupy so stredom 60–90 dní |
| Odchádzajúci | 20 % | najprv pravidelní, potom sa rozostupy predlžujú, pribúdajú zrušenia; v náhodnom bode 3–15 mesiacov dozadu prestanú chodiť |
| Jednorazoví/noví | 20 % | 1–2 návštevy, časť z nich nedávno |

- Každý zákazník má obľúbenú službu (~70 % návštev) a obľúbenú pracovníčku (~80 %).
- Minulé termíny: ~90 % `Completed`, ~6 % `Cancelled`, ~4 % `NoShow` (u odchádzajúcich viac zrušení). Každý `Completed` má platbu (karta ~60 %, hotovosť ~40 %).
- `created_at` = `starts_at` − 1 až 21 dní; `cancelled_at` medzi `created_at` a `starts_at`.
- Termíny len v otváracích hodinách a bez prekryvu u pracovníčky (pri kolízii posun o 15 min, inak iný deň ±2, inak vynechať).
- Šum je zámerný: žiadny archetyp nesmie byť dokonale predvídateľný.

## 11. Nasadenie

| Kontajner | Zdroj | Port | Poznámka |
|---|---|---|---|
| db-main | `postgres:17` | 5432 | volume `db-main-data`, healthcheck `pg_isready` |
| db-ml | `postgres:17` | 5433 → 5432 | volume `db-ml-data`, healthcheck |
| app | `backend-main/Dockerfile` (context = koreň) | 8080 | `depends_on: db-main (healthy)`; migrácie + seed pri štarte |
| ml | `backend-ml/Dockerfile` | 8000 | `depends_on: db-ml (healthy), app`; `alembic upgrade head` a uvicorn; volume `ml-models` pre `MODEL_DIR` |

- Dockerfile `app` (multi-stage): node (`npm ci`, `npm run build` s `VITE_ML_API_URL` ako build arg) → dotnet sdk (restore, publish) → aspnet runtime; `dist/` frontendu skopírovaný do `wwwroot/`. `UseStaticFiles` + `MapFallbackToFile("index.html")` pre cesty mimo `/api`.

| Env premenná | Kto ju používa | Príklad / poznámka |
|---|---|---|
| `POSTGRES_MAIN_PASSWORD` | db-main, app | |
| `POSTGRES_ML_PASSWORD` | db-ml, ml | |
| `INTERNAL_API_KEY` | app, ml | dlhý náhodný reťazec |
| `SEED_DEMO_DATA` | app | `true` |
| `SEED_ADMIN_PASSWORD`, `SEED_USER_PASSWORD` | app | len demo heslá |
| `COOKIE_SECURE` | app | `false` pre `http://localhost` |
| `VITE_ML_API_URL` | build `app` | `http://localhost:8000` |
| `CORS_ORIGINS` | ml | `http://localhost:8080,http://localhost:5173` |
| `MAIN_API_URL` | ml | `http://app:8080` v Dockeri, `http://localhost:5080` v dev |
| `SESSION_COOKIE_NAME` | ml | `salon_sid` |
| `MODEL_DIR` | ml | `/app/models` |
| `RISK_MEDIUM_THRESHOLD`, `RISK_HIGH_THRESHOLD` | ml | `0.33`, `0.66` (§8.5) |
| `DATABASE_URL` | ml | skladá sa v compose z `POSTGRES_ML_PASSWORD` (`postgresql+psycopg://salon:…@db-ml:5432/salon_ml`); v dev predvolene `localhost:5433` |

V compose sa premenné mapujú na .NET konfiguráciu (`ConnectionStrings__Main`, `Internal__ApiKey`, `Seed__DemoData`, `Cookie__Secure` …).

**Akceptácia:** čistý klon → `cp .env.example .env` → `docker compose up --build` → http://localhost:8080 → prihlásenie adminom → ML dashboard → „Prepočítať predikcie“ funguje.

### 11.1 Produkčné nasadenie — Azure

- Cieľ: **jedna Linux VM v Azure** (predplatné Azure for Students), na nej ten istý `docker-compose.yml` ako lokálne. Žiadne spravované Azure služby (DB ostávajú kontajnery).
- VM má DNS label (`<nazov>.<region>.cloudapp.azure.com`); frontend aj ML sú na **rovnakom hostname** (rôzne porty), takže cookie `salon_sid` so `SameSite=Lax` funguje rovnako ako na localhoste.
- Na VM sa mení iba `.env`: `VITE_ML_API_URL` a `CORS_ORIGINS` ukazujú na hostname VM, nové heslá a `INTERNAL_API_KEY`. Porty DB (5432/5433) sa von **nepublikujú** (NSG povolí iba 22, 8080, 8000).
- HTTPS zatiaľ nie; vyžaduje reverse proxy ako ďalší kontajner (vyučujúci potvrdil, že viac ako 4 kontajnery je OK) — rozhodne sa vo Fáze 9.
- CI (GitHub Actions) iba builduje a testuje; nasadenie je zatiaľ ručné (ssh → `git pull` → `docker compose up -d --build`).

## 12. Známe obmedzenia (vedomé zjednodušenia)

- E-mail zákazníka sa neoveruje — ktokoľvek môže rezervovať pod cudzím e-mailom.
- Telefonická rezervácia hľadá zákazníka podľa e-mailu (§4.2), takže pracovník si ňou môže „pripojiť“ existujúceho zákazníka kolegyne a od tej chvíle vidí jeho kontakt a poznámky (nie však cudzie termíny ani útratu). Je to zámer: kto zákazníka obsluhuje, potrebuje poznať jeho poznámky (typ pleti, alergie). Pracovníci sú dôveryhodní zamestnanci; termín, ktorý vytvorí User, je vždy priradený jemu (`employee_id`, `source = Staff`), takže je dohľadateľné, kto si zákazníka pripojil.
- Dáta sú syntetické; metriky modelu dokazujú funkčnosť pipeline, nie reálnu presnosť.
- ML nepozná vzťah zákazník–pracovník, takže pri `/ml/predictions/{customerId}` neoveruje, či ho User obsluhuje. Riziko odchodu považujeme za nízko citlivý údaj.
- URL ML služby sa zapéka do buildu frontendu — pri zmene hostname (localhost ↔ Azure VM) treba image `app` zbuildovať znova.
- Produkcia na Azure VM beží zatiaľ cez HTTP bez TLS (§11.1).
