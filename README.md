# Assets Portal

## Windows identity, DPAPI, and Jira owner matching

The working 7z package used a Jira AQL reference constraint on `Osoba_odpowiedzialna."Email Address"`. The new `/api/assets` endpoint reconstructs that constraint from the **authenticated Windows login** and `Jira:UserEmailDomain`. When `UserEmailDomain` is absent, it falls back to the domain part of the configured Jira account email, then to the Windows server's AD DNS domain if the older Jira:BaseUrl/Token configuration is used. A Windows identity containing a complete UPN uses its own domain. The principal is never taken from an untrusted URL or request body. Login/derived email must pass strict validation before inclusion in AQL.

The previously added raw `OwnerAttributeId` value comparison remains for compatibility; creating reports for assets owned through a Jira reference checks the server-side AQL ownership query. Team hierarchy stays separately controlled by `team.json`.

The isolated `C:\ProgramData\Assets\Data\keys` key ring is now encrypted with **machine-scope Windows DPAPI**, rather than user-scope DPAPI, because the IIS application-pool token may not have a loaded user profile. This relies on the strict NTFS ACL configured by the production updater. Do not copy keys from a different application. Pre-existing user-scoped keys may still need authorized recovery under their original identity; do not delete existing keys without a backup and an inventory of encrypted settings. A failing `/api/me` returns a server error; `401` is authentication and `403` means access denied by the portal policy.


## Separate Assets configuration and first deployment

This repository (`Eris92/Assets`) is **not** the separate site at `C:\Sites\AssetsPortal`. Never copy configuration, databases, or DPAPI keys from that other project.

The Assets application uses its own directory `C:\ProgramData\Assets`:
- `appsettings.json` - configuration and bootstrap administrator SID
- `Data\portal.db` - SQLite reports, settings, and access rules
- `Data\keys` - DPAPI-protected data protection keys
- `team.json` - optional team directory

If a checkout-local `C:\Sites\Assets\appsettings.json` points to a database outside `C:\ProgramData\Assets`, the updater preserves that local Assets configuration but rewrites **only** `Portal:DatabasePath` in its new ProgramData copy to `C:\ProgramData\Assets\Data\portal.db`. It does not open the old database, import DPAPI keys, touch the original file, or read `C:\Sites\AssetsPortal`. An existing `C:\ProgramData\Assets\appsettings.json` is validated and never silently replaced. The production updater automatically creates the Assets configuration from `appsettings.org.json` if missing and bootstraps only the elevated account running deployment as recovery administrator. It never imports data from `C:\Sites\AssetsPortal`. Jira settings (URL, credentials, AQL, workspace and attribute IDs) must be configured for Assets separately in the portal. The updater will not silently invent these credentials. Existing `C:\ProgramData\Assets\appsettings.json` is always preserved.

Run the one-command updater after the GitHub Actions build/release jobs finish:

~~~powershell
& 'C:\Sites\Assets\scripts\Update-Production.ps1'
~~~


## One-command GitHub -> IIS production update

Source checkout stays in C:\Sites\Assets. Each released application version goes into C:\Sites\AssetsDeploy\releases\<commit-sha>; IIS Assets-KL points to a separate release directory. No NuGet access or .NET 10 SDK is needed on the server, only the ASP.NET Core Hosting Bundle 10 and IIS Windows Authentication.

After GitHub Actions Build and release jobs complete, run in elevated Windows PowerShell:

~~~powershell
& 'C:\Sites\Assets\scripts\Update-Production.ps1'
~~~

That single command performs git pull --ff-only origin main, gets the latest public GitHub Release, verifies that its tag matches the checked-out commit, verifies SHA256 of its ZIP, stages the compiled DLLs, backs up IIS settings/ProgramData, preserves configuration and data, switches the IIS PhysicalPath, sets Windows Authentication enabled and Anonymous disabled, and restarts only the app pool. Local source and production configuration are not overwritten. If the release is still building, it safely stops without changing IIS; rerun after CI succeeds.

