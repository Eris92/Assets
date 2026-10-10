# Assets Portal

## Download and build on another Windows PC

```powershell
git clone https://github.com/Eris92/Assets.git
Set-Location Assets
.\scripts\Publish.ps1
```

Existing checkout:

```powershell
git pull --ff-only origin main
.\scripts\Publish.ps1
```

The production frontend is in `wwwroot`, connected to ASP.NET Core API. `preview/` remains a separate static demonstration. Publishing outputs a self-contained Windows x64 app to `out/AssetsPortal`; .NET 10 SDK is needed to build. IIS still requires the ASP.NET Core Hosting Bundle and Windows Authentication. The script creates configuration in ProgramData only if missing and does not modify IIS or replace a running deployment.

GitHub Actions builds and uploads the ready-to-download `AssetsPortal-win-x64` artifact on every push to main. Download it from the successful workflow run and extract it to the IIS application directory after backing up the prior deployment.

## Integrated UI and reports

- Equipment lists with search, type/status/location filters, sorting, and team hierarchy.
- Own reports: not-held equipment or attribute correction.
- Manager reports: employee left, incorrect assignment, or attribute correction.
- New reports persist in SQLite. Assignment removal occurs only on administrator approval; source changes since reporting produce 409.
- Permissions, handover protocol, and Entra integration remain placeholders pending data integration.
- The existing Reports table is extended automatically with Type and AffectedEmployee; existing reports default to attribute correction. Back up portal.db and Data Protection keys before updating.

## Team hierarchy

Copy `team.org.json` to `C:\ProgramData\AssetsPortal\team.json` and replace the sample identities with real `DOMAIN\login`, names, and manager logins. This is an administrator-maintained hierarchy, not automatic AD/Entra discovery. Restrict write access to administrators; grant the application read-only access. The server derives direct and indirect reports from this file and checks ownership when listing or reporting team equipment. An empty or missing file means no team permissions. Windows Authentication determines the caller; admin membership alone does not grant access to other teams.

Jira owner values must match the Windows login or its SAM account name. Ambiguous team ownership is excluded. Approving an assignment report clears the owner attribute; validate that the Jira attribute allows an empty value before production use. Jira errors leave the report pending.

AQL pagination uses the official `startAt`, `maxResults`, and `includeAttributes` query parameters: https://developer.atlassian.com/cloud/assets/rest/api-group-object/#api-object-aql-post. Query cap is 100 pages of up to 100 objects; exceeding the cap returns an error rather than silently returning incomplete equipment.

## Wymagania

- Windows Server z IIS, Windows Authentication, ASP.NET Core Hosting Bundle dla .NET 10.
- .NET 10 SDK na maszynie publikujacej.
- Jira Assets Cloud: `cloudId`, `workspaceId`, token uslugi z prawem odczytu i zapisu obiektow.
- Grupa AD dla administratorow. Konto app pool ma zapis w katalogu bazy i wyjscie HTTPS do Atlassian.

## Konfiguracja

1. `dotnet publish .\AssetsPortal.csproj -c Release -o C:\Sites\AssetsPortal`
2. W IIS utworz aplikacje z osobnym app pool (`No Managed Code`), HTTPS i zainstaluj role service **Windows Authentication**. `web.config` wlacza Windows Authentication i wylacza Anonymous Authentication; sprawdz wynik w IIS > aplikacja > Authentication. Jesli IIS blokuje te sekcje, ustaw je na poziomie site w IIS i odblokuj delegowanie konfiguracji lub usun sekcje `<security>` z `web.config`.
3. W repo jest tylko wzor `appsettings.org.json`. Przy pierwszym wdrozeniu skopiuj go do `C:\ProgramData\AssetsPortal\appsettings.json` (jesli plik juz istnieje, nie nadpisuj go):

   ```powershell
   New-Item -ItemType Directory -Path 'C:\ProgramData\AssetsPortal' -Force | Out-Null
   if (-not (Test-Path 'C:\ProgramData\AssetsPortal\appsettings.json')) {
       Copy-Item 'C:\Sites\AssetsPortal\appsettings.org.json' 'C:\ProgramData\AssetsPortal\appsettings.json'
   }
   ```

   Ustaw tam `Portal:AdminGroup` na pelna nazwe grupy AD, np. `DOMAIN\\Assets-Portal-Admins`, lub SID. To bootstrap administratora; sama grupa nie uwierzytelnia uzytkownika, najpierw IIS musi przekazac Windows identity do `/api/me`. Po pierwszym zapisie w panelu grupa jest rowniez utrwalona w SQLite. Aplikacja nadal odczyta stary `appsettings.json` z katalogu aplikacji, jesli jeszcze nie przeniosles go do ProgramData; konfiguracja z ProgramData ma pierwszenstwo. Kolejny `dotnet publish` nie nadpisze pliku w ProgramData.
