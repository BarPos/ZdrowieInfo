# ZdrowieInfo

Aplikacja codziennie sprawdza publiczne strony NFZ, CeZ, MZ i CSIRT CeZ, wykrywa nowe wpisy i przygotowuje raport istotnych zmian (Claude API). Raport jest dostępny w aplikacji webowej instalowanej na telefonie (PWA), z powiadomieniem push.

- Zakres i architektura: [Plan aplikacji ZdrowieInfo.md](Plan%20aplikacji%20ZdrowieInfo.md)
- Kolejność prac i stan etapów: [Plan implementacji ZdrowieInfo.md](Plan%20implementacji%20ZdrowieInfo.md)
- Decyzje techniczne: [docs/decyzje.md](docs/decyzje.md)

**Stan:** etapy 1–7 (kod): logowanie z TOTP i passkey, PWA, instalator; 23 źródła z wykrywaniem zmian, incydentami i harmonogramem 5:00; raport z Claude rano od 5:30 za poprzedni dzień (walidacja cytatów, ponowienia do 6:20, raport awaryjny), API dla zewnętrznych producentów raportów; powiadomienia push i dziennik incydentów; PDF z LaTeX-a; wyszukiwanie; serwer MCP; role administrator/użytkownik, raporty wstecz i okresowe, publiczne linki do PDF; instalator, wydania na GitHubie i aktualizacja z panelu. Do sprawdzenia na żywo: Claude API (od 18.11.2026) i push na iPhonie.

## Układ repozytorium

| Ścieżka | |
| --- | --- |
| `src/ZdrowieInfo.Server` | cienki host: `Program.cs`, polecenia CLI, migracje EF Core, service worker, manifest i ikony PWA |
| `src/ZdrowieInfo.Server.Core` | logika w obszarach (`Common`, `Data`, `Accounts`, `Sources`, `Reports`, `Export`, `Push`, `Search`, `Mcp`, …), każdy z `AddXxx()` |
| `src/ZdrowieInfo.Web` | Razor class library: strony czytelnika i kont (static SSR), panel (InteractiveServer + MudBlazor) |
| `tests/ZdrowieInfo.Server.Tests` | xUnit v3; testy integracyjne na PostgreSQL w Testcontainers |
| `deploy/linux` | `install.sh`, `update.sh`, `backup.sh`, jednostki systemd, testy skryptów |
| `deploy/dev` | PostgreSQL do pracy lokalnej |
| `build/build-server.sh` | paczki wydania (linux-x64, linux-arm64), `install.sh` i `SHA256SUMS.txt` |
| `.github/workflows` | `build` (testy, paczki) i `release` (wydanie GitHub) – uruchamiane ręcznie |

## Praca lokalna

Wymagania: .NET SDK 10, Docker.

```bash
docker compose -f deploy/dev/docker-compose.yml up -d
```

```bash
cd src/ZdrowieInfo.Server && dotnet run -- --create-user admin
```

```bash
cd src/ZdrowieInfo.Server && dotnet run
```

Aplikacja: <http://localhost:5180>. Konto z `--create-user` to administrator. Harmonogram źródeł jest lokalnie wyłączony (żeby każde uruchomienie nie odpytywało stron) — źródła sprawdzisz poleceniem `dotnet run -- --check-now` albo w panelu. Przy pierwszym logowaniu poprosi o skonfigurowanie aplikacji uwierzytelniającej. Dane lokalne (klucze Data Protection) trafiają do `.dev-data/` (poza gitem).

Testy (wymagają działającego Dockera):

```bash
dotnet test tests/ZdrowieInfo.Server.Tests
```

Nowa migracja:

```bash
dotnet ef migrations add <Nazwa> --project src/ZdrowieInfo.Server --output-dir Data/Migrations
```

## Wdrożenie (Ubuntu 24.04+, x64 albo arm64)

Instalacja, aktualizacje i kopie działają jak w DomainNotice: wydania na GitHubie (paczki dla x64 i arm64, `install.sh`, `SHA256SUMS.txt`), instalator na serwerze, aktualizacja z panelu przez `update.sh` (root) z kopią bazy i wycofaniem. Bez Dockera i bez .NET na serwerze (program jest self-contained). Szczegóły: [docs/decyzje.md](docs/decyzje.md), D32.

### Instalacja

Z wydania (repozytorium wydań jest wpisane w `install.sh`):

