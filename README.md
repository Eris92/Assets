# Assets Portal (IIS / Windows Authentication)

Pierwsza wersja: przypisane zasoby Jira Assets, zgloszenie korekty, kolejka administratora, zatwierdzenie z zapisem do Jira lub odrzucenie. Repozytorium ma jeden adapter Jira; kolejne zrodla (np. Azure) wymagaja osobnego adaptera i mapowania tozsamosci.

## Wymagania

- Windows Server z IIS, Windows Authentication, ASP.NET Core Hosting Bundle dla .NET 10.
- .NET 10 SDK na maszynie publikujacej.
- Jira Assets Cloud: `cloudId`, `workspaceId`, token uslugi z prawem odczytu i zapisu obiektow.
- Grupa AD dla administratorow. Konto app pool ma zapis w katalogu bazy i wyjscie HTTPS do Atlassian.

## Konfiguracja

1. `dotnet publish .\AssetsPortal.csproj -c Release -o C:\Sites\AssetsPortal`
2. W IIS utworz aplikacje z osobnym app pool (`No Managed Code`), HTTPS; wlacz **Windows Authentication**, wylacz **Anonymous Authentication**. Ustaw ACL na `C:\ProgramData\AssetsPortal` dla tozsamosci app pool.
3. Skonfiguruj `Jira:BaseUrl` w formacie `https://api.atlassian.com/ex/jira/{cloudId}/jsm/assets/workspace/{workspaceId}/v1/`.
4. Ustaw `Jira:OwnerAttributeId` na ID atrybutu wlasciciela; jego wartosc musi odpowiadac `DOMAIN\login` lub `login`. Zweryfikuj faktyczna reprezentacje wlasciciela w odpowiedzi Jira.
5. Ustaw `Jira:AllowedCorrectionAttributeIds` na jawna liste ID tekstowych pol dopuszczonych do automatycznej korekty. Nie dodawaj atrybutow referencyjnych, wielowartosciowych, systemowych ani tajnych bez dedykowanego mapowania.
6. Ustaw `Portal:AdminGroup` na pelna nazwe grupy AD.
7. Ustaw sekret przez zmienna srodowiskowa procesu IIS `Jira__Token`; nie wpisuj tokenu do `appsettings.json`. Zabezpiecz konfiguracje IIS i wykonaj recycle app pool.

Skrypt `Jira Asset Protocol.ps1` udostepnia AQL `objectType in objectTypeAndChildren("Sprzęt użytkownika")`, ale nie zawiera implementacji API ani ID atrybutow. Potrzebne sa rzeczywiste ID z Jira.

## Weryfikacja

- Zalogowany uzytkownik: `/api/me` zwraca tozsamosc, `/api/assets` tylko obiekty przypisane do konta; inne obiekty nie moga byc zgloszone przez manipulacje requestem.
- Konto spoza grupy admin: `/api/reports` pokazuje tylko wlasne sprawy; POST decyzji zwraca 403.
- Admin: zatwierdzenie zmienia pojedynczy atrybut w Jira, a ponowna decyzja zwraca 409. Zmiana wartosci w Jira przed decyzja rowniez zwraca 409.
- Odrzucenie nie modyfikuje Jira. Sprawdz dostep HTTPS, atrybuty i uprawnienia tokenu na srodowisku testowym przed produkcja.

## Ograniczenia pierwszej wersji

- Widok pobiera pierwsze 100 obiektow AQL; trzeba uzgodnic filtr AQL po wlascicielu/paginacje przy wiekszej skali.
- Korekta obsluguje pojedyncza wartosc tekstowa. Jira API i rzeczywiste typy atrybutow trzeba sprawdzic na reprezentatywnym obiekcie testowym.
- SQLite nadaje sie do pojedynczej instancji IIS. Przy HA wymagany jest wspolny transactional store i outbox dla operacji w Jira.
- Zapis w zewnetrznym API i status w bazie nie stanowia jednej transakcji. Przy awarii miedzy PUT i zapisem statusu administrator powinien porownac stan w Jira przed ponowieniem.

## Rollback

Przywroc poprzedni katalog publikacji i kopie `portal.db`. Zmiany juz zaakceptowane w Jira wymagaja osobnej korekty wedlug historii zgloszen; rollback aplikacji nie cofa zmian w Jira.
