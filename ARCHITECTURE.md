# Architektúra – Barber Shop CRM

## 1. Účel a rozsah

Tento dokument opisuje **navrhovanú architektúru podľa zadania**, nie aktuálny stav implementácie. Systém poskytuje rezervácie, správu zákazníkov a barberov, evidenciu návštev, platieb a hodnotení a predikciu odchodu zákazníkov (Customer Churn Prediction).

> Roly zodpovedajú implementácii v `backend-main/src/SalonCrm.Api/Domain/UserRole.cs`: `Admin` a `User`. Zákazník nemá účet ani používateľskú rolu; rezervuje cez verejný formulár a identifikuje sa e-mailom. Rozdielom oproti existujúcemu návrhu projektu zostáva komunikácia s ML: tento dokument navrhuje volanie cez hlavný backend, kým projekt počíta aj s priamym volaním ML API z frontendu. Dokument nemení implementáciu ani konfiguráciu.

## 2. Kontext systému

```mermaid
flowchart LR
    customer["Zákazník – bez účtu"]
    barber["User – pracovník / barber"]
    admin["Admin – administrátor"]
    crm["Barber Shop CRM"]

    customer -->|"Verejná rezervácia, identifikácia e-mailom"| crm
    barber -->|"Kalendár, správa termínov, história zákazníkov"| crm
    admin -->|"Zamestnanci, zákazníci, služby, štatistiky, ML dashboard"| crm
```

## 3. Aplikačná architektúra

```mermaid
flowchart TB
    users["Zákazník bez účtu / User / Admin"]

    subgraph browser["Prehliadač"]
        frontend["Frontend SPA<br/>Vite + React + TypeScript"]
    end

    subgraph server["Serverová časť"]
        main["Backend 1 – hlavné REST API<br/>ASP.NET Core (.NET)"]
        ml["Backend 2 – ML REST API<br/>Python + FastAPI"]
        model["ML pipeline<br/>Pandas + scikit-learn"]
        mainDb[("Hlavná SQL databáza<br/>PostgreSQL – salon_main")]
        mlDb[("ML SQL databáza<br/>PostgreSQL – salon_ml")]
        artifact[("Súbor natrénovaného modelu<br/>model + verzia")]
    end

    users --> frontend
    frontend <-->|"HTTPS / REST / JSON"| main
    main <-->|"SQL – CRM dáta"| mainDb
    main <-->|"Interné REST API<br/>vstupné dáta a výsledky predikcií"| ml
    ml <-->|"SQL – analýzy a história predikcií"| mlDb
    ml -->|"Príprava dát a predikcia"| model
    model -->|"Výsledky"| ml
    model <-->|"Uloženie / načítanie"| artifact
```

- **Frontend** zobrazuje rozhranie podľa roly a komunikuje iba s hlavným backendom. Nepristupuje priamo do databáz ani do ML API.
- **Hlavný backend** je zdrojom pravdy pre CRM. Zabezpečuje autentifikáciu, autorizáciu, obchodné pravidlá a komunikáciu s ML službou.
- **ML backend** vykonáva výhradne analýzy zákazníkov, prípravu dát a ML predikcie. Nespravuje rezervácie ani prihlasovanie používateľov.
- **Každý backend vlastní svoju databázu.** Medzi databázami nie sú priame dotazy, zdieľané tabuľky ani cudzie kľúče; dáta sa prenášajú cez REST API.
- Pre hlavný backend je zo zadaných alternatív zvolený **PostgreSQL**, ktorý používa aj existujúci projekt. MS SQL je možná alternatíva, nie ďalšia potrebná databáza.

## 4. Moduly a zodpovednosti

```mermaid
flowchart LR
    subgraph fe["Frontend – React"]
        customerUi["Verejný rezervačný formulár<br/>Zákazník bez prihlásenia"]
        barberUi["Zóna User – pracovník / barber<br/>Vlastný kalendár, termíny a zákazníci"]
        adminUi["Admin a CRM dashboard<br/>Správa, štatistiky, riziko odchodu"]
    end

    subgraph main["Backend 1 – ASP.NET Core"]
        api["REST API<br/>Verejné endpointy + autorizácia Admin / User"]
        profiles["Účty Admin / User<br/>Samostatná evidencia zákazníkov"]
        services["Služby a cenník"]
        bookings["Rezervácie a dostupnosť"]
        records["Návštevy, platby a hodnotenia"]
        dashboard["Štatistiky a CRM dashboard"]
        client["Klient ML API"]
    end

    subgraph ml["Backend 2 – FastAPI"]
        mlApi["Interné REST API"]
        features["Validácia dát a výpočet príznakov"]
        inference["Predikcia a určenie rizika"]
        history["Ukladanie analýz a histórie"]
    end

    customerUi --> api
    barberUi --> api
    adminUi --> api
    api --> profiles
    api --> services
    api --> bookings
    api --> records
    api --> dashboard
    dashboard --> client
    client <-->|"REST / JSON"| mlApi
    mlApi --> features
    features --> inference
    inference --> history
```

