# Plan implementacji ZdrowieInfo

17.11.2026 · na podstawie „Plan aplikacji ZdrowieInfo.md”

Ten dokument mówi **jak i w jakiej kolejności** zbudować to, co opisuje plan aplikacji. Nie powtarza zakresu ani uzasadnień — odsyła do sekcji planu. Stack bierzemy z sekcji „Stack technologiczny” planu aplikacji; wersje pakietów każdorazowo weryfikujemy (linia 10.0.x, wsparcie `net10.0`).

## 1. Rozstrzygnięcia przed startem

Plan aplikacji ma kilka miejsc, które trzeba ustalić, zanim powstanie kod. Poniżej moja propozycja domyślna; do zmiany, jeśli wolisz inaczej.

| # | Kwestia | Propozycja |
| --- | --- | --- |
| R1 | Adres raportu: plan używa i `/raporty/{id}`, i `/raporty/{data}` | **`/raporty/{data}`** (`yyyy-MM-dd`). Raport jest jeden na dzień (`reports.data` unikalna), adres jest czytelny i stały, ten sam w push, service workerze i MCP. `get_report` w MCP przyjmuje datę. |
| R2 | Klucze API są potrzebne w etapie 3 (`POST /api/reports`), a w planie pojawiają się dopiero przy MCP | Tabela `api_keys`, `ApiKeyAuthenticationHandler`, polityki zakresów, rate limiting i panel kluczy przechodzą do **etapu 3**. Etap 7 dodaje tylko zakres `mcp:read`. |
| R3 | Incydenty są potrzebne w raporcie („Stan źródeł”) już w etapie 3, a opisane przy push (etap 4) | Wykrywanie incydentów, tabela `source_incidents`, ponowienia i godzinne sprawdzanie otwartych incydentów — **etap 2**. Etap 4 dokłada tylko wysyłkę push i stronę `/incydenty`. |
| R4 | Stan bazowy: źródła dwumiesięczne nie dostałyby go aż do 1.01.2027 | Stan bazowy robiony **od razu dla każdego źródła bez stanu bazowego** (pierwsze uruchomienie i każde dodane źródło), niezależnie od grupy. Kolumna `sources.baseline_at`. |
| R5 | Które wpisy trafiają do raportu dziennego | Nie „z dzisiejszych uruchomień”, tylko **wszystkie niezaraportowane**: `source_items.report_id IS NULL AND is_baseline = false`. Wpis wykryty np. o 9:00 przy godzinnym sprawdzaniu po incydencie trafi do raportu następnego dnia, nic nie ginie. |
| R6 | `POST /api/reports`, gdy raport na ten dzień już istnieje | `409 Conflict`; nadpisanie tylko z `?replace=true`. |
| R7 | Restart serwera w trakcie okna 5:00–6:30 | Harmonogram liczy zaległe zadania ze stanu w bazie (nie z pamięci) i nadrabia je po starcie. |
| R8 | Serwer wyłączony 1. dnia miesiąca albo w poniedziałek — grupa przepadłaby na tydzień, miesiąc lub dwa | Źródło jest należne, gdy jego **ostatnie udane sprawdzenie jest starsze niż ostatni planowy termin jego grupy** (a nie „brak `check_runs` na dziś”). Po przerwie zaległe grupy są sprawdzane przy najbliższym uruchomieniu. |
| R9 | Zaległość z tygodnia obserwacji (etap 2) — pierwszy prawdziwy raport objąłby wszystkie wpisy z tego czasu | Przy włączeniu raportów (etap 3) jednorazowe polecenie `--mark-reported-before <data>` oznacza starsze wpisy jako zaraportowane. Domyślnie: odcięcie na dzień przed pierwszym raportem; alternatywnie jeden duży raport startowy, jeśli wolisz. |
| R10 | `POST /api/reports` z walidacją cytatów jak dla Claude — zewnętrzny program nie zna `item_id` ani treści wpisów | Dodatkowo `GET /api/items/pending` (zakres `reports:write`): niezaraportowane wpisy z `item_id`, URL, datą i treścią. Producent, który go nie używa, może podać cytat z samym `url` zamiast `item_id` — wtedy cytat jest oznaczony jako niezweryfikowany, a raport dostaje status *do weryfikacji*. |
| R11 | Odpowiedź `304 Not Modified` | Udane sprawdzenie bez zmian (nie „0 wpisów”, nie incydent). |
| R12 | Struktura projektów i stack testów — sekcja „Stack technologiczny” planu ich nie określa | **Założenie do potwierdzenia:** nazwy projektów (`ZdrowieInfo.Server`, `.Server.Core`, `.Web`, `.Server.Tests`), układ `src/` + `tests/` oraz testy na xUnit v3 + Testcontainers PostgreSQL + `Microsoft.AspNetCore.Mvc.Testing` wzięte z Twojego wcześniejszego projektu. Jeśli w tym projekcie stack różni się także w tych punktach, poprawię sekcje 2 i 5. |

**Dodatkowe kolumny** względem modelu danych w planie (bez nowych tabel, nadal 11 + Identity):

