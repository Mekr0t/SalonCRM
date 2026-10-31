# Overenie pracovných obrazoviek (issue #7)

## Automatické kontroly

```sh
cd frontend
npm run build
npm run lint
npm test
# z koreňa repozitára, pri bežiacom Dockeri:
dotnet test backend-main/SalonCrm.slnx
```

Frontendové testy overujú dátumy a časové pásmo vrátane DST, rozsahy filtrov, dostupnosť akcií podľa stavu a roly, sumy platieb, validáciu kontaktu a poznámok, stránkovanie, serializáciu requestov a spracovanie 401/409. Backendové integračné testy overujú rozsah dát pracovníka, kolízie a prechody stavov, služby, štatistiky a zrušenie sessions pri deaktivácii.

## Manuálny preklik

Použite lokálne demo dáta (`SEED_DEMO_DATA=true`) a dva oddelené profily prehliadača: admin a pracovník. Demo účty a heslá sú opísané v README. Kroky vykonávajte nad lokálnymi dátami.

1. Prihláste obe roly a obnovte chránenú stránku. Pracovník nemá administráciu v menu; pri priamom otvorení admin cesty nedostane jej obsah.
2. V kalendári prejdite týždeň/deň/mesiac, otvorte udalosť a porovnajte čas s detailom. Admin zvolí aj iného pracovníka; pracovník vidí iba svoje termíny. Skontrolujte aj iné časové pásmo počítača a týždeň zmeny letného času.
3. V zozname nastavte dátumy, stav a admin filter pracovníka. Otestujte prázdny výsledok aj neplatný rozsah.
4. Vytvorte telefonickú rezerváciu s GDPR. Pracovník môže rezervovať iba svoju priradenú službu, admin aj pre iného pracovníka alebo „ktokoľvek“. V otváracích hodinách možno rezervovať aj menej než hodinu dopredu.
5. Rovnaký čas skúste obsadiť v dvoch oknách: iba jeden request uspeje, druhý ukáže zrozumiteľnú kolíziu a zachová vstupy.
6. Preplánujte naplánovaný termín, uložte a vymažte internú poznámku. Po začiatku termínu ho dokončite s platbou (aj 0 € alebo zľava); ďalší označte ako neúčasť. Konečné stavy už neponúkajú zmenu stavu. Pracovník môže rušiť iba budúci termín, admin aj minulý; potvrdenie možno zavrieť bez zmeny.
7. Vyhľadajte zákazníka menom/e-mailom/telefónom, prejdite stránky, upravte kontakt a poznámky, otvorte termín z histórie. Pri spoločnom zákazníkovi pracovník vidí iba vlastné návštevy a platby; cudzí detail vráti 404.
8. Admin overí KPI pre rôzne obdobia, mesačný graf aj jeho tabuľku, top služby a vyťaženosť pracovníkov. Otestujte obdobie bez návštev a príliš dlhé obdobie.
9. Vytvorte a upravte službu aj používateľa; priraďte služby a overte ich ponuku pri rezervácii. Deaktivujte/aktivujte službu. Existujúce termíny si ponechajú pôvodnú cenu.
10. Deaktivujte prihláseného pracovníka. Admin dostane počet budúcich termínov, sessions pracovníka zmiznú. Jeho ďalší request (alebo návrat do okna) ho presmeruje na login. Admin nemôže deaktivovať seba ani si odobrať rolu.
11. Resetujte heslo testovacieho používateľa a overte zrušenie ostatných sessions. Ručne zrušte inú aj vlastnú session; pri vlastnej sa admin odhlási.
12. Pri vypnutom API overte chybu a tlačidlo zopakovania, po obnovení spojenia návrat dát. Overte úzke okno/tablet, klávesnicu, fokus a zatváranie potvrdení klávesom Escape.

## Stav overenia pri implementácii

Build, lint, 16 frontendových testov a 245 backendových testov prešli. S reálnymi demo dátami lokálneho API prešlo tiež 15 kontrol serverového vykreslenia deviatich obrazoviek pre obe roly (vrátane histórie, platieb, štatistík a rozsahu dát pracovníka). Serverové vykreslenie nekontroluje rozloženie ani interakcie v prehliadači.
