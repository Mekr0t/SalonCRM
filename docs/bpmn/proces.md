# BPMN — životný cyklus termínu a retencia zákazníkov

Model: [`salon-crm.bpmn`](salon-crm.bpmn) (BPMN 2.0, jedna kolaborácia). Otvára sa v Camunda Modeleri; XML prechádza validáciou voči oficiálnej schéme OMG BPMN 2.0.

Proces opisuje to, čo systém naozaj robí podľa [SPEC](../SPEC.md). Systém neposiela e-maily (SPEC §1, mimo rozsahu), so zákazníkom preto komunikuje cez webový formulár alebo personál telefonicky.

## Bazény a dráhy

| Bazén | Dráhy | Prečo |
|---|---|---|
| **Zákazník** | – | Externý účastník bez účtu a roly; so salónom komunikuje iba správami (message flows). |
| **Kozmetický salón** | Admin · Pracovník (User) · CRM systém (hlavný backend) | Roly zo SPEC §2 a hlavný backend, ktorý vykonáva kontroly a zápisy do `salon_main`. |
| **ML služba** | – | Samostatná služba s vlastnou databázou `salon_ml`. S hlavným backendom komunikuje iba cez HTTP, preto je to samostatný bazén a nie dráha (CLAUDE.md, tok dát 4). |

## Priebeh

1. **Rezervácia.** Zákazník si vyberie spôsob.
   - **Online:** služba → pracovník alebo „ktokoľvek“ → dátum a čas → kontakt → súhlas GDPR → odoslanie.
   - **Telefonicky:** pracovník zistí požiadavku, nájde voľný čas a zadá údaje. User rezervuje iba pre seba, Admin pre kohokoľvek.
2. **Spracovanie v CRM.** CRM:
   - validuje údaje (400),
   - pri voľbe „ktokoľvek“ pridelí pracovníka s najmenším počtom termínov,
   - overí slot,
   - nájde alebo vytvorí zákazníka podľa e-mailu a uloží termín `Scheduled`.
   
   Kolíziu pri zápise (`23P01`) zachytí hraničná chybová udalosť a vedie na rovnakú 409 ako obsadený slot. Pri online rezervácii zákazník dostane výsledok na obrazovke a pri zamietnutí si vyberie iný čas. Pri telefonickej rezervácii pracovník ponúkne iný čas.
3. **Čakanie na termín.** Udalostná brána rozhodne, čo nastane skôr:
   - nastane čas termínu,
   - zákazník požiada o zmenu (preplánovanie s opakovanou kontrolou slotu),
   - zákazník požiada o zrušenie.
4. **Podproces „Zrušenie termínu“** (CRM):
   - overí rolu a vlastníctvo termínu,
   - overí stav a čas: User iba pred začiatkom, Admin kedykoľvek, len stav `Scheduled`,
   - atomicky prepne stav na `Cancelled`.
   
   Chybové koncové udalosti 404 a 409 zachytí hraničná udalosť a pracovník zákazníkovi vysvetlí, prečo termín nemožno zrušiť.
5. **Návšteva.** Druhá udalostná brána: buď zákazník príde, alebo do 15 minút po začiatku nepríde, a vtedy pracovník termín označí ako `NoShow`. Ak zákazník príde, nasleduje **podproces „Vykonanie ošetrenia“**: konzultácia, poznámky v karte (alergie, typ pleti), kontraindikácie s prípadnou úpravou postupu, paralelne príprava pracoviska a diagnostika pleti, ošetrenie a odporúčanie.
6. **Platba a uzavretie.** Pracovník zapíše poznámku, prípadne upraví sumu (zľava, doplnková služba), prijme platbu a dokončí termín. CRM overí prechod a v jednej transakcii uloží `Completed` aj platbu. Súbeh (napr. zrušenie v tom istom čase) končí 409.
7. **Analýza rizika odchodu.** Časovač spustí proces každý pondelok. Admin spustí prepočet a ML služba:
   - overí session cez `introspect` (401/403),
   - získa zámok behu (409),
   - stiahne pseudonymizovaný export (`GET /api/internal/ml/export`) a uloží ho do `salon_ml`,
   - v **podprocese „Tréning modelu a predikcia rizika“**: snapshoty, príznaky bez úniku budúcich dát, labely, paralelne 3 modely a baseline, výber podľa ROC-AUC, dôležitosť príznakov, pretrénovanie, uloženie modelu, predikcia a zdôvodnenia.
   
   Nedostatok histórie (422) ukončí podproces chybou.
8. **Retencia.** Admin porovná model s baseline a vyberie rizikových zákazníkov. Pre každého z nich (sekvenčný **multi-instance podproces „Retenčný kontakt“**): načíta kartu a zdôvodnenie, kontaktuje zákazníka, pri záujme vytvorí telefonickú rezerváciu a zaznamená retenčnú akciu.

## Splnenie zadania

