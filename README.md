# Assets Portal (IIS / Windows Authentication)

Pierwsza wersja: przypisane zasoby Jira Assets, zgloszenie korekty, kolejka administratora, zatwierdzenie z zapisem do Jira lub odrzucenie. Repozytorium ma jeden adapter Jira; kolejne zrodla (np. Azure) wymagaja osobnego adaptera i mapowania tozsamosci.

## Wymagania

- Windows Server z IIS, Windows Authentication, ASP.NET Core Hosting Bundle dla .NET 10.
- .NET 10 SDK na maszynie publikujacej.
- Jira Assets Cloud: `cloudId`, `workspaceId`, token uslugi z prawem odczytu i zapisu obiektow.
- Grupa AD dla administratorow. Konto app pool ma zapis w katalogu bazy i wyjscie HTTPS do Atlassian.

## Konfiguracja

1. `dotnet publish .\AssetsPortal.csproj -c Release -o C:\Sites\AssetsPortal`
2. W IIS utworz aplikacje z osobnym app pool (`No Managed Code`), HTTPS i zainstaluj role service **Windows Authentication**. `web.config` wlacza Windows Authentication i wylacza Anonymous Authentication; sprawdz wynik w IIS > aplikacja > Authentication. Jesli IIS blokuje te sekcje, ustaw je na poziomie site w IIS i odblokuj delegowanie konfiguracji lub usun sekcje `<security>` z `web.config`.
3. Ustaw `Portal:AdminGroup` w `appsettings.json` na pelna nazwe grupy AD, np. `DOMAIN\\Assets-Portal-Admins`, lub SID. To jest bootstrap administratora; sama grupa nie uwierzytelnia uzytkownika, najpierw IIS musi przekazac Windows identity do `/api/me`.
4. Ustaw ACL na `C:\ProgramData\AssetsPortal` dla tozsamosci app pool (Modify), ogranicz dostep innym kontom. Katalog zawiera SQLite oraz chronione klucze Data Protection. Utrzymuj kopie katalogu wraz z baza i kluczami.
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

- Widok pobiera pierwsze 100 obiektow AQL; trzeba uzgodnic filtr AQL po wlascicielu/paginacje przy wiekszej skali.
- Korekta obsluguje pojedyncza wartosc tekstowa. Jira API i rzeczywiste typy atrybutow trzeba sprawdzic na reprezentatywnym obiekcie testowym.
- SQLite nadaje sie do pojedynczej instancji IIS. Przy HA wymagany jest wspolny transactional store i outbox dla operacji w Jira.
- Zapis w zewnetrznym API i status w bazie nie stanowia jednej transakcji. Przy awarii miedzy PUT i zapisem statusu administrator powinien porownac stan w Jira przed ponowieniem.

## Rollback

Przywroc poprzedni katalog publikacji i kopie `portal.db`. Zmiany juz zaakceptowane w Jira wymagaja osobnej korekty wedlug historii zgloszen; rollback aplikacji nie cofa zmian w Jira.
