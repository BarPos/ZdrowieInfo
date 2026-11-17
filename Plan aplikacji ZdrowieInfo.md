# Plan aplikacji ZdrowieInfo

Nov 17, 2026 · @Bartek

## Cel i zakres

Aplikacja sama sprawdza 23 publiczne strony NFZ, CeZ, MZ i CSIRT CeZ, wykrywa nowe wpisy i zleca Claude napisanie raportu istotnych zmian z cytowanymi źródłami. Raport trafia do aplikacji webowej (PWA na telefonie), a Ty dostajesz powiadomienie push.

- **Nazwa:** ZdrowieInfo (też nazwa rozwiązania i przestrzeni nazw).
- **Dla kogo:** jeden użytkownik (Ty), z możliwością dodania kluczy API dla programów i AI.
- **Co monitoruje:** awarie i przerwy w systemach, zmiany w P1 i CeR, nowe obowiązki sprawozdawcze, zmiany formatów XML, ostrzeżenia bezpieczeństwa.
- **Rytm:** jeden raport dziennie, gotowy przed 6:30, łączący wszystkie zmiany z danego dnia. Także w dni bez zmian.
- **Kontekst przychodni w raportach:** POZ, AOS, stomatologia, programy profilaktyczne, DiLO; podmiot kluczowy w KSC; lista używanych systemów do uzupełnienia w ustawieniach.
- **Hosting:** VPS x64, domena zmieniana w konfiguracji, HTTPS na reverse proxy (po Twojej stronie).
- **Interfejs:** tylko po polsku, bez usługi tłumaczeń.
- **Poza zakresem:** dane przychodni i pacjentów, logowanie do SZOI, jakiekolwiek dane rozliczeniowe. Aplikacja przetwarza wyłącznie publiczne komunikaty.

## Architektura

Jeden proces .NET na Ubuntu robi wszystko: pobiera strony, wykrywa zmiany, zleca raport Claude, generuje PDF, wysyła push i serwuje aplikację.

&#91;embedded content: architektura · jeden serwer, cztery systemy zewnętrzne\]

Dane płyną z góry na dół: pobranie, wykrycie zmian, raport z Claude, potem PDF i powiadomienie. Aplikacja webowa, API i serwer MCP czytają wszystko z PostgreSQL.

## Źródła i harmonogram

23 źródła w czterech grupach, sprawdzane wcześnie rano, żeby jeden raport dzienny był gotowy przed 6:30 (czas Warszawy):

- **5:00** – sprawdzenie źródeł należących do danego dnia: codziennie grupa dzienna; w poniedziałek także tygodniowa; 1. dnia miesiąca także miesięczna; 1. dnia miesiąca nieparzystego także dwumiesięczna.
- **ok. 5:30** – jeden raport dzienny ze wszystkich wykrytych zmian (albo raport „brak zmian”).
- **6:30** – najpóźniej wtedy raport jest gotowy do czytania. Powiadomienie push wychodzi od razu po jego zapisaniu (zwykle ok. 5:30).

Jeśli generowanie raportu się nie uda (np. błąd API), aplikacja ponawia próbę co 10 minut do 6:20. Potem zapisuje raport z listą wykrytych wpisów bez analizy i informacją o błędzie.

**Metoda** mówi, jak wykrywać zmiany: *lista wpisów* = porównanie listy tytułów i linków; *cała strona* = porównanie głównej treści (strony bez listy aktualności, np. opis komunikatu XML).