| Požiadavka | Model |
|---|---|
| ≥ 60 činností | **94** činností; **65** mimo podprocesov, takže požiadavka je splnená aj vtedy, ak sa činnosti v podprocesoch nerátajú |
| 2 bazény | 3 bazény (Zákazník, Kozmetický salón, ML služba) |
| bazén s ≥ 3 dráhami | Kozmetický salón: Admin, Pracovník, CRM systém |
| ≥ 1 podproces | 4: Zrušenie termínu, Vykonanie ošetrenia, Tréning modelu a predikcia rizika, Retenčný kontakt (multi-instance) |
| viac brán | 35 exkluzívnych, 4 paralelné, 2 udalostné |
| ďalšie prvky | správové, časové a chybové udalosti (koncové aj hraničné), 21 tokov správ, dátové úložiská `salon_main` a `salon_ml`, anotácie |

### Počty činností po dráhach

| Bazén / dráha | Hlavný tok | V podprocesoch | Spolu |
|---|---:|---:|---:|
| Zákazník | 18 | 0 | 18 |
| Admin | 6 | 4 | 10 |
| Pracovník | 16 | 8 | 24 |
| CRM systém | 17 | 4 | 21 |
| ML služba | 8 | 13 | 21 |
| **Spolu** | **65** | **29** | **94** |

## Zoznam činností