```bash
curl -fsSLO https://github.com/BarPos/ZdrowieInfo/releases/latest/download/install.sh
```

```bash
sudo bash install.sh
```

Instalator pyta o publiczny adres (`https://…`), bazę (PostgreSQL na tym serwerze – instalowany, gdy go nie ma; istniejący jest współdzielony: ZdrowieInfo dodaje tylko własną rolę i bazę – albo PostgreSQL gdzie indziej) i pierwsze konto (administrator). Pobiera paczkę dla tego procesora, sprawdza ją z `SHA256SUMS.txt`, instaluje pakiety (ICU, klient PostgreSQL, TeX do PDF), tworzy użytkownika `zdrowieinfo`, stosuje migracje, uruchamia usługę i codzienną kopię zapasową. Inne źródło paczki: `--version X.Y.Z` albo `--package ZdrowieInfo.Server-<wersja>-linux-<arch>.tar.gz` (bez internetu; sprawdzana z `SHA256SUMS.txt` obok).

Bez pytań (np. z Ansible):

```bash
sudo ZDROWIEINFO_ADMIN_PASSWORD='…' bash install.sh --unattended --db=local --public-url https://zdrowieinfo.example.pl --admin-user admin
```

Wszystkie opcje: `bash install.sh --help` (m.in. `--port`, `--listen` i `--trusted-proxy` dla reverse proxy na innej maszynie, `--contact-email`, `--no-pdf`, `--db=existing` z `--db-host`…). Hasła nigdy nie są argumentami: `--secrets-file`, `--secrets-stdin`, zmienne `ZDROWIEINFO_ADMIN_PASSWORD` / `ZDROWIEINFO_DB_PASSWORD` albo pytanie.

Adres nasłuchu można później zmienić w `/opt/zdrowieinfo/config/zdrowieinfo.env` (`ASPNETCORE_URLS`, potem `systemctl restart zdrowieinfo`); aktualizacje sprawdzają `/healthz` pod tym adresem.

Reverse proxy (po Twojej stronie): HTTPS → `http://127.0.0.1:5180`, z nagłówkami `Host`, `X-Forwarded-For`, `X-Forwarded-Proto` i obsługą WebSocket (panel to Blazor Server).

**PostgreSQL używany przez inne aplikacje** (`--db=local`, gdy PostgreSQL już jest): instalator tylko dodaje nową rolę i bazę (`zdrowieinfo`, a gdy ta nazwa jest zajęta – `zdrowieinfo_2`…; istniejąca rola czy baza nigdy nie jest zmieniana ani przejmowana). Rola nie ma uprawnień superużytkownika ani tworzenia baz i ról, a do jej bazy łączy się tylko ona. PostgreSQL nie jest instalowany, aktualizowany, konfigurowany (`postgresql.conf`, `pg_hba.conf`) ani restartowany, a zainstalowane pakiety nie są aktualizowane; klaster starszy niż 16 – odmowa. Przy kilku klastrach wybierz jeden przez `--db-port`. Nieudana instalacja usuwa tylko utworzone przez siebie rolę i bazę; kopia, przywracanie, wycofanie aktualizacji i odinstalowanie działają wyłącznie na bazie ZdrowieInfo.

Serwer zainstalowany poprzednim skryptem (`/etc/zdrowieinfo`, `/var/lib/zdrowieinfo`) nie jest przenoszony automatycznie – instalator zatrzymuje się z komunikatem.

### Aktualizacje

**Panel → Aktualizacje**: działająca wersja, najnowsze wydanie z opisem, *Sprawdź teraz* i *Aktualizuj do …* (serwer sprawdza wydania co 24 godziny). Serwer pobiera paczkę, sprawdza SHA-256 i zostawia zlecenie; `update.sh` (jako root, przez `zdrowieinfo-update.path`) zatrzymuje usługę, robi kopię bazy, stosuje migracje i uruchamia nową wersję. Gdy `/healthz` nie poda nowej wersji w ciągu 2 minut – przywraca bazę i poprzednią wersję. Zostają 3 ostatnie wersje. W trakcie przygotowywania raportu albo sprawdzania źródeł aktualizacja jest odrzucana z komunikatem – spróbuj, gdy się skończą.

Z wiersza poleceń (to samo przez `update.sh`):

```bash
sudo bash install.sh --upgrade --version X.Y.Z
```