- `sources`: `baseline_at`, `last_checked_at`, `etag`, `last_modified`, `fetch_mode` (http / playwright / ręczne), `notes`;
- `source_items`: `is_baseline`, `content_hash` (etap 2) oraz `report_id` — w którym raporcie uwzględniony, także jako `ignored` (migracja etapu 3, bo wtedy powstaje tabela `reports`);
- `source_snapshots`: `error_type`, `items_found`, `duration_ms`, `content` (tylko dla metody *cała strona* — potrzebne do diffu);
- `reports`: `model`, `ai_usage` (jsonb: tokeny wejścia/wyjścia/cache, koszt szacunkowy), `ignored` (jsonb), `generation_attempts`, `error`.

## 2. Struktura rozwiązania

```
ZdrowieInfo/
├── global.json                      # SDK 10.0.x, rollForward: latestFeature
├── Directory.Build.props            # wersja, nullable, implicit usings, LangVersion latest
├── Directory.Packages.props         # centralne wersje pakietów
├── ZdrowieInfo.sln
├── src/
│   ├── ZdrowieInfo.Server/          # cienki host: Program.cs, CLI, appsettings, migracje EF
│   ├── ZdrowieInfo.Server.Core/     # logika w obszarach, każdy z AddXxx()
│   │   ├── Common/                  # AppVersion, TimeProvider/strefa, Options, Data Protection
│   │   ├── Data/                    # ZdrowieInfoDbContext, encje, konfiguracje EF
│   │   ├── Accounts/                # Identity, TOTP, tworzenie konta z CLI
│   │   ├── ApiKeys/                 # format klucza, hash, AuthenticationHandler, polityki
│   │   ├── Sources/                 # pobieranie, ekstrakcja, porównanie, incydenty, robots.txt
│   │   ├── Scheduling/              # SchedulerService i plan zadań dnia
│   │   ├── Reports/                 # DailyReportService, ReportComposer, IReportGenerator, walidacja
│   │   ├── Claude/                  # ClaudeReportGenerator (SDK Anthropic), prompt, schemat JSON
│   │   ├── Push/                    # VapidKeyManager, PushNotifier, subskrypcje
│   │   ├── Export/                  # Scriban, latex_escape, IPdfCompiler (Tectonic / latexmk)
│   │   ├── Search/                  # zapytania pełnotekstowe, ts_headline
│   │   └── Mcp/                     # narzędzia MCP tylko do odczytu
│   └── ZdrowieInfo.Web/             # Razor class library: App.razor, strony czytelnika (static SSR),
│                                    #   panel (InteractiveServer + MudBlazor), wwwroot (PWA)
├── templates/report.tex.scriban
├── tests/
│   ├── ZdrowieInfo.Server.Tests/    # xUnit v3, Testcontainers PostgreSQL, WebApplicationFactory
│   └── fixtures/sources/<slug>/     # zapisane HTML każdego źródła (stan A, stan B)
├── deploy/linux/                    # install.sh, zdrowieinfo.service, backup (pg_dump + timer)
└── build/build-server.sh            # dotnet publish -r linux-x64 --self-contained
```

Zasady:

- `ZdrowieInfo.Server` zawiera tylko złożenie: `builder.Services.AddZdrowieInfoData().AddAccounts().AddSources()...` i mapowanie endpointów. Bez logiki.
- Migracje EF w projekcie hosta (`--migrate-only`), schemat wyłącznie z migracji.
- Usługi tła używają `IDbContextFactory<ZdrowieInfoDbContext>`, nigdy scoped DbContext z konstruktora.
- Czas zawsze z `TimeProvider`, strefa `Europe/Warsaw` z jednego helpera (`WarsawClock`), żeby testy mogły przesuwać czas.

## 3. Środowisko deweloperskie

Stan Twojego Maca (sprawdzony): .NET SDK 10.0.301, Docker, `latexmk`, Node; brak `psql` i Tectonic.

- **PostgreSQL**: kontener Dockera (`docker compose` w `deploy/dev/`, PostgreSQL 16 + rozszerzenia `unaccent`, `pg_trgm`). Testy integracyjne uruchamiają własny kontener przez Testcontainers.
- **PDF lokalnie**: `latexmk -xelatex` (masz). Tectonic opcjonalnie przez Homebrew, żeby testować tę samą ścieżkę co na serwerze.
- **Claude API bez klucza**: `FakeReportGenerator` (implementacja `IReportGenerator`) zwraca przykładowy raport zbudowany z prawdziwych wpisów, z poprawnymi cytatami — przechodzi walidację.
- **Push lokalnie**: Chrome akceptuje service worker i Web Push na `http://localhost`. iPhone wymaga HTTPS i prawdziwej domeny, więc test na telefonie dopiero na VPS.
- **Sekrety dev**: `dotnet user-secrets` dla hosta; w produkcji wrażliwe wartości w `app_settings` przez Data Protection (plan, sekcja Model).

## 4. Etapy