| # | Činnosť | Dráha / bazén | Podproces | Typ |
|---:|---|---|---|---|
| 1 | Výber služby | Zákazník | – | User |
| 2 | Výber pracovníka alebo „ktokoľvek“ | Zákazník | – | User |
| 3 | Výber dátumu a voľného času | Zákazník | – | User |
| 4 | Vyplnenie kontaktných údajov | Zákazník | – | User |
| 5 | Udelenie súhlasu so spracovaním osobných údajov | Zákazník | – | User |
| 6 | Odoslanie online rezervácie | Zákazník | – | Send |
| 7 | Prijatie výsledku rezervácie | Zákazník | – | Receive |
| 8 | Zavolanie do salónu | Zákazník | – | Send |
| 9 | Prijatie potvrdenia termínu | Zákazník | – | Receive |
| 10 | Žiadosť o zmenu termínu | Zákazník | – | Send |
| 11 | Prijatie nového termínu | Zákazník | – | Receive |
| 12 | Žiadosť o zrušenie termínu | Zákazník | – | Send |
| 13 | Prijatie odpovede salónu | Zákazník | – | Receive |
| 14 | Príchod do salónu | Zákazník | – | Send |
| 15 | Absolvovanie ošetrenia | Zákazník | – | Manual |
| 16 | Zaplatenie hotovosťou alebo kartou | Zákazník | – | Send |
| 17 | Rozhovor o ďalšej návšteve | Zákazník | – | Manual |
| 18 | Dohodnutie nového termínu | Zákazník | – | Send |
| 19 | Otvorenie ML dashboardu | Admin | – | User |
| 20 | Spustenie prepočtu predikcií | Admin | – | Send |
| 21 | Prijatie výsledku prepočtu | Admin | – | Receive |
| 22 | Zobrazenie chyby prepočtu | Admin | – | User |
| 23 | Porovnanie modelu s baseline | Admin | – | User |
| 24 | Výber zákazníkov s vysokým rizikom | Admin | – | User |
| 25 | Načítanie karty zákazníka a zdôvodnenia rizika | Admin | Retenčný kontakt so zákazníkom | User |
| 26 | Kontaktovanie zákazníka | Admin | Retenčný kontakt so zákazníkom | Send |
| 27 | Vytvorenie telefonickej rezervácie | Admin | Retenčný kontakt so zákazníkom | User |
| 28 | Zaznamenanie retenčnej akcie | Admin | Retenčný kontakt so zákazníkom | User |
| 29 | Zistenie želanej služby a pracovníka | Pracovník | – | User |
| 30 | Vyhľadanie voľného času v kalendári | Pracovník | – | User |
| 31 | Zadanie údajov zákazníka a potvrdenie súhlasu | Pracovník | – | User |
| 32 | Oznámenie termínu zákazníkovi | Pracovník | – | Send |
| 33 | Výber nového času v kalendári | Pracovník | – | User |
| 34 | Oznámenie nového termínu zákazníkovi | Pracovník | – | Send |
| 35 | Vyhľadanie termínu a voľba Zrušiť | Pracovník | – | User |
| 36 | Potvrdenie zrušenia zákazníkovi | Pracovník | – | Send |
| 37 | Vysvetlenie, prečo termín nemožno zrušiť | Pracovník | – | Send |
| 38 | Označenie termínu ako Neprišiel | Pracovník | – | User |
| 39 | Otvorenie detailu termínu a karty zákazníka | Pracovník | – | User |
| 40 | Konzultácia želaní zákazníka | Pracovník | Vykonanie ošetrenia | Manual |
| 41 | Kontrola poznámok v karte zákazníka (alergie, typ pleti) | Pracovník | Vykonanie ošetrenia | User |
| 42 | Kontrola kontraindikácií | Pracovník | Vykonanie ošetrenia | Manual |
| 43 | Úprava postupu ošetrenia | Pracovník | Vykonanie ošetrenia | Manual |
| 44 | Príprava pracoviska a pomôcok | Pracovník | Vykonanie ošetrenia | Manual |
| 45 | Odlíčenie a diagnostika pleti | Pracovník | Vykonanie ošetrenia | Manual |
| 46 | Vykonanie ošetrenia | Pracovník | Vykonanie ošetrenia | Manual |
| 47 | Odporúčanie domácej starostlivosti | Pracovník | Vykonanie ošetrenia | Manual |
| 48 | Zápis poznámky k termínu | Pracovník | – | User |
| 49 | Zadanie upravenej sumy (zľava, doplnková služba) | Pracovník | – | User |
| 50 | Prijatie platby | Pracovník | – | Receive |
| 51 | Dokončenie termínu so spôsobom platby | Pracovník | – | User |
| 52 | Zobrazenie konfliktu stavu termínu | Pracovník | – | User |
| 53 | Validácia údajov a súhlasu | CRM systém | – | Service |
| 54 | Pridelenie pracovníka s najmenším počtom termínov | CRM systém | – | Business rule |
| 55 | Overenie dostupnosti slotu | CRM systém | – | Service |
| 56 | Zamietnutie: termín už nie je voľný (409) | CRM systém | – | Service |
| 57 | Vyhľadanie alebo vytvorenie zákazníka podľa e-mailu | CRM systém | – | Service |
| 58 | Uloženie termínu v stave Scheduled | CRM systém | – | Service |
| 59 | Zobrazenie potvrdenia rezervácie | CRM systém | – | Send |
| 60 | Zobrazenie dôvodu zamietnutia | CRM systém | – | Send |
| 61 | Overenie slotu a uloženie nového času | CRM systém | – | Service |
| 62 | Overenie roly a vlastníctva termínu | CRM systém | Zrušenie termínu | Service |
| 63 | Kontrola stavu a času začiatku | CRM systém | Zrušenie termínu | Business rule |
| 64 | Zmena stavu na Cancelled | CRM systém | Zrušenie termínu | Service |
| 65 | Zápis času zrušenia | CRM systém | Zrušenie termínu | Service |
| 66 | Uloženie stavu NoShow | CRM systém | – | Service |
| 67 | Overenie prechodu do stavu Completed | CRM systém | – | Business rule |
| 68 | Uloženie stavu Completed a platby v jednej transakcii | CRM systém | – | Service |
| 69 | Overenie session a roly | CRM systém | – | Service |
| 70 | Vrátenie výsledku overenia | CRM systém | – | Send |
| 71 | Overenie interného API kľúča | CRM systém | – | Service |
| 72 | Zostavenie pseudonymizovaného exportu | CRM systém | – | Service |
| 73 | Odoslanie exportu | CRM systém | – | Send |
| 74 | Overenie session cez hlavný backend | ML služba | – | Send |
| 75 | Prijatie výsledku overenia | ML služba | – | Receive |
| 76 | Získanie zámku behu | ML služba | – | Service |
| 77 | Vyžiadanie exportu z hlavného backendu | ML služba | – | Send |
| 78 | Prijatie exportu | ML služba | – | Receive |
| 79 | Uloženie dát do raw tabuliek (sync) | ML služba | – | Service |
| 80 | Zostavenie snapshotov k 8 dátumom | ML služba | Tréning modelu a predikcia rizika | Service |
| 81 | Výpočet príznakov bez úniku budúcich dát | ML služba | Tréning modelu a predikcia rizika | Service |
| 82 | Výpočet labelov (odchod do 90 dní) | ML služba | Tréning modelu a predikcia rizika | Service |
| 83 | Tréning LogisticRegression | ML služba | Tréning modelu a predikcia rizika | Service |
| 84 | Tréning RandomForest | ML služba | Tréning modelu a predikcia rizika | Service |
| 85 | Tréning HistGradientBoosting | ML služba | Tréning modelu a predikcia rizika | Service |
| 86 | Vyhodnotenie baseline pravidla | ML služba | Tréning modelu a predikcia rizika | Business rule |
| 87 | Výber víťaza podľa ROC-AUC | ML služba | Tréning modelu a predikcia rizika | Business rule |
| 88 | Výpočet dôležitosti príznakov | ML služba | Tréning modelu a predikcia rizika | Service |
| 89 | Pretrénovanie víťaza na všetkých snapshotoch | ML služba | Tréning modelu a predikcia rizika | Service |
| 90 | Uloženie aktívnej verzie modelu | ML služba | Tréning modelu a predikcia rizika | Service |
| 91 | Predikcia rizika pre zákazníkov | ML služba | Tréning modelu a predikcia rizika | Service |
| 92 | Generovanie zdôvodnení | ML služba | Tréning modelu a predikcia rizika | Service |
| 93 | Odoslanie súhrnu behu | ML služba | – | Send |
| 94 | Vrátenie chyby (ProblemDetails) | ML služba | – | Send |