`update.sh`, `backup.sh` i jednostki systemd (uruchamiane jako root) nie są wymieniane aktualizacją z panelu – tylko przez `install.sh --upgrade` albo `install.sh --repair --version X.Y.Z` z paczki podanej przez administratora.

### Konserwacja

Ponowne `sudo bash install.sh` na zainstalowanym serwerze pokazuje menu; każda pozycja ma też opcję:

| Polecenie | |
| --- | --- |
| `install.sh --repair` | właściciele, prawa i usługi; konfiguracja i dane zostają |
| `install.sh --backup [--output ŚCIEŻKA]` | kopia: baza, konfiguracja, klucze, dane, program (codziennie o 3:30 automatycznie, 14 ostatnich) |
| `install.sh --restore PLIK` | przywraca kopię (także na nowym serwerze z zainstalowanym ZdrowieInfo; najpierw kopia bieżącego stanu) |
| `install.sh --set-password [LOGIN]` | nowe hasło konta, odblokowanie |
| `install.sh --reset-mfa LOGIN` | usuwa aplikację uwierzytelniającą i passkey konta (konfiguracja od nowa przy logowaniu) |
| `install.sh --uninstall [--keep-data]` | usuwa wszystko, także bazę (proponuje kopię, pyta o potwierdzenie); `--keep-data` – tylko program |

| Ścieżka na serwerze | |
| --- | --- |
| `/opt/zdrowieinfo/releases/<wersja>/` | program (root, tylko do odczytu dla usługi); `current` – dowiązanie do działającej wersji |
| `/opt/zdrowieinfo/config/zdrowieinfo.env` | konfiguracja: adres, port, baza, repozytorium wydań (root, 600; czyta systemd) |
| `/opt/zdrowieinfo/config/update.env` | ustawienia bazy dla `update.sh` i `backup.sh` (root, 600) |
| `/opt/zdrowieinfo/data/` | dane usługi: klucze Data Protection (bez nich zaszyfrowany klucz Claude API i VAPID są nie do odczytania), raporty PDF, pobrane aktualizacje |
| `/opt/zdrowieinfo/backups/` | kopie z `backup.sh`, w `pre-update/` zrzuty sprzed aktualizacji (tylko root) |
| `/opt/zdrowieinfo/bin/` | `update.sh`, `backup.sh` (root) |

Log: `journalctl -u zdrowieinfo -f`; aktualizacje: `journalctl -u zdrowieinfo-update`.

### Wydania (publikowanie)

Jedna wersja dla całości: `<Version>` w `Directory.Build.props` i `AppVersion.Current` (test sprawdza zgodność).

1. Podnieś obie, zatwierdź i wypchnij na GitHub.
2. **Actions → release → Run workflow** (opcjonalnie wydanie próbne albo szkic).
3. Workflow sprawdza, że tagu `v<wersja>` jeszcze nie ma, uruchamia **build** (testy, skrypty, paczki x64 i arm64), liczy `SHA256SUMS.txt` i tworzy tag oraz wydanie z plikami.