Optional parameters: -SiteName, -DeployRoot, -WhatIf, -HealthUrl (requires trusted HTTPS and access for the logged-in account). Deploy backups are stored at C:\ProgramData\Assets-Deploy\Backups\<timestamp> with restricted ACLs; deployment.json records the prior IIS PhysicalPath. For rollback, point the IIS site back to the prior PhysicalPath, restart its app pool and, only if necessary, restore the matching ProgramData backup. Verify /api/me and /api/assets after deployment.

## Aktualizacja IIS na framework-dependent .NET 10

Repozytorium zawiera tylko kod i szablony; archiwum dzialajacej instalacji zawiera sekrety, logi oraz klucze Data Protection, ktorych nie wolno dodawac do Git ani do paczki release.

1. Zainstaluj **ASP.NET Core Hosting Bundle 10** na serwerze IIS. Sprawdz `dotnet --list-runtimes` - wymagane sa `Microsoft.NETCore.App 10.x` i `Microsoft.AspNetCore.App 10.x`. Skonfiguruj Windows Authentication=Enabled i Anonymous Authentication=Disabled w IIS.
2. Zrob backup calego katalogu aplikacji, `C:\ProgramData\Assets\appsettings.json`, `portal.db` oraz katalogu `keys`. Stary `appsettings.json` moze pozostac w katalogu IIS; korzystniej przeniesc go do ProgramData. Jesli w ProgramData jest juz konfiguracja, **nie nadpisuj jej**: ma pierwszenstwo przed plikiem obok DLL. Czesc ustawien Jira ma dodatkowo pierwszenstwo z tabeli Settings w SQLite.
3. Zatrzymaj app pool na czas podmiany plikow. Skopiuj czysty pakiet z `out\AssetsPortal` lub artifact GitHub Actions. Nie kopiuj z 7z starych `coreclr.dll`, `hostfxr.dll`, `AssetsPortal.exe`, logow ani kluczy DPAPI.
4. Uruchom app pool, przetestuj `/api/me` (200, `allowed:true`) i `/api/assets` (200, JSON) oraz `scripts/Test-PortalAccess.ps1`. Gdy `/api/me` zwraca 401, diagnozuj IIS/Kerberos; gdy 200 z `allowed:false`, dodaj uprawnienia SID z konta administratora odzyskiwania. Nie wylaczaj deny-by-default.
5. Rollback: przywroc poprzedni pakiet razem z kopia bazy i kluczy. Aktualizacja aplikacji nie cofa korekt wykonanych w Jira.

Publikacja przez `scripts/Publish.ps1` dziala tylko wewnatrz katalogu `out` repozytorium i usuwa stary wygenerowany output przed kompilacja. Celowo nie zapisuje niczego w ProgramData ani w katalogu produkcyjnym.


## Portal access and Windows sign-in

Windows Authentication in IIS authenticates the user. The portal separately authorizes that Windows identity. There is no local password database or Entra login form.

On the IIS server, run elevated PowerShell from the updated repository:

```powershell
.\scripts\Initialize-PortalAdmin.ps1 -Account 'DOMAIN\administrator'
```

This resolves the account to a SID, backs up the existing JSON and adds only the recovery administrator, preserving the other configuration. Omit -Account to use the current elevated Windows account. No password is stored. Reload the portal after running it.

Alternatively, set a recovery administrator in `C:\ProgramData\Assets\appsettings.json`:

```json
"Portal": {
  "AdminUsers": ["DOMAIN\\administrator"],
  "AdminGroup": "DOMAIN\\Assets-Portal-Admins",
  "DatabasePath": "C:\\ProgramData\\Assets\\Data\\portal.db"
}
```

Merge these keys into the existing Portal object; preserve all existing Jira settings and other keys. AdminUsers accepts full Windows account names or SIDs. Use a real account/group, not the sample names. Recovery administrators have access even if a UI rule blocks them; recovery configuration requires server administrator access. The AdminGroup saved in the database takes precedence over the bootstrap group in the file; AdminUsers is always read from the server file and can recover a stale group setting.