| Częstotliwość | Źródło | Metoda | Uwagi |
| --- | --- | --- | --- |
| Codziennie | [NFZ – Aktualności dla świadczeniodawców](https://www.nfz.gov.pl/dla-swiadczeniodawcy/aktualnosci) | lista wpisów | główne źródło przerw i zmian |
| Codziennie | [ezdrowie.gov.pl – dla podmiotów leczniczych](https://ezdrowie.gov.pl/portal/home/dla-podmiotow-leczniczych) | lista wpisów | przerwy w P1 i PUI |
| Codziennie | [CSIRT CeZ – wszystkie aktualności](https://csirt.cez.gov.pl/pl/wszystkie-aktualnosci) | lista wpisów | ostrzeżenia KSC |
| Codziennie | [Mazowiecki OW NFZ – aktualności dla świadczeniodawców](https://nfz-warszawa.pl/dla-swiadczeniodawcow/aktualnosci/) | lista wpisów | ochrona Incapsula, może wymagać Playwright |
| Codziennie | [MZ – Centralna e-rejestracja](https://www.gov.pl/web/zdrowie/centralna-e-rejestracja-cer) | cała strona | data przy tytule nieaktualna |
| Codziennie | [NFZ – Walidacje i weryfikacje](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/walidacje-i-weryfikacje/) | lista wpisów | CWW/CRW |
| Codziennie | [NFZ – Słowniki](https://www.nfz.gov.pl/dla-swiadczeniodawcy/slowniki/) | lista wpisów | ICD-9, harmonogramy |
| Tygodniowo | [MZ – Komunikaty](https://www.gov.pl/web/zdrowie/komunikaty1) | lista wpisów | dużo tematów spoza zakresu |
| Tygodniowo | [CeZ – Aktualności](https://www.cez.gov.pl/pl/page/o-nas/aktualnosci) | lista wpisów |  |
| Tygodniowo | [NFZ – Komunikaty Departamentu Informatyki](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/komunikaty-departamentu-informatyki/) | lista wpisów | pokrywa się z Aktualnościami NFZ |
| Miesięcznie | [Komunikat SWIAD](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/lista-typow-komunikatow-xml/komunikat-swiad/) | cała strona |  |
| Miesięcznie | [Komunikat DEKL](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/lista-typow-komunikatow-xml/komunikat-dekl/) | cała strona |  |
| Miesięcznie | [Komunikat LIOCZ](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/lista-typow-komunikatow-xml/komunikat-liocz/) | cała strona |  |
| Miesięcznie | [Komunikat KOL](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/lista-typow-komunikatow-xml/komunikat-kol/) | cała strona |  |
| Miesięcznie | [Komunikat UMX](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/lista-typow-komunikatow-xml/komunikat-umx/) | cała strona | UMX 4.2 od 1.01.2027 |
| Miesięcznie | [Interfejs SIMP](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/interfejsy-integracyjne/simp/) | cała strona |  |
| Co 2 miesiące | [Komunikat ZBPOZ](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/lista-typow-komunikatow-xml/komunikat-zbpoz/) | cała strona |  |
| Co 2 miesiące | [Komunikat e-Faktury](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/lista-typow-komunikatow-xml/komunikat-e-faktury-e-faktura-e-rachunek/) | cała strona |  |
| Co 2 miesiące | [Komunikat SP\_ROZ](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/lista-typow-komunikatow-xml/komunikat-sproz/) | cała strona |  |
| Co 2 miesiące | [Interfejs DiLO](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/interfejsy-integracyjne/dilo/) | cała strona |  |
| Co 2 miesiące | [Interfejs eWUŚ](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/interfejsy-integracyjne/ewus/) | cała strona |  |
| Co 2 miesiące | [Interfejs KOLCE](https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/interfejsy-integracyjne/kolce/) | cała strona |  |
| Co 2 miesiące | [Opieka koordynowana w POZ](https://www.nfz.gov.pl/dla-swiadczeniodawcy/opieka-koordynowana-w-poz/) | cała strona |  |

Lista źródeł jest danymi w bazie (tabela `sources`), nie kodem: dodanie lub zmiana częstotliwości nie wymaga wdrożenia.

## Pobieranie stron i wykrywanie zmian

Zmiany wykrywa deterministyczny kod w .NET, nie Claude. Claude dostaje tylko to, co się zmieniło od ostatniego sprawdzenia.

1. **Harmonogram.** `SchedulerService : BackgroundService` o 5:00 wybiera grupy źródeł należące do danego dnia z tabeli `sources` (parsowanie: Cronos), używa `TimeProvider` i strefy `Europe/Warsaw`. Każde uruchomienie to rekord `check_runs`.
2. **Pobranie.** `IHttpClientFactory`, własny User-Agent z adresem kontaktowym, `If-None-Match` / `If-Modified-Since`, odstęp 2–3 s między żądaniami do tej samej domeny, ponowienie z Polly przy błędzie 5xx.
3. **Ekstrakcja.** AngleSharp z selektorem CSS zapisanym przy źródle:
   - *lista wpisów*: selektor elementu wpisu + tytułu, linku i daty; klucz wpisu = bezwzględny URL linku,
   - *cała strona*: selektor głównej treści, normalizacja białych znaków, usunięcie banera cookies i stopki.
4. **Porównanie.** Nowy klucz wpisu = nowy wpis. Dla stron *cała strona* hash treści, a przy zmianie DiffPlex wyznacza zmienione fragmenty.
5. **Szczegóły wpisu.** Dla nowego wpisu aplikacja pobiera jego stronę i zapisuje tytuł, URL, datę publikacji oraz treść (do analizy). W raporcie przechowywany jest tylko fragment, do którego odnosi się dany punkt.
6. **Kontrola jakości.** Gdy selektor zwróci 0 wpisów albo strona odpowie błędem, źródło dostaje status *wymaga uwagi* otwiera się incydent w dzienniku (z powiadomieniem push), a źródło trafia do sekcji „Stan źródeł” w raporcie.

**Pierwsze uruchomienie** zapisuje stan bazowy bez raportu. Inaczej pierwszy raport zawierałby całe archiwum stron.

**Mazowiecki OW NFZ** jest chroniony przez Imperva Incapsula. Najpierw zwykły HttpClient; jeśli odpowiedzi są puste, Microsoft.Playwright z przeglądarką headless, uruchamiany raz dziennie. Jeśli to też nie zadziała, źródło zostaje oznaczone jako ręczne.

## Generowanie raportu przez Claude API

Codziennie powstaje dokładnie jeden raport. Jeśli były nowe wpisy, pisze go Claude przez Messages API; jeśli nie, serwer sam tworzy raport „Brak zmian” bez wywołania API.

**Opłata.** Subskrypcja Max nie daje dostępu do API wprost, ale [plany Max i Team zawierają miesięczne kredyty na Claude API](https://platform.claude.com/docs/en/about-claude/api-credits-for-subscribers): 100 USD (Max 5x) lub 200 USD (Max 20x). Odbierasz je, łącząc organizację w Claude Console z planem (claude.ai → Settings → Billing). Kredyty nie przechodzą na kolejny okres, a po ich wyczerpaniu żądania do API są wstrzymywane do następnego przydziału (chyba że dokupisz kredyty). Przy kilkunastu wywołaniach w miesiącu to więcej niż potrzeba.

**Przebieg:**

1. `DailyReportService` o 5:30 zbiera wszystkie nowe wpisy i zmiany z uruchomień danego dnia (wszystkie grupy razem) oraz stan źródeł.
2. **Brak zmian:** zapis raportu z flagą `is_empty = true`, tytułem „Brak zmian – \<data>” i sekcją „Stan źródeł”. Na liście raportów oznaczony jako pusty. Powiadomienie i tak wychodzi, z treścią „Brak zmian w monitorowanych źródłach”.
3. **Są zmiany:** `ReportComposer` składa wejście: profil przychodni, kryteria ważności, listę nowych wpisów z identyfikatorami (`item_id`), tytułami, datami, URL-ami i treścią lub diffem.
4. Wywołanie Messages API z odpowiedzią w formacie JSON według schematu (structured outputs albo narzędzie z JSON Schema). Stały prompt systemowy idzie z prompt caching.
5. Odpowiedź jest walidowana: każdy `item_id` musi istnieć, a każdy cytat musi być dosłownym fragmentem zapisanej treści wpisu. Niezgodność = ponowienie raz, potem raport oznaczony do ręcznej weryfikacji.
6. Zapis raportu, cytatów i kopii źródeł, eksport LaTeX i PDF, od razu powiadomienie push.

**Schemat odpowiedzi (skrót):**

- `title`, `summary` (2–3 zdania),
- `findings[]`: `importance` (krytyczna / wysoka / średnia / niska), `category` (awaria, P1, CeR, sprawozdawczość, bezpieczeństwo, rozliczenia, inne), `headline`, `body`, `action` (opcjonalnie: co zrobić i do kiedy), `citations[]` z `item_id` i `quote`,
- `ignored[]`: `item_id` + krótki powód pominięcia (np. temat dla aptek).

Lista `ignored` pozwala sprawdzić, czy Claude czegoś nie odrzucił niepotrzebnie.

**Kryteria ważności** (w prompcie systemowym, do zmiany w przyszłości):

| Ważność | Kiedy |
| --- | --- |
| krytyczna | awaria lub przerwa dziś albo jutro; termin krótszy niż 7 dni |
| wysoka | nowy obowiązek lub zmiana formatu z terminem do 30 dni; podatność w oprogramowaniu używanym w przychodni |
| średnia | zmiany do zaplanowania, termin powyżej 30 dni |
| niska | informacje bez działania po stronie przychodni |

**Profil przychodni** jest edytowany w panelu (tabela `app_settings`) i trafia do promptu przy każdym raporcie: zakresy świadczeń (POZ, AOS, stomatologia, profilaktyka, DiLO), status podmiotu kluczowego w KSC oraz lista używanych systemów (system gabinetowy, firewall, serwery, VPN). Bez tej listy podatności z CSIRT dostają ważność średnią.

**Stan źródeł** to stała sekcja każdego raportu, także pustego: które źródła sprawdzono, które zwróciły błąd lub 0 wpisów. Każdy nowy problem z dostępem do źródła wywołuje też osobne powiadomienie push i trafia do dziennika incydentów (sekcja Powiadomienia push).

**Model:** Claude Sonnet 5.5 jako domyślny, z możliwością zmiany w ustawieniach. W Claude Console osobny workspace dla ZdrowieInfo z miesięcznym limitem wydatków, bez auto-reload. Klucz API przechowywany przez Data Protection, nie w pliku konfiguracyjnym. Kod integracji powstaje od razu; klucz i test po odebraniu kredytów (od 18.11.2026).

**Endpoint dla zewnętrznych producentów.** Niezależnie od wbudowanego workera serwer ma `POST /api/reports` (klucz API z zakresem `reports:write`), przyjmujący ten sam JSON. Dzięki temu raport może dodać też inny program lub agent AI.

## Model danych (PostgreSQL)

Jedenaście tabel własnych plus tabele ASP.NET Core Identity. Schemat wyłącznie z migracji EF Core.

| Tabela | Kluczowe kolumny | Do czego |
| --- | --- | --- |
| `sources` | nazwa, url, grupa częstotliwości, metoda, selektory (jsonb), aktywne, status | konfiguracja 23 źródeł |
| `source_snapshots` | source\_id, pobrano, http\_status, hash treści | historia pobrań i diagnostyka |
| `source_items` | source\_id, klucz (URL), tytuł, url, data publikacji, treść, pierwszy raz widziany | wykryte wpisy; unikalne (source\_id, klucz); treść usuwana po 12 miesiącach |
| `source_incidents` | source\_id, początek, koniec, typ błędu (HTTP, timeout, blokada, 0 wpisów), szczegóły, liczba prób, push o awarii wysłany, push o przywróceniu wysłany | dziennik problemów z dostępem do źródeł, bezterminowo |
| `check_runs` | data, grupy, start, koniec, status, liczba nowych wpisów, report\_id | log uruchomień |
| `reports` | data raportu (unikalna), `is_empty`, status (gotowy / do weryfikacji / błąd AI), tytuł, podsumowanie, body (jsonb), stan źródeł (jsonb), ścieżki .tex/.pdf, `search_vector` (tsvector), czy wysłano push | raporty dzienne |
| `report_findings` | report\_id, ważność, kategoria, nagłówek, treść, działanie | punkty raportu |
| `finding_citations` | finding\_id, source\_item\_id, tytuł, url, data, fragment | kopia źródła dla konkretnego punktu |
| `api_keys` | nazwa, prefiks, hash SHA-256, zakresy, ostatnie użycie, unieważniony | klucze dla programów i AI |
| `push_subscriptions` | user\_id, endpoint, p256dh, auth, urządzenie, origin | subskrypcje Web Push |
| `app_settings` | klucz, wartość (jsonb, wrażliwe szyfrowane Data Protection) | profil przychodni, model, zapisany adres publiczny, klucze VAPID, klucz Claude API |

**Kopia źródła** w `finding_citations` zawiera tylko tytuł, URL, datę i cytowany fragment. Pełna treść wpisu w `source_items` służy analizie i weryfikacji cytatów; można ją usuwać po np. 12 miesiącach zadaniem porządkowym, bez utraty cytatów.

## Aplikacja webowa i PWA na telefonie

Strony do czytania renderowane statycznie po stronie serwera (static SSR), panel administracyjny interaktywny. Dzięki temu czytanie raportów nie zależy od połączenia SignalR, które zrywa się, gdy telefon usypia aplikację.

**Tryby renderowania (Blazor Web App, .NET 10):**

- Brak globalnego trybu interaktywnego; domyślnie wszystkie strony to static SSR z enhanced navigation (przejścia bez przeładowania całej strony).
- `@rendermode InteractiveServer` tylko na stronach panelu: źródła, uruchomienia, klucze API, ustawienia. Tam MudBlazor 9.
- Strony czytelnika (`/`, `/raporty/{id}`, `/szukaj`, /incydenty) w prostym HTML i CSS. Większość komponentów MudBlazor wymaga interaktywności, więc tu ich nie używasz.
- Wyszukiwanie jako zwykły formularz `GET /szukaj?q=`, działa bez JavaScriptu.

**Instalowalność (PWA):**

- `wwwroot/manifest.webmanifest`: `name`, `short_name`, `start_url: "/"`, `scope: "/"`, `display: "standalone"`, `theme_color`, ikony 192 i 512 px (plus `maskable`).
- W `App.razor`: `<link rel="manifest">`, `<meta name="theme-color">`, `apple-touch-icon` dla iOS.
- `wwwroot/service-worker.js`:
  - zasoby statyczne (`/css`, `/js`, ikony, `_content`): cache-first z wersją cache równą `AppVersion.Current`,
  - strony raportów: network-first z zapasem z cache, więc raz otwarty raport czytasz też offline,
  - zdarzenia `push` i `notificationclick` (otwiera `/raporty/{id}`).
- Rejestracja service workera w małym pliku `wwwroot/js/pwa.js`, dołączonym w `App.razor`.

**Uwaga dla iPhone'a:** zainstalowana PWA ma osobne ciasteczka niż Safari, więc logujesz się w niej raz osobno. Potem obowiązuje sesja 30-dniowa (następna sekcja).

## Uwierzytelnianie

ASP.NET Core Identity dla Ciebie (hasło + TOTP), osobny schemat uwierzytelniania dla kluczy API. Własne haszowanie PBKDF2 ze starego stacku nie jest potrzebne, bo Identity robi to samo.

**Użytkownik:**

- Rejestracja wyłączona. Konto tworzy polecenie serwera `--create-user <login>` (podobnie jak `--migrate-only`).
- MFA: aplikacja uwierzytelniająca (TOTP) z kodem QR (QRCoder) i kodami odzyskiwania. Opcjonalnie passkey, jeśli Identity w .NET 10 go obsłuży w Twojej konfiguracji.
- Blokada po 5 nieudanych próbach na 15 minut.

**Sesja 30 dni:**

- Cookie Identity z `ExpireTimeSpan = 30 dni`, `SlidingExpiration = true` i trwałym logowaniem („zapamiętaj mnie” zaznaczone domyślnie).
- `Secure`, `HttpOnly`, `SameSite=Lax`.
- Klucze Data Protection zapisane na dysku (już masz w stacku). Bez tego każdy restart serwera wylogowuje.
- Zmiana hasła unieważnia sesje (security stamp).

**Klucze API (dla programów, AI i serwera MCP):**

- Format `mon_<prefiks>_<sekret>`, sekret 32 losowe bajty. Klucz pokazywany raz przy utworzeniu.
- W bazie prefiks i hash SHA-256 sekretu. Przy tak losowym sekrecie PBKDF2 nie jest potrzebne.
- Zakresy: `reports:write` (dodawanie raportów), `mcp:read` (wyszukiwanie przez MCP).
- Nagłówek `Authorization: Bearer <klucz>`, własny `AuthenticationHandler`, polityki autoryzacji na zakresy, rate limiting (wbudowany middleware ASP.NET Core) na `/api/*` i `/mcp`.

## Powiadomienia push

Web Push z kluczami VAPID, wysyłany z serwera w dwóch przypadkach: gotowy raport dzienny i problem z dostępem do źródła. Bez zewnętrznych usług.

1. Adres aplikacji w konfiguracji: `ZdrowieInfo:PublicUrl` (np. `https://zdrowieinfo.example.pl`). Z niego wynikają linki w powiadomieniach, temat VAPID i sprawdzanie origin.
2. Klucze VAPID generowane automatycznie przy starcie (opis zmiany domeny niżej) i trzymane w `app_settings`, zaszyfrowane Data Protection.
3. W aplikacji przycisk „Włącz powiadomienia”: `pwa.js` prosi o zgodę, tworzy subskrypcję `pushManager.subscribe` i wysyła ją do `POST /api/push/subscribe` (chronione cookie użytkownika).
4. Od razu po zapisaniu raportu dziennego `PushNotifier` wysyła powiadomienie o raporcie dziennym: tytuł, liczba punktów krytycznych i wysokich, adres `/raporty/{data}`. Dla pustego raportu: „Brak zmian w monitorowanych źródłach”. Biblioteka: pakiet NuGet `WebPush`.
5. Subskrypcje, które zwrócą 404 lub 410, są usuwane.

**Problemy z dostępem do źródła (incydenty):**

- Incydent otwiera się, gdy źródło po 3 próbach w ciągu ok. 15 minut nadal zwraca błąd HTTP, przekroczenie czasu, blokadę (np. Incapsula) albo 0 wpisów. Pojedynczy błąd naprawiony ponowieniem nie jest incydentem.
- Przy otwarciu incydentu push wychodzi od razu: „Brak dostępu do źródła: \<nazwa>”, z typem błędu i linkiem do dziennika.
- Dopóki incydent jest otwarty, kolejne nieudane sprawdzenia tego źródła nie wysyłają nowych powiadomień, tylko zwiększają licznik prób.
- Dopóki incydent jest otwarty, źródło jest sprawdzane co godzinę, niezależnie od swojej grupy, żeby szybko wykryć powrót. Pierwsze udane sprawdzenie zamyka incydent i od razu wysyła push: „Dostęp przywrócony: \<nazwa>”, z czasem trwania przerwy. Informacja trafia też do najbliższego raportu dziennego.
- Dziennik incydentów w aplikacji (`/incydenty`): źródło, początek, koniec, czas trwania, typ błędu, szczegóły, liczba prób. Filtry po źródle i dacie; historia przechowywana bezterminowo.

**Zmiana domeny.** Klucze VAPID same w sobie nie są przypisane do domeny, ale subskrypcje push już tak: są związane z originem, na którym zarejestrowano service workera. Dlatego przy każdym starcie `VapidKeyManager` porównuje `PublicUrl` z adresem zapisanym w `app_settings`. Jeśli się różnią (albo kluczy nie ma):

- generuje nową parę kluczy VAPID i zapisuje nowy adres,
- usuwa stare subskrypcje (na nowym adresie i tak nie zadziałają),
- zapisuje w logu informację o zmianie.

Po zmianie domeny musisz raz zainstalować aplikację z nowego adresu, zalogować się i ponownie włączyć powiadomienia: ciasteczka i PWA są przypisane do domeny. Manifest i service worker używają adresów względnych, więc nie wymagają zmian.

**iPhone:** Web Push działa od iOS 16.4 i tylko w aplikacji dodanej do ekranu głównego. Zgodę trzeba wyrazić wewnątrz zainstalowanej aplikacji.

**Zapas:** jeśli Web Push na Twoim telefonie okaże się zawodny, ten sam `PushNotifier` może wysyłać przez samodzielnie hostowane ntfy (jeden dodatkowy kanał, ta sama treść).

## Eksport LaTeX i PDF

Plik .tex generuje serwer z Twojej formatki na podstawie JSON-a raportu. Claude nie pisze kodu LaTeX, więc kompilacja nie zależy od modelu.

1. Formatka `templates/report.tex.scriban` w repozytorium (szablon Scriban). Do niej trafiają: tytuł, data, podsumowanie, punkty pogrupowane według ważności, przypisy ze źródłami (tytuł, data, URL, cytowany fragment).
2. Każdy tekst z raportu przechodzi przez funkcję `latex_escape` (`\ { } $ & # ^ _ % ~`). Adresy URL przez `\url{}` z pakietu `hyperref`.
3. Kompilacja Tectonic (silnik XeTeX, polskie znaki bez dodatkowej konfiguracji) albo `latexmk -xelatex` z TeX Live:
   - zawsze bez shell-escape,
   - w katalogu tymczasowym, z limitem czasu 60 s,
   - jako ten sam nieuprzywilejowany użytkownik co usługa.
4. Zapis `.tex` i `.pdf` na dysku, ścieżki w `reports`. W aplikacji dwa przyciski: „Pobierz PDF” i „Pobierz .tex”.
5. Błąd kompilacji nie blokuje raportu ani powiadomienia: raport HTML jest dostępny od razu, a błąd trafia do logu i panelu.

**Otwarte:** potrzebuję Twojej „wcześniej ustalonej formatki”, żeby przenieść ją do szablonu. Tectonic przy pierwszym uruchomieniu pobiera pakiety z internetu; na serwerze trzeba to zrobić raz i zachować cache.

## Wyszukiwanie i serwer MCP

Wyszukiwanie pełnotekstowe w PostgreSQL wystarczy na start. Serwer MCP udostępnia to samo wyszukiwanie agentom AI.

**Wyszukiwanie pełnotekstowe:**

- Kolumna `search_vector` (tsvector, generowana) w `reports` i `report_findings`, indeks GIN.
- PostgreSQL nie ma w standardzie słownika języka polskiego. Dwie drogi:
  - słownik ispell dla polskiego (pliki z sjp.pl) wgrany do katalogu `tsearch_data` i własna konfiguracja `polish`; lepsze dopasowanie odmian,
  - konfiguracja `simple` + `unaccent` + `pg_trgm`; prostsze w utrzymaniu, słabsze przy odmianie.
- Filtry: zakres dat, ważność, kategoria, źródło. Wyniki z podświetleniem (`ts_headline`).

**Wyszukiwanie semantyczne (później, opcjonalnie):** pgvector i embeddingi punktów raportu. Anthropic nie oferuje modelu embeddingów, więc potrzebny byłby osobny dostawca albo model lokalny.

**Serwer MCP:**

- Oficjalne C# SDK MCP z transportem HTTP, hostowane w tym samym procesie pod `/mcp`.
- Uwierzytelnianie kluczem API z zakresem `mcp:read`.
- Narzędzia tylko do odczytu:
  - `search_reports(query, from?, to?, importance?, category?)`,
  - `get_report(id)` z punktami i cytatami,
  - `list_sources()` ze statusem i datą ostatniego sprawdzenia.
- Odpowiedzi narzędzi zawierają URL-e źródeł, żeby agent mógł je zacytować.

## Stack technologiczny

Twój dotychczasowy stack bez LDAP, Kerberos i `EnableWindowsTargeting`, plus biblioteki potrzebne do pobierania stron, raportów i push.

**Bez zmian:**

- .NET 10 (SDK w `global.json`, `rollForward: latestFeature`), C# latest, nullable i implicit usings.
- Jedno rozwiązanie `ZdrowieInfo.sln`, centralne zarządzanie pakietami (`Directory.Packages.props`), pakiety z linii 10.0.x.
- Wersja w `Directory.Build.props` i `AppVersion.Current`, test sprawdzający zgodność.
- ASP.NET Core + Kestrel na VPS z Ubuntu 24.04 (x64), usługa systemd (`Type=notify`), użytkownik nieuprzywilejowany, publikacja self-contained dla linux-x64.
- Plain HTTP za Twoim reverse proxy, z obsługą forwarded headers; adres publiczny z `ZdrowieInfo:PublicUrl`.
- PostgreSQL 16+ przez EF Core 10 i Npgsql, schemat tylko z migracji, flaga `--migrate-only`, `IDbContextFactory` w usługach tła.
- Biblioteka serwerowa podzielona na obszary z metodami `AddXxx()`, cienki host, biblioteka UI (Razor class library).
- `TimeProvider`, `ILogger`, klasy Options, `BackgroundService`, Data Protection z kluczami na dysku.
- Markdig (bez surowego HTML) do renderowania treści.
- Blazor Web App + MudBlazor 9 (tylko panel), cookie auth z politykami ról.
- Interfejs tylko po polsku, bez usługi tłumaczeń; kultura `pl-PL` dla dat i liczb, strefa `Europe/Warsaw`.

**Nowe elementy:**

| Obszar | Biblioteka lub narzędzie |
| --- | --- |
| Logowanie i MFA | ASP.NET Core Identity (EF Core), QRCoder |
| Harmonogram | Cronos |
| Pobieranie stron | `IHttpClientFactory`, Polly (przez `Microsoft.Extensions.Http.Resilience`) |
| Ekstrakcja HTML | AngleSharp |
| Porównywanie treści | DiffPlex |
| Strony z ochroną przed botami | Microsoft.Playwright (opcjonalnie) |
| Claude API | oficjalne SDK Anthropic dla C# albo `HttpClient` do Messages API |
| Push | WebPush (VAPID) |
| Szablon LaTeX | Scriban |
| Kompilacja PDF | Tectonic lub TeX Live (`latexmk`) jako program systemowy |
| MCP | oficjalne C# SDK MCP (transport HTTP dla ASP.NET Core) |

Przed dodaniem każdego pakietu sprawdź, czy wspiera `net10.0`, zgodnie z Twoją zasadą linii 10.0.x.

## Etapy realizacji

Siedem etapów; po etapie 3 aplikacja już działa i dostarcza raporty, reszta to rozbudowa.

1. **Szkielet.** `ZdrowieInfo.sln`, migracje, Identity z TOTP, sesja 30 dni, PWA (manifest, service worker), `PublicUrl`, usługa systemd. Gotowe, gdy zainstalujesz aplikację na telefonie i się zalogujesz.
2. **Źródła i wykrywanie zmian.** Tabela `sources` z 23 źródłami, selektory, harmonogram 5:00, stan bazowy, podgląd wykrytych wpisów w panelu. Gotowe, gdy przez tydzień wykrywa nowe wpisy bez fałszywych alarmów.
3. **Raporty dzienne z Claude.** `DailyReportService`, `ReportComposer`, raport „Brak zmian”, kryteria ważności, profil przychodni w panelu, walidacja cytatów, widok raportu, endpoint `POST /api/reports`. Kod teraz; klucz API i test po odebraniu kredytów (od 18.11.2026). Do testów bez klucza: implementacja `IReportGenerator` zwracająca przykładowy raport.
4. **Powiadomienia push.** VAPID z automatyczną wymianą po zmianie `PublicUrl`, subskrypcje, wysyłka po raporcie i przy incydentach źródeł, dziennik incydentów.
5. **Eksport LaTeX i PDF.** Formatka w Scriban (na później), kompilacja, pobieranie plików.
6. **Wyszukiwanie.** tsvector, wybór słownika, strona `/szukaj`.
7. **Serwer MCP.** Narzędzia tylko do odczytu, klucze z zakresem `mcp:read`.

Wyszukiwanie semantyczne (pgvector) zostaje poza planem, dopóki pełnotekstowe nie okaże się niewystarczające.

## Ryzyka i otwarte kwestie

Największe ryzyko to ciche awarie: strona zmieni układ, selektor przestanie działać, a aplikacja będzie milczeć. Dlatego źródło bez wpisów lub z błędem zawsze trafia do raportu technicznego.

| Ryzyko | Jak ograniczać |
| --- | --- |
| Zmiana struktury HTML strony | status *wymaga uwagi* przy 0 wpisów, selektory edytowalne w panelu |
| Blokada przez Incapsula (OW NFZ) | Playwright, rzadkie pobieranie, w ostateczności źródło ręczne |
| Zmyślone lub przekręcone cytaty | walidacja: cytat musi być dosłownym fragmentem zapisanej treści |
| Pominięcie ważnego wpisu przez Claude | lista `ignored` z powodem, widoczna w raporcie |
| Wyczerpanie kredytów API | licznik użycia w panelu, powiadomienie przy błędzie 4xx z API |
| Publicznie dostępny serwer | MFA, rate limiting, aktualizacje, kopie zapasowe PostgreSQL |
| Regulaminy stron | własny User-Agent, odstępy między żądaniami, sprawdzenie `robots.txt` |

**Otwarte kwestie:**

- [ ] Odebrać kredyty API w claude.ai (Settings → Billing, od 18.11.2026), utworzyć workspace z limitem i klucz API w Claude Console.
- [ ] Uzupełnić w panelu listę systemów używanych w przychodni.
- [ ] Przekazać formatkę LaTeX do przeniesienia w Scriban (na później).
- [ ] Wybrać słownik wyszukiwania: ispell dla polskiego czy `simple` + `pg_trgm` (na później).
- [ ] Sprawdzić w praktyce, czy mazowiecki OW NFZ da się pobierać automatycznie.