| Aktér / rola | Hlavné oprávnenia |
|---|---|
| Zákazník (bez účtu a roly) | Verejný výber služby, pracovníka a termínu a vytvorenie rezervácie. Identifikácia normalizovaným e-mailom; nevzniká prihlasovací účet. |
| `User` (pracovník, napr. barber) | Vidí a spravuje iba vlastné termíny a zákazníkov, ktorých obsluhoval. |
| `Admin` | Vidí všetky dáta; spravuje používateľov, zákazníkov, služby a cenník, štatistiky, ML a retenčné akcie. |

Oprávnenia a vlastníctvo dát kontroluje backend pri každej chránenej požiadavke, nie iba frontend. Používateľské účty a ich roly spravuje admin. Pri verejnej rezervácii sa zákazník vyhľadá alebo vytvorí podľa normalizovaného e-mailu; e-mail sám osebe neoprávňuje na prístup k histórii ani na zmeny existujúcich rezervácií. Zákaznícky portál, vlastná história a hodnotenia zo zadania nie sú oprávneniami existujúcej zákazníckej roly, pretože taká rola v kóde neexistuje.

## 5. Tok vytvorenia rezervácie

```mermaid
sequenceDiagram
    autonumber
    actor Customer as Zákazník
    participant FE as React frontend
    participant API as ASP.NET Core API
    participant DB as Hlavná PostgreSQL

    Customer->>FE: Vyberie barbera, službu a termín
    FE->>API: Vyžiada dostupné termíny
    API->>DB: Načíta dostupnosť a rezervácie
    DB-->>API: Obsadenosť termínov
    API-->>FE: Dostupné termíny
    Customer->>FE: Potvrdí rezerváciu
    FE->>API: Odošle verejnú rezerváciu a kontaktné údaje
    API->>API: Validuje vstup a vyhľadá alebo vytvorí zákazníka podľa e-mailu
    API->>DB: Transakčne overí kolíziu a uloží rezerváciu
    alt Termín je voľný
        DB-->>API: Rezervácia uložená
        API-->>FE: 201 Created a detail rezervácie
        FE-->>Customer: Zobrazí potvrdenie
    else Termín bol medzitým obsadený
        DB-->>API: Konflikt rezervácie
        API-->>FE: 409 Conflict
        FE-->>Customer: Ponúkne výber iného termínu
    end
```

Kontrola dostupnosti pri zobrazení kalendára nestačí: pred súbežnými rezerváciami chráni aj transakcia a databázové obmedzenie. Zmeny a rušenie rezervácií vykonáva oprávnený pracovník (`User` iba pri vlastných termínoch) alebo `Admin`, pri dodržaní povolených prechodov stavu.

## 6. Tok ML predikcie a zobrazenia výsledkov

Nasledujúci diagram ukazuje navrhované vyhodnotenie na požiadanie admina. Tréning modelu prebieha oddelene od obsluhy tejto požiadavky.

```mermaid
sequenceDiagram
    autonumber
    actor Admin
    participant FE as CRM dashboard
    participant API as ASP.NET Core API
    participant DB as Hlavná PostgreSQL
    participant ML as FastAPI
    participant Model as Pandas + scikit-learn
    participant MLDB as ML PostgreSQL

    Admin->>FE: Spustí vyhodnotenie rizika odchodu
    FE->>API: Požiada o analýzu zákazníkov
    API->>API: Overí oprávnenie admina
    API->>DB: Načíta históriu k referenčnému dátumu
    DB-->>API: Návštevy a rezervácie
    API->>ML: Interná požiadavka s pseudonymizovanými dátami
    ML->>ML: Overí službu a validuje vstup
    ML->>Model: Vypočíta príznaky a spustí model
    Model-->>ML: Pravdepodobnosti odchodu
    ML->>ML: Zaradí zákazníkov do rizikových skupín
    ML->>MLDB: Uloží analýzu, výsledky, čas a verziu modelu
    MLDB-->>ML: Potvrdenie uloženia
    ML-->>API: Identifikátory zákazníkov, skóre a rizikové skupiny
    API-->>FE: Výsledky pre CRM dashboard
    FE-->>Admin: Zobrazí nízke, stredné a vysoké riziko
```