Log in as a recovery administrator and open **Ustawienia > Dostep do portalu**. Add a Windows user or AD group by DOMAIN\account or SID, choose User/Admin and Allow/Deny. Rules are stored by SID. Deny overrides grants except for recovery administrators. Unlisted users are denied by default. Existing authenticated users therefore need an explicit allow rule or an allowed AD group after this update. Delegated administrators cannot change/remove rules matching themselves; a recovery administrator can. All rule changes are audited in SQLite. Removing an allow rule does not block access inherited from another group; add a Deny rule to block it.

All business API routes enforce access on the server. `/api/me` returns the authenticated account and whether portal access is granted; this permits distinguishing successful Windows login from missing authorization. No business data is returned to denied users. Access decisions are re-evaluated on every API request; Windows group membership changes still require a refreshed Windows logon token.

For failed sign-in run (on a domain Windows computer, with the real URL):

```powershell
.\scripts\Test-PortalAccess.ps1 -PortalUrl 'https://portal.example.com/' -SiteName 'Assets'
```

The script is read-only, uses the current Windows credentials, preserves TLS validation, prints status/WWW-Authenticate/account/role without CSRF tokens, and reads effective IIS settings if run on the server. For a virtual IIS application pass its full application URL. A 401 means authentication failed, 403 means portal authorization failed, 500/502 indicates application/API failure, and 404 or HTML instead of JSON indicates incorrect deployment/routing. Windows Authentication enabled and Anonymous disabled alone do not establish the cause of a 401: correlate IIS sc-status, sc-substatus and sc-win32-status, URL, browser intranet/SSO settings and Kerberos/SPN negotiation. Do not disable authentication, TLS checks or loopback protection as a workaround.

Official IIS Windows Authentication reference: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0

Backup the application directory, portal.db and Data Protection keys before upgrading. Roll back with the prior app and database backup; older application versions do not enforce the new access rules.

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

The production frontend is in `wwwroot`, connected to ASP.NET Core API. Publishing generates a framework-dependent .NET 10 Windows package in `out/AssetsPortal`, without a bundled runtime or apphost EXE. .NET 10 SDK is only needed to build. IIS requires Hosting Bundle 10 and Windows Authentication. The build script never creates or changes server configuration and refuses to erase live data.

GitHub Actions builds and uploads the ready-to-download `AssetsPortal-win-x64` artifact on every push to main. Download it from the successful workflow run and extract it to the IIS application directory after backing up the prior deployment.

## Integrated UI and reports

- Equipment lists with search, type/status/location filters, sorting, and team hierarchy.
- Own reports: not-held equipment or attribute correction.
- Manager reports: employee left, incorrect assignment, or attribute correction.
- New reports persist in SQLite. Assignment removal occurs only on administrator approval; source changes since reporting produce 409.
- Permissions, handover protocol, and Entra integration remain placeholders pending data integration.
- The existing Reports table is extended automatically with Type and AffectedEmployee; existing reports default to attribute correction. Back up portal.db and Data Protection keys before updating.

## Team hierarchy

Copy `team.org.json` to `C:\ProgramData\Assets\team.json` and replace the sample identities with real `DOMAIN\login`, names, and manager logins. This is an administrator-maintained hierarchy, not automatic AD/Entra discovery. Restrict write access to administrators; grant the application read-only access. The server derives direct and indirect reports from this file and checks ownership when listing or reporting team equipment. An empty or missing file means no team permissions. Windows Authentication determines the caller; admin membership alone does not grant access to other teams.

Jira owner values must match the Windows login or its SAM account name. Ambiguous team ownership is excluded. Approving an assignment report clears the owner attribute; validate that the Jira attribute allows an empty value before production use. Jira errors leave the report pending.

AQL pagination uses the official `startAt`, `maxResults`, and `includeAttributes` query parameters: https://developer.atlassian.com/cloud/assets/rest/api-group-object/#api-object-aql-post. Query cap is 100 pages of up to 100 objects; exceeding the cap returns an error rather than silently returning incomplete equipment.

## Wymagania