4. Ustaw ACL na `C:\ProgramData\AssetsPortal` dla tozsamosci app pool (Modify) pod SQLite i klucze Data Protection; ogranicz dostep innym kontom. Dla samego `appsettings.json` ustaw aplikacji tylko odczyt, a zapis pozostaw administratorom serwera. Utrzymuj kopie katalogu wraz z baza i kluczami.
5. Otworz portal jako czlonek grupy. W prawym gornym rogu widac konto i role; w menu **Ustawienia** uzupelnij Jira URL (`https://firma.atlassian.net`), email konta serwisowego, API token, Cloud ID, AQL, ID atrybutu wlasciciela i liste ID pol dopuszczonych do korekty. Token jest szyfrowany w bazie przy uzyciu ASP.NET Core Data Protection; nie jest zwracany do przegladarki. Pozostawienie pola tokenu pustego zachowuje obecny token.
6. Gdy workspace ID nie jest znane, zapisz ustawienia i uzyj **Wykryj workspace**, a potem zapisz wykryte ID. Wartosc atrybutu wlasciciela musi odpowiadac `DOMAIN\login` lub `login` (sprawdz odpowiedz Jira).

API Jira Assets uzywa Basic auth z `email:API token`. Portal tworzy adres API na podstawie Cloud ID i workspace ID, zamiast wymagac recznego wpisywania calego URL.

### Diagnostyka 401 / brak administratora

Na `/api/me` prawidlowe logowanie zwraca `name`, `authenticationType`, `admin` i `adminGroup`. Gdy zwraca 401, sprawdz IIS Windows Authentication=Enabled, Anonymous Authentication=Disabled, dostepnosc role service Windows Authentication i ponow zalogowanie w przegladarce. Gdy zwraca `admin:false`, porownaj `adminGroup` z grupa AD i `whoami /groups` na koncie uzytkownika; wyloguj i zaloguj Windows po dodaniu do grupy, aby odswiezyc token. Aplikacja sprawdza SID grupy z tokenu Windows, w tym grupy zagniezdzone.

Skrypt `Jira Asset Protocol.ps1` udostepnia AQL `objectType in objectTypeAndChildren("Sprzęt użytkownika")`, ale nie zawiera implementacji API ani ID atrybutow. Potrzebne sa rzeczywiste ID z Jira.

## Weryfikacja

- Zalogowany uzytkownik: `/api/me` zwraca tozsamosc, `/api/assets` tylko obiekty przypisane do konta; inne obiekty nie moga byc zgloszone przez manipulacje requestem.
- Konto spoza grupy admin: `/api/reports` pokazuje tylko wlasne sprawy; ustawienia i decyzje zwracaja 403.
- Admin: zatwierdzenie zmienia pojedynczy atrybut w Jira, a ponowna decyzja zwraca 409. Zmiana wartosci w Jira przed decyzja rowniez zwraca 409.
- Odrzucenie nie modyfikuje Jira. Sprawdz dostep HTTPS, atrybuty i uprawnienia tokenu na srodowisku testowym przed produkcja.

## Ograniczenia pierwszej wersji

- Widok pobiera strony AQL do limitu opisanego powyzej. Przy duzej skali ogranicz AQL.
- Korekta obsluguje pojedyncza wartosc tekstowa. Jira API i rzeczywiste typy atrybutow trzeba sprawdzic na reprezentatywnym obiekcie testowym.
- SQLite nadaje sie do pojedynczej instancji IIS. Przy HA wymagany jest wspolny transactional store i outbox dla operacji w Jira.
- Zapis w zewnetrznym API i status w bazie nie stanowia jednej transakcji. Przy awarii miedzy PUT i zapisem statusu administrator powinien porownac stan w Jira przed ponowieniem.

## Rollback

Przywroc poprzedni katalog publikacji i kopie `portal.db`. Zmiany juz zaakceptowane w Jira wymagaja osobnej korekty wedlug historii zgloszen; rollback aplikacji nie cofa zmian w Jira.
