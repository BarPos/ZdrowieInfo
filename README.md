# ZdrowieInfo

Aplikacja codziennie sprawdza publiczne strony NFZ, CeZ, MZ i CSIRT CeZ, wykrywa nowe wpisy i przygotowuje raport istotnych zmian (Claude API). Raport jest dostępny w aplikacji webowej instalowanej na telefonie (PWA), z powiadomieniem push.

- Zakres i architektura: [Plan aplikacji ZdrowieInfo.md](Plan%20aplikacji%20ZdrowieInfo.md)
- Kolejność prac i stan etapów: [Plan implementacji ZdrowieInfo.md](Plan%20implementacji%20ZdrowieInfo.md)
- Decyzje techniczne: [docs/decyzje.md](docs/decyzje.md)

**Stan:** etap 1 (szkielet) — logowanie z hasłem, obowiązkowym TOTP i opcjonalnym passkey, sesja 30 dni, PWA, panel, instalator. Źródła i raporty: etapy 2–3.

## Układ repozytorium

| Ścieżka | |
| --- | --- |
| `src/ZdrowieInfo.Server` | cienki host: `Program.cs`, polecenia CLI, migracje EF Core, service worker, manifest i ikony PWA |
| `src/ZdrowieInfo.Server.Core` | logika w obszarach (`Common`, `Data`, `Accounts`, `Settings`), każdy z `AddXxx()` |
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

Aplikacja: <http://localhost:5180>. Przy pierwszym logowaniu poprosi o skonfigurowanie aplikacji uwierzytelniającej. Dane lokalne (klucze Data Protection) trafiają do `.dev-data/` (poza gitem).

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
| `--create-user <login>` | tworzy konto; hasło (min. 12 znaków) z terminala albo ze standardowego wejścia |
| `--set-password <login>` | nowe hasło, odblokowanie konta; zalogowane urządzenia wylogowane w ciągu 5 minut |
| `--reset-mfa <login>` | awaryjnie: wyłącza TOTP, usuwa passkey, odblokowuje konto; zalogowane urządzenia wylogowane w ciągu 5 minut |

Rejestracja w aplikacji jest wyłączona.

## Zmiana domeny

Ciasteczka, zainstalowana PWA, subskrypcje push i passkey są związane z domeną. Po zmianie `ZdrowieInfo__PublicUrl`: zainstaluj aplikację z nowego adresu, zaloguj się, dodaj passkey i (od etapu 4) włącz powiadomienia ponownie.

## iPhone

Zainstalowana PWA (Udostępnij → Do ekranu początkowego) ma osobne ciasteczka niż Safari — logujesz się w niej osobno, potem sesja trwa 30 dni. Powiadomienia push działają od iOS 16.4 i tylko w zainstalowanej aplikacji.
