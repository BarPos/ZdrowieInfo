# ZdrowieInfo

Aplikacja codziennie sprawdza publiczne strony NFZ, CeZ, MZ i CSIRT CeZ, wykrywa nowe wpisy i przygotowuje raport istotnych zmian (Claude API). Raport jest dostępny w aplikacji webowej instalowanej na telefonie (PWA), z powiadomieniem push.

- Zakres i architektura: [Plan aplikacji ZdrowieInfo.md](Plan%20aplikacji%20ZdrowieInfo.md)
- Kolejność prac i stan etapów: [Plan implementacji ZdrowieInfo.md](Plan%20implementacji%20ZdrowieInfo.md)
- Decyzje techniczne: [docs/decyzje.md](docs/decyzje.md)

**Stan:** etapy 1–7 (kod): logowanie z TOTP i passkey, PWA, instalator; 23 źródła z wykrywaniem zmian, incydentami i harmonogramem 5:00; raport dzienny z Claude od 5:30 (walidacja cytatów, ponowienia do 6:20, raport awaryjny), API dla zewnętrznych producentów raportów; powiadomienia push i dziennik incydentów; PDF z LaTeX-a; wyszukiwanie; serwer MCP; role administrator/użytkownik, raporty wstecz i okresowe, publiczne linki do PDF. Do sprawdzenia na żywo: Claude API (od 18.11.2026) i push na iPhonie.

## Układ repozytorium

| Ścieżka | |
| --- | --- |
| `src/ZdrowieInfo.Server` | cienki host: `Program.cs`, polecenia CLI, migracje EF Core, service worker, manifest i ikony PWA |
| `src/ZdrowieInfo.Server.Core` | logika w obszarach (`Common`, `Data`, `Accounts`, `Sources`, `Reports`, `Export`, `Push`, `Search`, `Mcp`, …), każdy z `AddXxx()` |
| `src/ZdrowieInfo.Web` | Razor class library: strony czytelnika i kont (static SSR), panel (InteractiveServer + MudBlazor) |
| `tests/ZdrowieInfo.Server.Tests` | xUnit v3; testy integracyjne na PostgreSQL w Testcontainers |
| `deploy/linux` | jednostka systemd, `install.sh`, kopie zapasowe |
| `deploy/dev` | PostgreSQL do pracy lokalnej |
| `build/build-server.sh` | paczka self-contained linux-x64 |

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

## Wdrożenie (Ubuntu 24.04, x64)

1. Zbuduj paczkę: `./build/build-server.sh` → `artifacts/zdrowieinfo-<wersja>-linux-x64.tar.gz` (uruchamia też testy).
2. Na serwerze rozpakuj i uruchom:

   ```bash
   sudo ./install.sh --public-url https://zdrowieinfo.example.pl
   ```

   Instalator: instaluje PostgreSQL, tworzy użytkownika systemowego `zdrowieinfo`, bazę z losowym hasłem, `/etc/zdrowieinfo/zdrowieinfo.env`, stosuje migracje, uruchamia usługę i codzienną kopię zapasową, na końcu pyta o login pierwszego konta.
3. Reverse proxy (po Twojej stronie): HTTPS → `http://127.0.0.1:5180`, z nagłówkami `Host`, `X-Forwarded-For`, `X-Forwarded-Proto`. Proxy na innej maszynie: ustaw `ZdrowieInfo__TrustedProxies__0` i `ASPNETCORE_URLS` w pliku env.

Aktualizacja: rozpakuj nową paczkę i uruchom `sudo ./install.sh` bez argumentów.

| Ścieżka na serwerze | |
| --- | --- |
| `/opt/zdrowieinfo` | aplikacja (tylko do odczytu dla usługi) |
| `/etc/zdrowieinfo/zdrowieinfo.env` | konfiguracja i hasło bazy (root, 600) |
| `/var/lib/zdrowieinfo/keys` | klucze Data Protection — bez nich restart wylogowuje, a zaszyfrowane ustawienia (klucz Claude API, VAPID) są nie do odczytania |
| `/var/lib/zdrowieinfo/backups` | codzienne kopie bazy i kluczy (14 ostatnich) |

## Polecenia serwera

Uruchamiane z katalogu aplikacji, jako użytkownik usługi i z jej konfiguracją, np.:

```bash
cd /opt/zdrowieinfo && sudo -u zdrowieinfo env $(sudo grep -v '^#' /etc/zdrowieinfo/zdrowieinfo.env | xargs) ./ZdrowieInfo.Server --reset-mfa admin
```

| Polecenie | |
| --- | --- |
| `--migrate-only` | stosuje migracje i kończy (aplikacja robi to też przy starcie) |
| `--create-user <login> [--role uzytkownik]` | tworzy konto (domyślnie administratora); hasło (min. 12 znaków) z terminala albo ze standardowego wejścia |
| `--set-password <login>` | nowe hasło, odblokowanie konta; zalogowane urządzenia wylogowane w ciągu 5 minut |
| `--reset-mfa <login>` | awaryjnie: wyłącza TOTP, usuwa passkey, odblokowuje konto; zalogowane urządzenia wylogowane w ciągu 5 minut |
| `--check-sources [slug]` | próba selektorów na żywych stronach, bez zapisu |
| `--check-now [slug]` | sprawdza źródła teraz i zapisuje wynik (jak harmonogram) |
| `--generate-report [rrrr-mm-dd] [--replace]`, `--generate-report rrrr-mm-dd--rrrr-mm-dd` | raport dnia teraz (domyślnie dziś), raport wstecz albo okresowy od–do (brakujące wpisy pobiera ze stron) |
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

Programy i agenci AI mogą dodawać raporty kluczem z zakresem `reports:write` (**Panel → Klucze API**):

```bash
curl -H "Authorization: Bearer mon_…" https://zdrowieinfo.example.pl/api/items/pending
```

```bash
curl -X POST -H "Authorization: Bearer mon_…" -H "Content-Type: application/json" --data @raport.json "https://zdrowieinfo.example.pl/api/reports?date=2026-11-18"
```

Każdy raport ma też wersję PDF (XeLaTeX z szablonu; dla administratora przycisk „Pobierz PDF” na stronie raportu, adres `/eksport/raport-rrrr-mm-dd.pdf`). Lokalnie potrzebny TeX Live (`xelatex`, `latexmk`); na serwerze instaluje go `install.sh`.

Administrator może udostępnić PDF raportu publicznym linkiem (`/udostepnione/<losowy token>`, bez logowania): na dole strony raportu **Udostępnij…** – tam też nowy losowy link (stary przestaje działać), wycofanie i usunięcie raportu.

**Panel → Raporty**: raport dzienny za wcześniejszy dzień albo raport okresowy od–do (do 92 dni), z wpisów opublikowanych w tych dniach. Gdy znane wpisy źródła nie sięgają tak daleko, aplikacja przegląda starsze strony list i pobiera treść wpisów (może to potrwać kilka minut; zlecenia działają w tle). Raporty wstecz nie wysyłają powiadomień.

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