Repozytorium wydań: [BarPos/ZdrowieInfo](https://github.com/BarPos/ZdrowieInfo) – workflow wpisuje do `install.sh` repozytorium, w którym działa. Lokalnie: `build/build-server.sh [--arch x64|arm64] [--repository właściciel/nazwa]` → `artifacts/` (domyślnie BarPos/ZdrowieInfo; `--repository ""` – paczki bez GitHuba, np. do testów w kontenerze). Pobieranie `install.sh` bez logowania i sprawdzanie aktualizacji bez tokenu wymagają publicznego repozytorium (prywatne: `ZdrowieInfo__Updates__GitHubToken` w `zdrowieinfo.env`).

Testy skryptów: `deploy/linux/update.sh --self-test`, `deploy/linux/tests/update-functions-test.sh`, a całość w kontenerze Ubuntu (paczki z `--repository ""`): `deploy/linux/tests/container-smoke.sh KATALOG_PACZEK KATALOG_NOWSZEJ_WERSJI` oraz współdzielony PostgreSQL: `deploy/linux/tests/container-sharedpg-smoke.sh KATALOG_PACZEK`.

## Polecenia serwera

Program serwera ma też polecenia (jako użytkownik usługi, z jej konfiguracją – najprościej przez funkcję z `update.sh`):

```bash
sudo bash -c 'source /opt/zdrowieinfo/bin/update.sh && zi_run_as_service_user /opt/zdrowieinfo/current --check-sources'
```

| Polecenie | |
| --- | --- |
| `--migrate-only` | stosuje migracje i kończy (aplikacja robi to też przy starcie) |
| `--create-user <login> [--role uzytkownik]` | tworzy konto (domyślnie administratora); hasło (min. 12 znaków) z terminala albo ze standardowego wejścia |
| `--set-password <login>` | nowe hasło, odblokowanie konta; zalogowane urządzenia wylogowane w ciągu 5 minut |
| `--reset-mfa <login>` | awaryjnie: wyłącza TOTP, usuwa passkey, odblokowuje konto; zalogowane urządzenia wylogowane w ciągu 5 minut |
| `--check-sources [slug]` | próba selektorów na żywych stronach, bez zapisu |
| `--check-now [slug]` | sprawdza źródła teraz i zapisuje wynik (jak harmonogram) |
| `--generate-report [rrrr-mm-dd] [--replace]`, `--generate-report rrrr-mm-dd--rrrr-mm-dd` | raport dnia teraz (domyślnie dziś – w trakcie dnia, następnego ranka harmonogram zastąpi go pełnym), raport wstecz albo okresowy od–do (brakujące wpisy pobiera ze stron) |
| `--export-report [rrrr-mm-dd albo rrrr-mm-dd--rrrr-mm-dd]` | generuje PDF raportu (domyślnie dziś) |
| `--mark-reported-before rrrr-mm-dd` | przy włączaniu raportów: starsze wpisy nie trafią do pierwszego raportu |
| `--apply-default-sources [slug]` | przywraca adresy i selektory z listy domyślnej (np. po aktualizacji aplikacji); zmienione źródła dostają nowy stan bazowy |

Rejestracja w aplikacji jest wyłączona – konta zakłada administrator (**Panel → Użytkownicy**) albo polecenie serwera.

## Konta i role

| Rola | Dostęp |
| --- | --- |
| administrator | wszystko: panel (źródła, wpisy, raporty, ustawienia, klucze API, użytkownicy), incydenty, PDF, publiczne linki, usuwanie raportów |
| użytkownik | czytanie i wyszukiwanie raportów, własne konto (hasło, weryfikacja dwuetapowa, passkey, powiadomienia o raportach) |

Weryfikacja dwuetapowa jest obowiązkowa dla wszystkich. **Panel → Użytkownicy → Usuń 2FA** (np. po zgubieniu telefonu) usuwa aplikację uwierzytelniającą i passkey – przy następnym logowaniu użytkownik skonfiguruje je od nowa.

## Claude API i raporty

Klucz Claude API wpisuje się w panelu (**Panel → Ustawienia**); jest zapisywany zaszyfrowany w bazie. Tam też model (domyślnie `claude-sonnet-5-5`), effort, profil przychodni (zakresy świadczeń, KSC, lista używanych systemów) i zużycie w miesiącu. Lokalnie (`Development`) raporty przygotowuje generator przykładowy bez Claude (`ZdrowieInfo:Reports:Generator = Sample`).

**Sprawdzanie podatności w internecie** (Panel → Ustawienia, domyślnie wyłączone): gdy wśród nowych wpisów jest podatność (CVE albo słowo „podatność”), przed raportem osobne wywołanie Claude z narzędziem web search sprawdza podatne wersje, poprawki i czy podatność jest wykorzystywana. Wynik ze źródłami jest osobną sekcją raportu (strona i PDF), a analiza może z niego korzystać przy ocenie ważności – cytaty nadal tylko z monitorowanych wpisów. Do wyszukiwarki nie trafia lista systemów przychodni. Koszt: $10 za 1000 wyszukiwań plus tokeny (limit wyszukiwań na raport i opcjonalna lista domen w ustawieniach). Błąd sprawdzania nie zatrzymuje raportu – trafia do jego uwag. Wyszukiwanie musi być włączone dla organizacji w Claude Console.

Programy i agenci AI mogą dodawać raporty kluczem z zakresem `reports:write` (**Panel → Klucze API**):

```bash
curl -H "Authorization: Bearer mon_…" https://zdrowieinfo.example.pl/api/items/pending
```

```bash
curl -X POST -H "Authorization: Bearer mon_…" -H "Content-Type: application/json" --data @raport.json "https://zdrowieinfo.example.pl/api/reports?date=2026-11-18"
```

Każdy raport ma też wersję PDF (XeLaTeX z szablonu; dla administratora przycisk „Pobierz PDF” na stronie raportu, adres `/eksport/raport-rrrr-mm-dd.pdf`). Lokalnie potrzebny TeX Live (`xelatex`, `latexmk`); na serwerze instaluje go `install.sh`.

Administrator może udostępnić PDF raportu publicznym linkiem (`/udostepnione/<losowy token>`, bez logowania): na dole strony raportu **Udostępnij…** – tam też nowy losowy link (stary przestaje działać), wycofanie i usunięcie raportu.

**Panel → Raporty**: raport dzienny za wcześniejszy dzień albo raport okresowy od–do (do 92 dni), z wpisów opublikowanych w tych dniach. Gdy znane wpisy źródła nie sięgają tak daleko, aplikacja przegląda starsze strony list i pobiera treść wpisów (może to potrwać kilka minut; zlecenia działają w tle). Raporty wstecz nie wysyłają powiadomień. *Odśwież PDF* przy raporcie składa sam PDF od nowa z zapisanej treści (np. po aktualizacji, która zmienia wygląd PDF) – bez wywołania Claude.

**Panel → Wpisy własne**: komunikaty spoza monitorowanych stron (np. jawne ostrzeżenia CSIRT MON z poczty) – źródło, tytuł, data i treść w edytorze, do którego można wkleić komunikat z tabelami i listami (obrazy, style i skrypty są usuwane, HTML przebudowywany z listy dozwolonych znaczników). Wpis trafia do najbliższego raportu dziennego obejmującego jego datę, jak wpisy ze stron; w raporcie ma źródło „… (wpis własny)” i link do strony wpisu (dla każdego zalogowanego). Edycja i usuwanie – do czasu, aż wpis trafi do raportu; wpis z raportu można *przywrócić jako niezaraportowany* (wraca do najbliższego raportu dziennego, stary raport zostaje bez zmian). Lista wpisów ma wyszukiwanie w tytule i źródle, filtr źródła i stanu oraz stronicowanie. Treść jest wysyłana do Claude API – wklejaj tylko komunikaty jawne.

Format raportu (`title`, `summary`, `findings[]` z `importance`, `category`, `headline`, `body`, `action`, `citations[]` z `item_id` i `quote`, `ignored[]`) — [docs/decyzje.md](docs/decyzje.md), D16–D19.

## Powiadomienia i wyszukiwanie

Powiadomienia włącza się osobno na każdym urządzeniu: **Konto → Powiadomienia** (tam też powiadomienie testowe). Przychodzą, gdy raport jest gotowy; administratorzy dostają też powiadomienia, gdy źródło przestaje być dostępne i gdy dostęp wraca (historia w **Incydenty**). Wszystkie źródła są domyślnie sprawdzane codziennie o 5:00.

**Szukaj** przeszukuje punkty i podsumowania raportów: wielkość liter i polskie znaki bez znaczenia, fragment słowa łapie odmiany („przerw” → przerwa, przerwy), `"fraza"`, `or`, `-słowo`; filtry dat, ważności, kategorii i źródła.

## Serwer MCP (agenci AI)

Agent AI (np. Claude Code) może przeszukiwać raporty przez MCP: utwórz klucz z zakresem `mcp:read` (**Panel → Klucze API**) i dodaj serwer:

```bash
claude mcp add --transport http zdrowieinfo https://zdrowieinfo.example.pl/mcp --header "Authorization: Bearer mon_…"
```

Narzędzia (tylko odczyt): `search_reports` (jak strona Szukaj), `get_report` (cały raport dnia z cytatami i adresami źródeł; bez daty najnowszy), `list_sources` (źródła i ich stan). Limit 60 zapytań na minutę na klucz.

## Zmiana domeny

Ciasteczka, zainstalowana PWA, subskrypcje push i passkey są związane z domeną. Po zmianie `ZdrowieInfo__PublicUrl`: zainstaluj aplikację z nowego adresu, zaloguj się, dodaj passkey i włącz powiadomienia ponownie (stare subskrypcje aplikacja usuwa przy starcie).

## iPhone

Zainstalowana PWA (Udostępnij → Do ekranu początkowego) ma osobne ciasteczka niż Safari — logujesz się w niej osobno, potem sesja trwa 30 dni. Powiadomienia push działają od iOS 16.4 i tylko w zainstalowanej aplikacji.