Výsledky možno neskôr načítať z histórie v ML databáze cez rovnakú cestu frontend → hlavný backend → ML backend. Pri nedostupnosti ML služby zostávajú bežné CRM funkcie dostupné; dashboard zobrazí chybu alebo posledný dostupný výsledok s dátumom výpočtu.

## 7. ML pipeline

```mermaid
flowchart TB
    input["Pseudonymizovaná história<br/>návštevy a rezervácie k dátumu T"]
    validation["Validácia a čistenie dát<br/>Pandas"]
    features["Výpočet príznakov<br/>dni od poslednej návštevy<br/>frekvencia a intervaly návštev<br/>počet návštev a zrušených rezervácií"]

    subgraph training["Tréning – oddelený proces"]
        labels["Historické príklady s označením odchodu"]
        split["Časové rozdelenie tréningových a testovacích dát"]
        train["Tréning klasifikátora<br/>scikit-learn"]
        evaluation["Vyhodnotenie kvality<br/>precision, recall, ROC-AUC"]
        artifact[("Verziovaný model<br/>vrátane predspracovania")]
    end

    subgraph prediction["Predikcia"]
        inference["Načítanie modelu a výpočet skóre"]
        risk["Pravdepodobnosť odchodu 0 až 1<br/>nízke / stredné / vysoké riziko"]
        history[("ML databáza<br/>história výsledkov")]
    end

    input --> validation --> features
    features --> labels --> split --> train --> evaluation --> artifact
    features --> inference
    artifact --> inference
    inference --> risk --> history
```

- **Definícia odchodu:** pred tréningom treba určiť, po akom období bez návštevy sa zákazník považuje za odídeného a aký je horizont predikcie. Zadanie konkrétnu hranicu neurčuje.
- **Ochrana pred únikom dát:** príznaky k dátumu T smú používať iba informácie známe k T. Označenie odchodu sa pri tréningu určuje z nasledujúceho sledovaného obdobia; príklady musia mať dostatočne dlhé následné pozorovanie.
- **Rizikové skupiny:** hranice medzi nízkym, stredným a vysokým rizikom sú konfigurovateľné a majú vychádzať z vyhodnotenia modelu.
- **Interpretácia:** zákazník s obvyklým intervalom návštev 21 dní, ktorý neprišiel 60 dní, môže dostať zvýšené riziko. Nejde o istotu, že zákazník odišiel.
- **Dáta:** bez vhodných historických a označených dát nemožno deklarovať spoľahlivú kvalitu predikcie; syntetické dáta slúžia iba na demonštráciu.

## 8. Konceptuálny dátový model

Diagram zobrazuje hlavné entity a vzťahy, nie finálnu databázovú schému. Pre jednoduchosť predpokladá jednu službu na rezerváciu a najviac jednu platbu a hodnotenie na návštevu.

```mermaid
erDiagram
    CUSTOMER ||--o{ RESERVATION : "vytvara"
    USER ||--o{ RESERVATION : "obsluhuje ako pracovnik"
    SERVICE ||--o{ RESERVATION : "je objednana"
    RESERVATION ||--o| VISIT : "vedie k navsteve"
    VISIT ||--o| PAYMENT : "ma platbu"
    VISIT ||--o| REVIEW : "ma hodnotenie"

    USER {
        uuid id PK
        string email UK
        string password_hash
        string role
    }
    CUSTOMER {
        uuid id PK
        string email UK
        string name
        string phone
    }
    SERVICE {
        uuid id PK
        string name
        int duration_minutes
        decimal price
    }
    RESERVATION {
        uuid id PK
        uuid customer_id FK
        uuid employee_id FK
        uuid service_id FK
        datetime starts_at
        datetime ends_at
        decimal agreed_price
        string status
    }
    VISIT {
        uuid id PK
        uuid reservation_id FK
        datetime completed_at
    }
    PAYMENT {
        uuid id PK
        uuid visit_id FK
        decimal amount
        string method
        string status
    }
    REVIEW {
        uuid id PK
        uuid visit_id FK
        int rating
        string comment
    }
```