- Windows Server z IIS, Windows Authentication, ASP.NET Core Hosting Bundle dla .NET 10.
- .NET 10 SDK na maszynie publikujacej.
- Jira Assets Cloud: `cloudId`, `workspaceId`, token uslugi z prawem odczytu i zapisu obiektow.
- Grupa AD dla administratorow. Konto app pool ma zapis w katalogu bazy i wyjscie HTTPS do Atlassian.

## Konfiguracja

1. Run `.\scripts\Publish.ps1` on a machine with .NET 10 SDK. Copy the contents of `out\AssetsPortal` into IIS only after backing up the running deployment; never publish directly into the IIS folder.
2. W IIS utworz aplikacje z osobnym app pool (`No Managed Code`), HTTPS, zainstaluj **ASP.NET Core Hosting Bundle 10** i role service Windows Authentication. Skonfiguruj na poziomie aplikacji/site w IIS: **Windows Authentication = Enabled**, **Anonymous Authentication = Disabled**. Te opcje nie sa teraz w `web.config`, aby uniknac HTTP 500.19, gdy sekcje authentication sa zablokowane.
3. W repo jest tylko wzor `appsettings.org.json`. Przy pierwszym wdrozeniu skopiuj go do `C:\ProgramData\Assets\appsettings.json` (jesli plik juz istnieje, nie nadpisuj go):

   ```powershell
   New-Item -ItemType Directory -Path 'C:\ProgramData\Assets' -Force | Out-Null
   if (-not (Test-Path 'C:\ProgramData\Assets\appsettings.json')) {
       Copy-Item 'C:\Sites\Assets\appsettings.org.json' 'C:\ProgramData\Assets\appsettings.json'
   }
   ```

   Ustaw tam `Portal:AdminGroup` na pelna nazwe grupy AD, np. `DOMAIN\\Assets-Portal-Admins`, lub SID. To bootstrap administratora; sama grupa nie uwierzytelnia uzytkownika, najpierw IIS musi przekazac Windows identity do `/api/me`. Po pierwszym zapisie w panelu grupa jest rowniez utrwalona w SQLite. Aplikacja nadal odczyta stary `appsettings.json` z katalogu aplikacji, jesli jeszcze nie przeniosles go do ProgramData; konfiguracja z ProgramData ma pierwszenstwo. Kolejny `dotnet publish` nie nadpisze pliku w ProgramData.
4. Ustaw ACL na `C:\ProgramData\Assets` dla tozsamosci app pool (Modify) pod SQLite i klucze Data Protection; ogranicz dostep innym kontom. Dla samego `appsettings.json` ustaw aplikacji tylko odczyt, a zapis pozostaw administratorom serwera. Utrzymuj kopie katalogu wraz z baza i kluczami.
5. Otworz portal jako czlonek grupy. W prawym gornym rogu widac konto i role; w menu **Ustawienia** uzupelnij Jira URL (`https://firma.atlassian.net`), email konta serwisowego, API token, Cloud ID, AQL, ID atrybutu wlasciciela i liste ID pol dopuszczonych do korekty. Token jest szyfrowany w bazie przy uzyciu ASP.NET Core Data Protection; nie jest zwracany do przegladarki. Pozostawienie pola tokenu pustego zachowuje obecny token.
6. Gdy workspace ID nie jest znane, zapisz ustawienia i uzyj **Wykryj workspace**, a potem zapisz wykryte ID. Wartosc atrybutu wlasciciela musi odpowiadac `DOMAIN\login` lub `login` (sprawdz odpowiedz Jira).

API Jira Assets uzywa Basic auth z `email:API token` i tworzy adres z Cloud ID i workspace ID. Starszy `Jira:BaseUrl` z Bearer tokenem jest nadal odczytywany, ale tylko gdy wskazuje na `https://api.atlassian.com/ex/jira/.../jsm/assets/workspace/...`. Przy migracji nie kopiuj kluczy DPAPI z archiwum na inny serwer/kontekst; zachowaj oryginalny katalog kluczy aktualnej instancji i jej baze danych.

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