Każdy etap kończy się działającą, wdrażalną aplikacją. Rozmiar: S ≈ 1–2 dni pracy, M ≈ 3–5, L ≈ 1–2 tygodnie.

### Etap 0 — Weryfikacja i spike'i (S)

**Zrobione 17.11.2026 — wyniki w [docs/decyzje.md](docs/decyzje.md)** (D1–D5). Zmiany w planie: passkey proponowany do etapu 1 (do Twojej decyzji), Playwright odłożony do testu z VPS (z łącza domowego OW NFZ pobiera się zwykłym HTTP).

Rzeczy, które mogą zmienić projekt, sprawdzamy, zanim powstanie reszta.

1. **Wersje pakietów pod `net10.0`** — lista do `Directory.Packages.props`: Npgsql.EntityFrameworkCore.PostgreSQL 10.x, Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.x, MudBlazor 9.x, Microsoft.Extensions.Hosting.Systemd 10.0.x, Microsoft.Extensions.Http.Resilience, Cronos, AngleSharp, DiffPlex, Scriban, WebPush, QRCoder, Markdig, Anthropic (oficjalne SDK C#), ModelContextProtocol.AspNetCore, Microsoft.Playwright, xunit.v3, Testcontainers.PostgreSql, Microsoft.AspNetCore.Mvc.Testing. Dla każdego: najnowsza stabilna wersja i czy deklaruje `net10.0` (albo `netstandard2.0`/`net8.0` bez problemów). Przy okazji: czy Identity w .NET 10 obsługuje passkey w układzie static SSR (opcja z planu; jeśli tak — do etapu 1, jeśli nie — pomijamy).
2. **Spike: MudBlazor bez globalnej interaktywności.** Strona static SSR + strona z `@rendermode InteractiveServer` używająca `MudDataGrid`, `MudDialog`, `MudSnackbar`. MudBlazor wymaga providerów (`MudThemeProvider`, `MudPopoverProvider`, `MudDialogProvider`, `MudSnackbarProvider`) w interaktywnym drzewie; w układzie static SSR nie zadziałają. Oczekiwane rozwiązanie: komponent `AdminShell` z providerami, renderowany interaktywnie wewnątrz każdej strony panelu. Wynik spike'u ustala wzorzec dla wszystkich stron panelu.
3. **Spike: logowanie w Blazor static SSR.** Formularze logowania, TOTP i zmiany hasła muszą być static SSR (ciasteczko ustawia się tylko w zwykłym żądaniu HTTP, nie w obwodzie SignalR). Punkt odniesienia: szablon `dotnet new blazor -au Individual` — bierzemy jego strony Account, usuwamy rejestrację, tłumaczymy na polski.
4. **Spike: mazowiecki OW NFZ (Incapsula).** Jedno żądanie HttpClientem z naszym User-Agentem; jeśli odpowiedź to strona blokady, próba z Playwright headless. Wynik decyduje o `fetch_mode` tego źródła i czy Playwright w ogóle wchodzi do instalacji serwera (to kilkaset MB przeglądarki).
5. **Spike: Claude SDK C#.** Mały test (bez klucza tylko kompilacja; z kluczem od 18.11): `messages.create` z `OutputConfig.Format` (JSON Schema), `CacheControl` na prompcie systemowym, model z ustawień. Ustala dokładne nazwy typów SDK.

Gotowe, gdy: lista wersji jest w repozytorium, a cztery spike'i mają zapisany wynik (krótka notatka w `docs/decyzje.md`).

### Etap 1 — Szkielet (M)

**Kod gotowy 17.11.2026** — szczegóły i zakres sprawdzenia w [docs/decyzje.md](docs/decyzje.md) (D6–D10). Do kryterium „gotowe” brakuje wdrożenia na VPS: instalacja PWA na telefonie przez HTTPS, logowanie z TOTP i passkey.

1. `git init`, `.gitignore`, `global.json`, `Directory.Build.props` (wersja 0.1.0), `Directory.Packages.props`, `ZdrowieInfo.sln`, cztery projekty.
2. `AppVersion.Current` + test zgodności z `Directory.Build.props`.
3. `ZdrowieInfoDbContext` dziedziczący z `IdentityDbContext`, pierwsza migracja: Identity + `app_settings`.
4. Host: Kestrel na HTTP, `ForwardedHeaders` (zaufane proxy z konfiguracji), `UseSystemd()` (`Type=notify`), kultura `pl-PL`, Options `ZdrowieInfo` z walidacją przy starcie (`PublicUrl` musi być absolutnym `https://` w produkcji).
5. CLI w `Program.cs`: `--migrate-only`, `--create-user <login>` (hasło z wejścia, bez echa), `--reset-mfa <login>` (awaryjne).
6. Data Protection: klucze w `/var/lib/zdrowieinfo/keys` (ścieżka z konfiguracji), nazwa aplikacji ustalona na sztywno, żeby zmiana katalogu publikacji nie unieważniała kluczy.
7. Identity: rejestracja wyłączona, blokada 5 prób / 15 min, TOTP z QR (QRCoder) i kodami odzyskiwania, MFA wymuszone po pierwszym logowaniu. Cookie: 30 dni, sliding, `Secure` (poza dev), `HttpOnly`, `SameSite=Lax`, „zapamiętaj mnie” domyślnie zaznaczone. Walidacja security stamp co np. 30 min.
8. `ZdrowieInfo.Web`: `App.razor` bez globalnego trybu interaktywnego, układ czytelnika (prosty CSS, tryb jasny/ciemny), pusta strona główna, układ panelu wg wyniku spike'u.
9. PWA: `manifest.webmanifest`, ikony 192/512/maskable + `apple-touch-icon`, `service-worker.js` (cache-first dla statycznych z wersją `AppVersion.Current`, network-first dla stron), `js/pwa.js` z rejestracją. Zdarzenia push na razie puste.
10. Nagłówki bezpieczeństwa (CSP bez `unsafe-inline` dla skryptów, `X-Content-Type-Options`, `Referrer-Policy`), `robots.txt` dla samej aplikacji (`Disallow: /`).
11. `deploy/linux/`: `zdrowieinfo.service` (użytkownik `zdrowieinfo`, `Type=notify`, `ProtectSystem=strict`, `ReadWritePaths` na dane i klucze), `install.sh` (użytkownik, katalogi, baza, migracja, utworzenie konta), `build/build-server.sh` (self-contained linux-x64).

Testy: test wersji; integracyjny start aplikacji na Testcontainers; logowanie z TOTP przez `WebApplicationFactory`; brak strony rejestracji (404); trwałość cookie po restarcie (te same klucze Data Protection).

Gotowe, gdy: aplikacja działa na VPS za Twoim reverse proxy, instalujesz PWA na telefonie, logujesz się z TOTP, a restart usługi Cię nie wylogowuje.

### Etap 2 — Źródła i wykrywanie zmian (L)

**Kod gotowy 17.11.2026** — [docs/decyzje.md](docs/decyzje.md) (D11–D15, w tym przechodzenie po wielu stronach list). Stan bazowy i drugie sprawdzenie na żywo bez fałszywych alarmów; kryterium „tydzień bez fałszywych alarmów” wymaga uruchomienia na VPS. Playwright odłożony (D4).

Najważniejszy etap jakościowo: od niego zależy, czy aplikacja nie milczy.

**2a. Dane i konfiguracja źródeł**

1. Migracja: `sources`, `source_snapshots`, `source_items` (unikalne `(source_id, klucz)`), `source_incidents`, `check_runs`.
2. Seed 23 źródeł jako migracja danych (lub `--seed-sources`, idempotentne po URL), z grupą częstotliwości i metodą z planu.
3. **Selektory ustalamy z prawdziwych stron, nie z pamięci**: dla każdego źródła pobieramy stronę, wyznaczamy selektory (`item`, `title`, `link`, `date` albo `content` + lista `remove`), zapisujemy HTML jako fixture `tests/fixtures/sources/<slug>/a.html`. Większość to nfz.gov.pl (wspólny układ, kilka wzorców), do tego ezdrowie, CSIRT CeZ, CeZ, gov.pl ×2, OW NFZ.

**2b. Potok sprawdzenia źródła** (`SourceChecker`)

1. Pobranie: typowany `HttpClient` przez `IHttpClientFactory`, User-Agent `ZdrowieInfo/<wersja> (+kontakt z konfiguracji)`, `If-None-Match`/`If-Modified-Since`, timeout 30 s, resilience: ponowienie przy 5xx/timeout (2 próby z backoffem).
2. Ograniczanie: kolejka per domena z odstępem 2–3 s (losowy jitter), zadania na różnych domenach równolegle.
3. `robots.txt`: pobierany raz na dobę per domena; ścieżka zabroniona → źródło dostaje status *wymaga uwagi* z opisem, bez pobierania.
4. Ekstrakcja (AngleSharp):
   - *lista wpisów*: wpisy z selektora, link do URL bezwzględnego (bez fragmentu, z usunięciem parametrów śledzących), tytuł znormalizowany, data parsowana w kilku formatach polskich (`dd.MM.yyyy`, „17 listopada 2026”);
   - *cała strona*: główna treść po usunięciu elementów z `remove` (cookies, stopka, „data aktualizacji” jeśli zmienia się bez treści), normalizacja białych znaków, hash SHA-256.
5. Porównanie: nowy klucz = nowy wpis; dla *cała strona* zmiana hashu → DiffPlex (diff liniowy na tekście) zapisany jako treść „wpisu zmiany” z kluczem `url#yyyy-MM-dd`.
6. Szczegóły nowego wpisu: pobranie strony wpisu (ta sama kolejka domenowa), ekstrakcja treści selektorem `detail_content` (z domyślnym zapasem: `main`/`article`), zapis treści i `content_hash`. Załączniki PDF: na start tylko link i nazwa w treści, bez parsowania.
7. Kontrola jakości: 0 wpisów / błąd HTTP / timeout / strona blokady (wykrywanie po sygnaturach Incapsula) → nieudana próba.

**2c. Incydenty** (bez push — to etap 4)

- Nieudane sprawdzenie planuje ponowienie: +5 i +10 minut (3 próby w ok. 15 min). Dopiero trzecia nieudana otwiera incydent. Sukces w ponowieniu = brak incydentu (ale zapis w `source_snapshots`).
- Otwarty incydent: kolejne próby co godzinę, licznik prób, bez nowych incydentów. Pierwszy sukces zamyka incydent (`koniec`).
- Pola `push o awarii wysłany` / `push o przywróceniu wysłany` istnieją od razu (false), etap 4 je obsłuży.

**2d. Harmonogram** (`SchedulerService : BackgroundService`)

Jedna pętla, która co minutę (i od razu po starcie) liczy z bazy, co jest należne, zamiast trzymać timery w pamięci:

| Zadanie | Kiedy należne |
| --- | --- |
| Stan bazowy | źródło aktywne z `baseline_at IS NULL` — od razu |
| Sprawdzenie planowe | ≥ 5:00 czasu warszawskiego; źródło, którego ostatnie udane sprawdzenie jest starsze niż ostatni planowy termin jego grupy (R8); terminy grup wg planu (dziennie; pn tygodniowe; 1. dzień miesiąca; 1. dzień miesiąca nieparzystego) |
| Ponowienie źródła | zaplanowany czas ponowienia minął |
| Sprawdzenie otwartego incydentu | minęła godzina od ostatniej próby |
| Raport dzienny (etap 3) | ≥ 5:30, raport na dziś nie istnieje, sprawdzenie dzienne zakończone (włącznie z ponowieniami) albo jest ≥ 6:00 |
| Ponowienie raportu (etap 3) | co 10 min do 6:20; potem raport awaryjny |
| Porządki | raz na dobę: treść `source_items` starszych niż 12 miesięcy → NULL |

Reguły grup trzymamy w kodzie jako czyste funkcje `GroupsDueOn(DateOnly)` i `LastScheduledSlot(grupa, teraz)` (łatwe do testu); Cronos przydaje się, jeśli godzina startu ma być edytowalna w ustawieniach (`0 5 * * *`). Przejścia czasu letni/zimowy: harmonogram działa na czasie lokalnym Warszawy przeliczanym przez `TimeZoneInfo`, test dla ostatniej niedzieli marca i października.

**2e. Panel (InteractiveServer + MudBlazor)**

- Źródła: lista ze statusem, ostatnim sprawdzeniem i liczbą wpisów; edycja selektorów z przyciskiem **„Testuj selektor”** (pobiera stronę na żywo i pokazuje wyekstrahowane wpisy bez zapisu); włącz/wyłącz; „sprawdź teraz”.
- Uruchomienia: `check_runs` z wynikiem per źródło.
- Wykryte wpisy: lista ostatnich nowych wpisów z podglądem treści/diffu.
- CLI `--check-sources [slug]`: to samo co „Testuj selektor”, z konsoli, do strojenia na serwerze.

Testy:

- ekstraktor dla **każdego** źródła na fixture (liczba wpisów > 0, tytuły/linki/daty poprawne);
- porównanie: fixture A → B daje dokładnie oczekiwane nowe wpisy; ta sama strona dwa razy → zero zmian (ochrona przed fałszywymi alarmami);
- normalizacja *cała strona*: zmiana tylko banera cookies/daty → ten sam hash;
- `GroupsDueOn` dla charakterystycznych dat (pn 1. dzień nieparzystego miesiąca, zwykła środa, 1 lutego);
- 304 jako udane sprawdzenie bez zmian;
- harmonogram z `FakeTimeProvider`: 5:00, nadrabianie po restarcie o 5:40, serwer wyłączony 1. dnia miesiąca → grupa miesięczna sprawdzona 2. dnia, ponowienia +5/+10, otwarcie incydentu po 3. próbie, zamknięcie po sukcesie, zmiana czasu;
- stan bazowy nie oznacza wpisów jako nowych.

Gotowe, gdy: przez tydzień na VPS wykrywa nowe wpisy bez fałszywych alarmów (porównujesz ręcznie z tym, co widać na stronach). **Ten tydzień to czas kalendarzowy — równolegle piszemy etap 3.**

### Etap 3 — Raporty dzienne z Claude (L)

**Kod gotowy 17.11.2026** — [docs/decyzje.md](docs/decyzje.md) (D16–D19). Sprawdzone z generatorem przykładowym i w testach; wywołanie prawdziwego Claude API po wpisaniu klucza (od 18.11.2026).

**3a. Model i kontrakt**

1. Migracja: `reports` (unikalna data), `report_findings`, `finding_citations`, `api_keys`.
2. Kontrakt JSON raportu (C# rekordy + JSON Schema generowany z jednego miejsca): `title`, `summary`, `findings[]` (`importance`, `category`, `headline`, `body`, `action?`, `citations[]` z `item_id` i `quote`), `ignored[]` (`item_id`, `reason`). Ten sam kontrakt dla Claude i dla `POST /api/reports`.
3. `IReportGenerator` z implementacjami `ClaudeReportGenerator` i `FakeReportGenerator`; wybór w ustawieniach (dev: fake).

**3b. `DailyReportService`**

1. Zbiera niezaraportowane wpisy (R5) i stan źródeł dnia (sprawdzone / błąd / 0 wpisów / incydenty otwarte i zamknięte od ostatniego raportu).
2. Brak wpisów → raport „Brak zmian – <data>”, `is_empty = true`, bez wywołania API.
3. Są wpisy → `ReportComposer` składa wejście: profil przychodni, lista wpisów z `item_id`, tytułem, datą, URL, treścią lub diffem. Gdy łączny rozmiar wejścia jest bardzo duży (np. pierwszy dzień po długiej przerwie), **nie obcinamy po cichu**: dzielimy na partie po źródłach i łączymy wyniki, a w raporcie zaznaczamy, że powstał z kilku wywołań.
4. Walidacja odpowiedzi: każdy `item_id` istnieje i należy do wejścia; każdy cytat jest dosłownym fragmentem treści wpisu po tej samej normalizacji (białe znaki, cudzysłowy typograficzne, myślniki); każdy wpis z wejścia jest w `findings` albo w `ignored`. Niezgodność → jedno ponowienie z informacją o błędach; dalej źle → raport ze statusem *do weryfikacji* (zapisany, widoczny, oznaczony).
5. Błąd API (5xx, sieć, 429) → ponowienie co 10 min do 6:20, potem raport *błąd AI* z listą wykrytych wpisów bez analizy (tytuły i linki) i opisem błędu. Błąd 4xx dotyczący kredytów/limitu → od razu raport awaryjny i (po etapie 4) push.
6. Zapis w transakcji: raport, punkty, cytaty (kopia tytułu, URL, daty, fragmentu), `source_items.report_id` dla wszystkich wpisów wejścia. Potem (poza transakcją) eksport PDF (etap 5) i push (etap 4) — ich błąd nie cofa raportu.

**3c. `ClaudeReportGenerator`** (oficjalne SDK `Anthropic` dla C#)

- Model z `app_settings`, domyślnie `claude-sonnet-5-5` (zgodnie z planem); poziom `effort` też w ustawieniach, start `medium`, do przestrojenia po pierwszych raportach.
- Odpowiedź przez structured outputs (`OutputConfig.Format` z JSON Schema), bez prefill i bez wymuszonego `tool_choice` (oba zwracają 400 na tym modelu).
- Prompt systemowy stały (rola, kryteria ważności, kategorie, zasady cytowania), z `CacheControl`. **Uwaga:** przy jednym wywołaniu dziennie cache nie da oszczędności (żyje 5 min/1 h); pomaga tylko przy ponowieniach i partiach tego samego dnia. Zostawiamy, bo nic nie kosztuje.
- Obsługa `stop_reason`: `refusal` (sprawdzić przed odczytem treści; rozważyć serwerowy fallback modelu), `max_tokens` (traktowane jak błąd walidacji).
- Klucz API z `app_settings` (Data Protection), ustawiany w panelu, nigdy w logach.
- Zapis `ai_usage` z `response.Usage` → licznik użycia w panelu.

**3d. Klucze API i endpoint**

- Generator `mon_<prefiks>_<sekret>` (32 bajty losowe, base64url), hash SHA-256, porównanie w czasie stałym, `ostatnie użycie` aktualizowane najwyżej raz na minutę.
- `ApiKeyAuthenticationHandler` (schemat `ApiKey`, nagłówek `Authorization: Bearer`), polityki `reports:write` (teraz) i `mcp:read` (etap 7), rate limiting na `/api/*`.
- `GET /api/items/pending` i `POST /api/reports`: ten sam kontrakt i ta sama walidacja co dla Claude, cytaty bez `item_id` wg R10; zasady konfliktu wg R6.
- Migracja z kolumną `source_items.report_id` i polecenie `--mark-reported-before` (R9).
- Panel: tworzenie (klucz pokazany raz), lista, unieważnienie.

**3e. Widoki czytelnika (static SSR)**

- `/` — lista raportów (data, tytuł, liczniki krytycznych/wysokich, oznaczenie pustych, *do weryfikacji*, *błąd AI*).
- `/raporty/{data}` — podsumowanie, punkty pogrupowane wg ważności, działanie i termin, cytaty z linkami do źródeł, sekcja „Pominięte” (`ignored` z powodami), „Stan źródeł”. Treść przez Markdig bez surowego HTML.
- Panel: profil przychodni (zakresy, KSC, lista systemów), model, effort, klucz Claude, licznik użycia, „wygeneruj raport ponownie” dla danego dnia.

Testy: walidator cytatów (dosłowny, po normalizacji, zmyślony, przekręcony, z innego wpisu); ścieżki `DailyReportService` z fake generatorem (brak zmian, zmiany, niepoprawna odpowiedź → ponowienie → *do weryfikacji*, błąd API do 6:20 → raport awaryjny); idempotencja (drugie uruchomienie tego samego dnia nic nie robi); `POST /api/reports` (bez klucza 401, zły zakres 403, konflikt 409, `replace=true`); hash klucza i jego unieważnienie. Test z prawdziwym API — ręczny, oznaczony jako pomijany bez klucza.

Gotowe, gdy: codziennie przed 6:30 jest raport (pusty albo z punktami), cytaty przechodzą walidację, a `POST /api/reports` przyjmuje raport z zewnątrz. Klucz i pierwszy test na żywo — po odebraniu kredytów (od 18.11.2026).

**Po etapie 3 aplikacja realizuje główny cel.**

### Etap 4 — Powiadomienia push i dziennik incydentów (M)

**Kod gotowy 17.11.2026** — [docs/decyzje.md](docs/decyzje.md) (D22): endpointy pod `/push` zamiast `/api/push`, `IPushSender` zamiast `INotificationChannel`. Do sprawdzenia ręcznie: push na iPhonie (wymaga wdrożenia z HTTPS).

1. Migracja: `push_subscriptions`.
2. `VapidKeyManager` przy starcie: porównanie `PublicUrl` z zapisanym adresem, generowanie kluczy, usuwanie starych subskrypcji, log (plan, „Zmiana domeny”).
3. `GET /api/push/vapid-public-key`, `POST /api/push/subscribe`, `DELETE /api/push/subscribe` (cookie + antyforgery); przycisk „Włącz powiadomienia” i „Wyślij testowe” w ustawieniach.
4. `service-worker.js`: `push` (tytuł, treść, `data.url`) i `notificationclick` (fokus istniejącego okna albo otwarcie `/raporty/{data}`).
5. `PushNotifier` (pakiet WebPush): raport dzienny (liczba krytycznych i wysokich), „Brak zmian…”, otwarcie incydentu, przywrócenie dostępu (z czasem trwania), błąd kredytów API. 404/410 → usunięcie subskrypcji. Flagi „push wysłany” zapobiegają duplikatom po restarcie.
6. Strona `/incydenty` (static SSR): filtry po źródle i dacie przez parametry GET.
7. Interfejs `INotificationChannel`, żeby ewentualne ntfy (plan, „Zapas”) było drugą implementacją, bez zmian w wywołaniach.

Testy: wymiana kluczy przy zmianie `PublicUrl`; brak duplikatów push dla otwartego incydentu; usuwanie subskrypcji po 410 (fałszywy serwer push).

Gotowe, gdy: na iPhonie (PWA z ekranu głównego, iOS ≥ 16.4) przychodzi push o raporcie i o incydencie, a kliknięcie otwiera właściwą stronę.

### Etap 5 — Eksport LaTeX i PDF (S–M)

**Kod gotowy 17.11.2026** (przed etapem 4, na prośbę) — [docs/decyzje.md](docs/decyzje.md) (D20–D21): wbudowana formatka zamiast tej z planu (do podmiany przez `ZdrowieInfo:Export:TemplatePath`), latexmk z TeX Live; strona raportu w stylu dokumentu.

1. `templates/report.tex.scriban` — na start prosta formatka zastępcza; Twoja formatka zastąpi ją, gdy ją przekażesz.
2. Funkcja Scriban `latex_escape` (znaki z planu + `\textbackslash`, tylda i daszek przez `\textasciitilde{}`/`\textasciicircum{}`), URL-e przez `\url{}`.
3. `IPdfCompiler`: `TectonicCompiler` i `LatexmkCompiler`; wybór w konfiguracji. Katalog tymczasowy, limit 60 s z zabiciem procesu, bez shell-escape (`-Z shell-escape` wyłączony / `-no-shell-escape`).
4. Pliki w `/var/lib/zdrowieinfo/reports/<rok>/`, ścieżki w `reports`; przyciski „Pobierz PDF” / „Pobierz .tex” (z autoryzacją).
5. Błąd kompilacji → log i oznaczenie w panelu, raport i push niezależne. Panel: „Wygeneruj PDF ponownie”.
6. Instalacja: Tectonic na VPS + jednorazowe rozgrzanie cache pakietów w `install.sh`.

Testy: `latex_escape` dla wszystkich znaków specjalnych i polskich liter; kompilacja przykładowego raportu (pomijana, gdy brak kompilatora); tekst z próbą `\write18` nie wykonuje niczego.

Gotowe, gdy: każdy raport ma PDF z polskimi znakami i klikalnymi źródłami.

### Etap 6 — Wyszukiwanie (S–M)

**Kod gotowy 17.11.2026** — [docs/decyzje.md](docs/decyzje.md) (D23): `pl_unaccent` + dopasowanie fragmentu słowa zamiast słownika; fragment z podświetleniem w C# zamiast `ts_headline`; bez `pg_trgm` i stronicowania (do 100 wyników, filtry).

1. Decyzja o słowniku (otwarta kwestia w planie). Propozycja na start: własna konfiguracja `pl_unaccent` = `unaccent` + `simple` jako mapowanie tekstowe (`CREATE TEXT SEARCH CONFIGURATION ... ALTER MAPPING ... WITH unaccent, simple`). Uwaga techniczna: funkcja `unaccent()` nie jest `IMMUTABLE`, więc nie da się jej wprost użyć w kolumnie generowanej — konfiguracja tekstowa omija ten problem. Ispell dla polskiego można dodać później jako zamiana konfiguracji i przebudowa kolumn.
2. Migracja: generowane `search_vector` w `reports` i `report_findings` (wagi: nagłówek A, treść B), indeksy GIN; `pg_trgm` na tytułach dla literówek.
3. `/szukaj?q=&od=&do=&waznosc=&kategoria=&zrodlo=` jako zwykły formularz GET, wyniki z `ts_headline`, stronicowanie.

Testy: odmiana i brak polskich znaków („przerwa”/„przerwy”, „zrodlo”/„źródło” — na ile pozwala wybrany słownik), filtry, puste zapytanie.

Gotowe, gdy: znajdujesz dawny raport po słowie z treści punktu.

### Etap 7 — Serwer MCP (S)

**Kod gotowy 17.11.2026** — [docs/decyzje.md](docs/decyzje.md) (D24); podłączenie w README.

1. `ModelContextProtocol.AspNetCore` (weryfikacja wersji z etapu 0), transport HTTP pod `/mcp`, w tym samym procesie.
2. Uwierzytelnianie istniejącym schematem kluczy, polityka `mcp:read`, rate limiting.
3. Narzędzia: `search_reports` (to samo zapytanie co `/szukaj`), `get_report(date)`, `list_sources()`. Odpowiedzi zawierają URL-e źródeł i link do raportu w aplikacji.
4. Opis podłączenia w dokumentacji (np. Claude Code: `claude mcp add --transport http ...` z nagłówkiem `Authorization`).

Testy: wywołanie narzędzi przez klienta MCP w teście integracyjnym; brak dostępu bez zakresu.

Gotowe, gdy: agent AI wyszukuje raporty i cytuje źródła przez MCP.

## 5. Strategia testów

| Poziom | Co | Jak |
| --- | --- | --- |
| Jednostkowe | ekstraktory, normalizacja, `GroupsDueOn`, walidator cytatów, `latex_escape`, format kluczy | czyste funkcje, fixture'y HTML |
| Czas | harmonogram, ponowienia, incydenty, okno 5:00–6:30, zmiana czasu | `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing) |
| Integracyjne | migracje, EF, Identity, endpointy, generowane `tsvector` | Testcontainers PostgreSQL + `WebApplicationFactory` |
| Kontraktowe | odpowiedź Claude i `POST /api/reports` | ten sam schemat JSON, przykładowe dokumenty dobre i złe |
| Ręczne | Claude na żywo, push na iPhonie, PWA offline, Incapsula | lista kontrolna przy zakończeniu etapu |

Fixture'y źródeł odświeżamy, gdy strona zmieni układ: nowy HTML do `b.html`, poprawka selektora, test.

## 6. Co jest po Twojej stronie

| Kiedy | Co |
| --- | --- |
| przed końcem etapu 1 | VPS z Ubuntu 24.04, domena, reverse proxy z HTTPS kierujące na port aplikacji; adres kontaktowy do User-Agenta |
| etap 1 | ikona aplikacji (albo zgoda na prostą wygenerowaną) |
| od 18.11.2026 (etap 3) | odebranie kredytów API, workspace z limitem w Claude Console, klucz API wpisany w panelu |
| etap 3 | lista systemów używanych w przychodni w profilu |
| etap 2 | tydzień obserwacji: porównanie wykrytych wpisów ze stronami |
| etap 5 | formatka LaTeX |
| etap 6 | wybór słownika (albo akceptacja propozycji) |
| przed etapem 1 | potwierdzenie rozstrzygnięć R1–R12, zwłaszcza R12 (struktura i stack testów) |

## 7. Kolejność w czasie

```
Etap 0 ──► Etap 1 ──► Etap 2 ──► [tydzień obserwacji etapu 2 na VPS]
                                   │
                                   └─ równolegle: Etap 3 (kod z FakeReportGenerator; klucz od 18.11)
                                                    │
                                                    ▼
                                               Etap 4 ──► Etap 5 ──► Etap 6 ──► Etap 7
```

Etapy 5, 6 i 7 są od siebie niezależne — kolejność można zmienić według potrzeb.

## Po etapach 1–7 (17.11.2026)

Role administrator/użytkownik, zarządzanie użytkownikami (z usunięciem 2FA), raporty wstecz i okresowe z pobieraniem brakujących wpisów, usuwanie raportów, publiczny link do PDF (bez pobierania .tex), stronicowanie, wszystkie źródła codziennie — [docs/decyzje.md](docs/decyzje.md), D25–D31.

Instalacja, aktualizacje i wydania jak w DomainNotice: `install.sh` (menu konserwacji), `update.sh` z kopią bazy i wycofaniem, Panel → Aktualizacje, workflow `build` i `release` na GitHubie — [docs/decyzje.md](docs/decyzje.md), D32.