`USER.role` nadobúda iba hodnoty `Admin` alebo `User`. Pracovník/barber je používateľský účet priradený cez `employee_id`, nie samostatná rola ani entita `BARBER`. `CUSTOMER` je samostatný záznam bez väzby na prihlasovací účet. Ostatné entity zostávajú konceptuálnym návrhom podľa zadania, nie presným prepisom aktuálnej schémy.

Cena dohodnutá v rezervácii uchováva historickú hodnotu aj po zmene cenníka. Platby predstavujú evidenciu platieb; integrácia externej platobnej brány nie je súčasťou zadania.

Samostatná ML databáza:

```mermaid
erDiagram
    MODEL_VERSION ||--o{ ANALYSIS_RUN : "pouzita pri"
    ANALYSIS_RUN ||--o{ PREDICTION : "obsahuje"

    MODEL_VERSION {
        uuid id PK
        string version UK
        string artifact_path
        datetime trained_at
        string metrics_json
    }
    ANALYSIS_RUN {
        uuid id PK
        uuid model_version_id FK
        datetime reference_date
        datetime created_at
        string status
    }
    PREDICTION {
        uuid id PK
        uuid analysis_run_id FK
        uuid customer_external_id
        float churn_probability
        string risk_level
        string features_json
    }
```

`customer_external_id` je pseudonymizovaný identifikátor z hlavného backendu, **nie databázový cudzí kľúč** do hlavnej databázy. ML služba nepotrebuje meno, e-mail, telefón ani prihlasovacie údaje zákazníka. Artefakt modelu je uložený v súborovom úložisku; databáza eviduje jeho verziu a metadáta.

## 9. Navrhované nasadenie

Štyri kontajnery zodpovedajú základnému rozdeleniu existujúceho `docker-compose.yml`, ale nižšie uvedený smer volania ML služby vychádza z tohto zadania, nie z aktuálnej konfigurácie.

```mermaid
flowchart TB
    browser["Prehliadač"]

    subgraph host["Server – Docker Compose"]
        subgraph public["Verejný vstup"]
            app["app :8080<br/>ASP.NET Core + zostavený React frontend"]
        end
        subgraph internal["Interná kontajnerová sieť"]
            ml["ml :8000<br/>FastAPI + ML pipeline"]
            mainDb[("db-main :5432<br/>PostgreSQL")]
            mlDb[("db-ml :5432<br/>PostgreSQL")]
        end
        mainVolume[("Trvalý zväzok CRM dát")]
        mlVolume[("Trvalý zväzok ML dát")]
        modelVolume[("Trvalý zväzok modelov")]
    end

    browser <-->|"HTTPS cez TLS termináciu"| app
    app <-->|"Interné HTTP / REST"| ml
    app <--> mainDb
    ml <--> mlDb
    mainDb --- mainVolume
    mlDb --- mlVolume
    ml --- modelVolume
```

- Pri vývoji môže Vite bežať samostatne; pri nasadení hlavný backend poskytuje zostavené statické súbory frontendu.
- V produkcii majú byť databázy a ML API dostupné iba interne. Existujúce mapovanie ich portov na hostiteľa slúži vývoju a tento cieľový stav zatiaľ nezaručuje.
- TLS terminácia je požiadavka produkčného prostredia; konkrétny spôsob jej nasadenia tento návrh neurčuje.
- Trvalé zväzky uchovávajú dáta pri výmene kontajnerov, nenahrádzajú však pravidelné zálohy.

## 10. Bezpečnosť a prevádzkové pravidlá

- Prihlásenie účtov `Admin` a `User` spravuje hlavný backend cez serverové sessions a cookie `salon_sid`. V produkcii má cookie používať `HttpOnly`, `Secure` a zodpovedajúcu ochranu proti CSRF. Zákazník verejnú rezerváciu vytvára bez prihlásenia.
- Heslá sa ukladajú iba ako bezpečné hashe. Citlivé nastavenia a prihlasovacie údaje sa odovzdávajú cez konfiguráciu prostredia, nie cez zdrojový kód.
- Interné ML API overuje identitu hlavného backendu, napríklad servisným API kľúčom. Samotná interná sieť nenahrádza autentifikáciu.
- Prenášajú sa iba údaje potrebné na analýzu. Pseudonymizované záznamy zostávajú citlivými údajmi s obmedzeným prístupom a dobou uchovávania.
- Volania ML API majú časový limit a zrozumiteľné chybové odpovede; nedostupnosť ML nesmie zablokovať rezervácie.
- Každá predikcia obsahuje referenčný dátum, čas výpočtu a verziu modelu, aby bolo možné rozlíšiť aktuálne a historické výsledky.
