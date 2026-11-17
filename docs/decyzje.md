# Decyzje — etap 0

17.11.2026. Wyniki weryfikacji i spike'ów z etapu 0 planu implementacji. Kod spike'ów był w `spikes/`; w etapie 1 zastąpiony właściwym kodem. Został tylko `spikes/ClaudeSpike` (poza rozwiązaniem) do pierwszego wywołania API z kluczem.

## D1. Wersje pakietów

Stan na 17.11.2026: .NET 11 jest już wydany, więc pakiety Microsoft przypinamy świadomie do **linii 10** (zasada linii 10.0.x). Lista w `Directory.Packages.props`, z włączonym `CentralPackageTransitivePinningEnabled`.

Wyjątek od „10.0.x”: `Microsoft.Extensions.Http.Resilience` i `Microsoft.Extensions.TimeProvider.Testing` pochodzą z repozytorium dotnet/extensions, które wydaje co miesiąc wersje 10.**x**.0, a nie poprawki 10.0.x. Ich ostatnie 10.0.x to 10.0.0 sprzed roku, więc bierzemy najnowsze z linii 10 (10.11.0). Nie wciągają nic z linii 11.

| Pakiet | Wersja | `net10.0` |
| --- | --- | --- |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | tak (tylko net10.0) |
| EFCore.NamingConventions (dodany w etapie 1, spoza listy stacku) | 10.0.1 | tak — nazwy tabel i kolumn snake_case, także Identity (`asp_net_users`) |
| Microsoft.EntityFrameworkCore.Design / Relational | 10.0.15 | tak |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.15 | tak |
| Microsoft.Extensions.Hosting.Systemd | 10.0.15 | tak |
| Microsoft.Extensions.Http.Resilience | 10.11.0 | tak |
| QRCoder | 1.8.1 | przez net6.0 / netstandard2.0 |
| AngleSharp | 1.10.4 | tak |
| DiffPlex | 1.9.0 | przez net6.0 |
| Cronos | 0.13.0 | przez net6.0 |
| Microsoft.Playwright | 1.64.1 | przez netstandard2.0 (patrz D4 — na razie niepotrzebny) |
| Anthropic (oficjalne SDK C#) | 13.10.0 | przez net9.0 |
| Markdig | 1.6.0 | tak |
| Scriban | 7.6.1 | przez net8.0 |
| WebPush | 1.0.13 | tak |
| ModelContextProtocol.AspNetCore | 2.4.0 | tak |
| MudBlazor | 9.14.0 | tak |
| xunit.v3 | 4.0.3 | przez net8.0 |
| Testcontainers.PostgreSql | 4.17.0 | tak |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.15 | tak |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 | tak |

Sprawdzenie (tymczasowe projekty próbne z pakietami aplikacji i testów, usunięte w etapie 1): restore i build bez błędów, test dymny xUnit v3 na Microsoft Testing Platform przechodzi, `dotnet list package --vulnerable --include-transitive` bez podatności, żadna zależność przechodnia nie wciąga linii 11.x. Jedyne przechodnie 9.0 to `System.Composition.*` z narzędzi EF Design (normalne).

## D2. MudBlazor bez globalnej interaktywności — wzorzec `AdminShell`

**Działa.** Sprawdzone w przeglądarce (projekt próbny z szablonu, usunięty w etapie 1; wzorzec przeniesiony do `ZdrowieInfo.Web/Admin/AdminShell.razor`): `MudDataGrid`, `MudSelect` (popover), `MudDialog` i `MudSnackbar` działają, bez błędów w konsoli, także po przejściach enhanced navigation static → panel → static → panel.

Wzorzec:

- `App.razor` bez globalnego trybu; CSS i JS MudBlazor dołączone globalnie (koszt: ~kilkaset KB także na stronach czytelnika — w etapie 1 rozważyć dołączanie tylko w układzie panelu przez `<HeadContent>`; do sprawdzenia, czy enhanced navigation to dobrze obsługuje).
- Strona panelu: `@rendermode InteractiveServer` + `[Authorize]`, a cała treść w `<AdminShell>`.
- `AdminShell` to **zwykły komponent bez własnego `@rendermode`** z `MudThemeProvider`, `MudPopoverProvider`, `MudDialogProvider`, `MudSnackbarProvider` i `ChildContent`. Jest interaktywny, bo leży wewnątrz interaktywnej strony. (Nadanie mu własnego trybu nie zadziała: `ChildContent` nie przechodzi przez granicę trybu renderowania.)

Osobliwość: komunikat snackbar sprzed przejścia na stronę statyczną pokazał się ponownie po powrocie do panelu (ten sam obwód, ta sama instancja `ISnackbar`). Rozwiązanie w etapie 1: `Snackbar.Clear()` przy inicjalizacji `AdminShell` albo krótki `VisibleStateDuration`.

## D3. Logowanie w static SSR, TOTP, passkey

**Podstawowe pytanie rozstrzygnięte.** Szablon `dotnet new blazor -au Individual -int Server` (.NET 10) generuje strony `Account/*` jako static SSR (bez `@rendermode`), więc ciasteczko ustawia się w zwykłym żądaniu HTTP.

Co faktycznie sprawdziłem w przeglądarce: rejestracja z formularza static SSR zalogowała użytkownika (ciasteczko ustawione), strona panelu z `[Authorize]` go rozpoznała, strona konfiguracji TOTP się renderuje. **Nie sprawdzałem**: osobnego formularza logowania, weryfikacji kodu TOTP przy logowaniu, trwałości ciasteczka po restarcie ani blokady konta — konfiguracja z planu (cookie 30 dni, sliding, `HttpOnly`, `SameSite=Lax`, blokada 5 prób / 15 min) się kompiluje i uruchamia, ale jej działanie potwierdzą testy integracyjne etapu 1.

Do etapu 1 bierzemy strony z szablonu i:

- usuwamy rejestrację, potwierdzanie e-maila, reset hasła e-mailem, logowanie zewnętrzne, dane osobowe;
- tłumaczymy na polski;
- **QR dla TOTP**: szablon pokazuje tylko klucz tekstowy; dodajemy QRCoder (`SvgQRCode` lub PNG jako data URI) renderowany po stronie serwera, bez JavaScriptu;
- MFA wymuszone po pierwszym logowaniu.

**Passkey**: szablon .NET 10 zawiera gotową obsługę (strony `Manage/Passkeys`, `PasskeySubmit.razor.js`, schemat `IdentitySchemaVersions.Version3`), przeznaczoną dla static SSR z małym modułem JS. **Obecne w szablonie, nie uruchamiane** w spike'u. Propozycja (do Twojej decyzji, plan oznacza passkey jako opcjonalny): włączyć w etapie 1 obok hasła + TOTP, skoro kod jest gotowy. Uwaga: passkey jest związany z domeną (RP ID) — po zmianie `PublicUrl` trzeba go zarejestrować ponownie, tak jak subskrypcje push.

## D4. Mazowiecki OW NFZ (Imperva Incapsula)

**Zwykły klient HTTP przechodzi.** Jedno żądanie z własnym User-Agentem: HTTP 200, 31,5 KB, właściwy tytuł strony i 19 linków do aktualności z prawdziwą treścią. Odpowiedź ma nagłówek `X-CDN: Imperva` i ustawia ciasteczka `visid_incap_*` / `incap_ses_*`, ale nie pokazuje strony blokady. `robots.txt` nie zabrania ścieżki `/dla-swiadczeniodawcow/`.

Decyzje:

- źródło startuje z `fetch_mode = http`; klient dla tej domeny trzyma `CookieContainer` między żądaniami (ciasteczka Imperva);
- **Playwright odłożony** do testu z VPS: nie wchodzi do etapu 2 ani do instalacji serwera, dopóki próba z serwera nie pokaże blokady;
- **zakres dowodu jest mały**: jedno żądanie listy z łącza domowego, bez pobrania strony pojedynczego wpisu. Adresy centrów danych Imperva traktuje często surowiej — test z VPS (lista + kilka stron wpisów) to pierwsza czynność etapu 2;
- RSS na stronie istnieje tylko dla pacjentów (`/rss/dla-pacjenta/...`), dla świadczeniodawców nie — zostaje parsowanie HTML.

## D5. Claude API (SDK C# `Anthropic` 13.10.0)

**Kompiluje się** (`spikes/ClaudeSpike`), wywołanie na żywo po otrzymaniu klucza (od 18.11.2026). Ustalony kształt wywołania:

- przestrzeń `Anthropic.Models.Beta.Messages`, `client.Beta.Messages.Create(...)` — beta jest potrzebna tylko dla serwerowego fallbacku modelu;
- model `claude-sonnet-5-5` (z ustawień), `MaxTokens = 16000`, bez streamingu;
- prompt systemowy jako `List<BetaTextBlockParam>` z `CacheControl = new BetaCacheControlEphemeral()`;
- `OutputConfig = new BetaOutputConfig { Effort = Effort.Medium, Format = new BetaJsonOutputFormat { Schema = ... } }` — structured outputs, schemat raportu z `additionalProperties: false` i enumami ważności/kategorii;
- **fallback modelu**: `Betas = ["server-side-fallback-2026-07-01"]`, `Fallbacks = new Default()` — gdy klasyfikator bezpieczeństwa odrzuci żądanie, serwer sam przekieruje je do zastępczego modelu. Włączone domyślnie; do wyłączenia, jeśli wolisz, żeby odmowa kończyła się raportem awaryjnym;
- przed odczytem treści: `StopReason == "refusal"` → raport awaryjny; `"max_tokens"` → jak błąd walidacji;
- wyjątki od najbardziej szczegółowego: `AnthropicRateLimitException`, `Anthropic5xxException`, `AnthropicIOException` (ponawiane co 10 min do 6:20) → `Anthropic4xxException` (bez ponawiania; klucz, kredyty, błędne żądanie → raport awaryjny + push) → `AnthropicApiException`;
- zużycie z `response.Usage` (`InputTokens`, `OutputTokens`, `CacheCreationInputTokens`, `CacheReadInputTokens`) → `reports.ai_usage`.

Do sprawdzenia przy pierwszym wywołaniu z kluczem: czy `"type": ["string", "null"]` dla `action` jest akceptowane przez structured outputs (zapas: `action` jako pusty string), i czy prompt systemowy przekracza minimalny rozmiar cache (jeśli nie — cache po prostu nie działa, bez błędu).

## Wpływ na plan implementacji

- Etap 1: wzorzec panelu wg D2; `Snackbar.Clear()` w `AdminShell`; passkey, jeśli go zaakceptujesz (D3); testy integracyjne logowania z TOTP, trwałości ciasteczka i blokady.
- Etap 2: Playwright odłożony (D4); test OW NFZ z VPS jako pierwsza czynność po wdrożeniu.
- Etap 3: kształt `ClaudeReportGenerator` wg D5.

# Decyzje — etap 1

## D6. Autoryzacja stron i `blazor.web.js`

Globalna `FallbackPolicy` („wszystko wymaga logowania”) **nie nadaje się**: obejmuje też żądania bez endpointu, a tak (middleware, nie endpoint) serwowany jest `_framework/blazor.web.js` — strona logowania traciła skrypt Blazora. Zamiast tego `MapRazorComponents<App>().RequireAuthorization()`: każda strona wymaga logowania, wyjątki mają `[AllowAnonymous]` (logowanie, kod 2FA, kod odzyskiwania, blokada, błędy). Nowe grupy endpointów (`/api`, `/mcp`) dostają `RequireAuthorization` jawnie.

## D7. Zasoby Blazora w hoście bez plików `.razor`

Komponenty są w `ZdrowieInfo.Web`, więc SDK samo nie dołącza `_framework/blazor.web.js` do hosta. W `ZdrowieInfo.Server.csproj` jest `RequiresAspNetWebAssets=true`; `build-server.sh` sprawdza, że plik trafił do publikacji. Testy integracyjne (środowisko `Testing`) wywołują `UseStaticWebAssets()`, tak jak robi to Development.

## D8. Nagłówki i HTML

- CSP ustawia host (`frame-ancestors 'none'`, skrypty tylko z własnej domeny, `style-src 'unsafe-inline'` dla MudBlazor). Domyślny dodatkowy nagłówek CSP Blazora (`frame-ancestors 'self'`) wyłączony (`ContentSecurityFrameAncestorsPolicy = null`), żeby nie było dwóch polityk.
- Polskie litery w HTML wprost (`AddWebEncoders` z `UnicodeRanges.All`), nie jako encje `&#x142;`.
- CSS i JS MudBlazor ładowane tylko pod `/panel`; link „Panel” wymusza pełne przeładowanie (`data-enhance-nav="false"`).

## D9. Konta

- Logowanie po loginie (bez e-maila). Rejestracja, reset hasła e-mailem, logowanie zewnętrzne, dane osobowe i wyłączanie 2FA usunięte z szablonu.
- **TOTP obowiązkowe**: claim `zi:mfa` w ciasteczku; bez niego `MfaEnforcementMiddleware` przepuszcza tylko stronę konfiguracji aplikacji uwierzytelniającej (i zasoby). „Przenieś na nowy telefon” zamiast „Wyłącz 2FA”.
- **Passkey**: logowanie składane ręcznie (`PerformPasskeyAssertionAsync` + `SignInAsync(isPersistent)`), bo `PasskeySignInAsync` nie przyjmuje „zapamiętaj mnie” — sesja passkey też trwa 30 dni. RP ID = host z `PublicUrl`. Bez automatycznego pytania o passkey przy wejściu na stronę logowania (conditional mediation z szablonu wyłączone, pole loginu bez `autocomplete="webauthn"`) — klucz wybiera się przyciskiem.
- Blokada liczy błędne hasła (`lockoutOnFailure: true` — szablon miał `false`).
- Hasło min. 12 znaków, bez wymogu znaków specjalnych; komunikaty Identity po polsku.
- Awaryjne polecenia serwera: `--set-password`, `--reset-mfa`.
- Security stamp sprawdzany co 5 minut (szablon: 30) — po zmianie hasła lub `--reset-mfa` inne sesje wygasają najpóźniej po 5 minutach.

## D10. PWA

- `manifest.webmanifest` i ikony w `wwwroot` hosta; `/service-worker.js` serwowany przez endpoint z wersją cache = `AppVersion.Current` i `Cache-Control: no-cache`.
- Service worker: cache-first dla zasobów statycznych (bez przekierowań i bez HTML pod adresem pliku — inaczej utrwaliłby stronę logowania), network-first dla stron czytelnika, bez cache dla `/Account`, `/panel`, `/api`, `/mcp`, `/_blazor`. Wylogowanie czyści zapisane strony. Obsługa `push` i `notificationclick` gotowa na etap 4.

## Co sprawdzono w etapie 1

- 25 testów (jednostkowe + integracyjne na PostgreSQL w Testcontainers): przekierowanie do logowania, brak stron rejestracji, nagłówki, zasoby PWA, wymuszona konfiguracja TOTP, logowanie hasło → kod, odrzucenie złego kodu, ciasteczko 30 dni z `Secure`/`HttpOnly`/`SameSite=Lax`, blokada po 5 próbach, sesja po restarcie (ta sama baza i klucze), `--reset-mfa`, szyfrowanie ustawień.
- Przeglądarka (lokalnie): logowanie, QR i konfiguracja TOTP, kody odzyskiwania, panel interaktywny (SignalR), brak naruszeń CSP, układ w szerokości telefonu, sesja po restarcie serwera.
- Paczka linux-x64 uruchomiona w kontenerze Ubuntu 24.04 (konfiguracja przez zmienne środowiskowe jak w `zdrowieinfo.env`): migracje, start, `/healthz`, strona logowania, `blazor.web.js`.

**Nie sprawdzono:** `install.sh` i jednostki systemd na prawdziwym serwerze (tylko kontrola składni), dodania i logowania passkey (panel podglądu nie ma uwierzytelniacza — działa endpoint opcji WebAuthn), instalacji PWA na iPhonie przez HTTPS.

# Decyzje — etap 2

## D11. Selektory i fixture'y

Selektory ustalone na prawdziwych stronach (17.11.2026), zapisanych w `tests/fixtures/sources/<slug>/`: `a.html` (lista lub strona), `b.html` (drugie pobranie stron „cała strona” — test stabilności), `detail.html` (strona pojedynczego wpisu, po jednej na portal). W CeR wycięte obrazki `data:` (4,6 MB → 120 KB). Lista źródeł z selektorami: `src/ZdrowieInfo.Server.Core/Sources/DefaultSources.json`, wgrywana przy starcie **tylko dla brakujących slugów** — edycje z panelu nie są nadpisywane.

| Portal | Wpis | Tytuł / link | Data | Treść wpisu |
| --- | --- | --- | --- | --- |
| nfz.gov.pl, nfz-warszawa.pl | `.news-list .news` | `h3.title a` | `.date` | `.news-module` |
| ezdrowie.gov.pl | `li.article-list-element` | `.articleListBigBoxTitle` / `a.showMoreText` | `.articleListBigBoxAddData` | `.articleMod` |
| csirt.cez.gov.pl | `.teaser` (bez bloku „Polecane”) | `.views-field-field-title` / `.views-field-view-node a` | `time[datetime]` | `.field--name-field-lead, .field--name-field-content` |
| cez.gov.pl | `.view-content .views-row` | `.views-field-title a` | `time[datetime]` | `.node__content` |
| gov.pl (MZ) | `.art-prev li` | `.title a` | `.date` | `#main-content` |
| NFZ „cała strona” | — | — | — | `.module-content .site-text` |
| gov.pl CeR | — | — | — | `#main-content` bez `.event-date` |

Na żywo (17.11.2026, z łącza domowego): wszystkie 23 źródła OK, pełne sprawdzenie ~42 s (19 źródeł na nfz.gov.pl × 2–3 s odstępu). Stan bazowy + drugie sprawdzenie: 0 fałszywych alarmów. ezdrowie.gov.pl pokazuje tylko 3 najnowsze wpisy — pełniejsza lista kategorii to `/portal/home/aktualnosci/?isExtend=true&categoryId=30` (adresu źródła nie zmieniałem; do decyzji).

## D12. Wykrywanie zmian

- **Klucz wpisu** = URL bez fragmentu, parametrów `utm_*` i bez `www.` (NFZ linkuje raz do `nfz.gov.pl`, raz do `www.nfz.gov.pl`).
- **Aktualizacja wpisu**: ten sam klucz, ale inny tytuł lub data na liście (NFZ podbija stare wpisy „[AKTUALIZACJA…]”) → `UpdateCount++`, `PreviousTitle`, ponowne pobranie treści, wpis wraca do raportu.
- **Cała strona**: tekst z blokami i **adresami linków** („Komunikat SWIAD (wersja 8.8.3) (https://…/plik.doc)”) — podmiana pliku pod tą samą nazwą też jest zmianą. Zmiana → wpis „Zmiana strony: …” z diffem (`+`/`-` z linią kontekstu). Test: tekst 14 stron identyczny między dwoma pobraniami.
- **Stan bazowy** = pierwsza **udana** ekstrakcja (nie pierwsza próba). Zmiana adresu, metody albo selektorów w panelu → nowy stan bazowy i wyczyszczone walidatory HTTP.
- **Walidatory** (`ETag`, `Last-Modified`) zapisywane tylko po udanej ekstrakcji, używane tylko po stanie bazowym; `304` = udane sprawdzenie bez zmian.
- **Treść wpisów** pobierana po zapisaniu listy; wpis bez treści jest ponawiany przy kolejnych sprawdzeniach (do 3 prób, potem zostaje opis z listy). W jednym uruchomieniu każdy URL pobierany raz; treść z innego źródła o tym samym URL-u jest kopiowana (NFZ Aktualności i Komunikaty DI mają te same artykuły). **Do etapu 3:** raport musi łączyć wpisy o tym samym URL-u z różnych źródeł w jeden punkt.

## D13. Pobieranie

- User-Agent `ZdrowieInfo/<wersja>`; adres kontaktowy tylko z `ZdrowieInfo:Sources:ContactEmail` (domyślnie pusty).
- Jedno żądanie naraz na domenę (bez `www.`), 2–3 s odstępu; domeny równolegle.
- `robots.txt` raz na dobę na domenę, RFC 9309 (grupa `zdrowieinfo`, inaczej `*`; najdłuższa reguła wygrywa). Błąd pobrania `robots.txt` = brak ograniczeń, nie incydent. Test na prawdziwym `robots.txt` OW NFZ (grupa `Fasterfox` z `Disallow: /` nas nie dotyczy).
- Ponowienia przy 5xx/timeoutach (Polly, 2 próby), limit 30 s na żądanie i 10 MB na odpowiedź, ciasteczka trzymane przez cały czas życia procesu (Imperva).
- Blokada: HTTP 403/429 albo strona Incapsula zamiast treści.

## D14. Incydenty i harmonogram

- Błąd → ponowienie po 5 min, potem po kolejnych 5 (3 próby w ~10–15 min). Trzecia nieudana → incydent (początek = pierwsza nieudana próba), dalej próba co godzinę bez nowych incydentów. Pierwszy sukces zamyka incydent. Pojedynczy błąd naprawiony ponowieniem nie jest incydentem. Push o incydentach: etap 4 (pola w tabeli już są).
- Harmonogram co minutę liczy z bazy, co jest należne: stan bazowy (nowe/edytowane źródła), planowe (ostatni udany sukces starszy niż ostatni termin grupy — R8), ponowienia. Restart i przerwa serwera niczego nie gubią. Jedno uruchomienie naraz (panel „Sprawdź teraz” czeka na harmonogram).
- W `appsettings.Development.json` harmonogram wyłączony, żeby `dotnet run` nie odpytywało stron; lokalnie `--check-now`.
- Porządki raz na dobę: treść wpisów starszych niż 12 miesięcy usuwana.

## D15. Wiele stron listy – żeby nie pominąć wpisów

Problem: sprawdzana była tylko pierwsza strona listy. Przy więcej niż jednej stronie nowych wpisów naraz (np. po przerwie serwera, po weekendzie dla źródeł tygodniowych, na ezdrowie już przy 4 nowych) część by przepadła.

- **Reguła:** jeśli ostatni (najstarszy) wpis na stronie jest nowy, za nim mogą być kolejne nowe → pobierana jest następna strona (selektor `NextPage`), aż ostatni wpis strony będzie znany **albo** strona sięgnie wpisów starszych niż ostatnie udane sprawdzenie (minus dzień zapasu). Granica daty chroni przed zalaniem raportu, gdy strona usunie wpis lub zmieni adresy (wtedy na koniec 1. strony wsuwa się stary, nieznany wpis) — test `Removed_entry_does_not_flood_report_with_older_pages`. Stan bazowy bierze tylko pierwszą stronę. Wpisy wyróżnione u góry (bump, „hero”) nie przeszkadzają, bo liczy się ostatni wpis.
- **Błąd kolejnej strony = nieudane sprawdzenie.** Nic nie jest zapisywane, ponowienie za 5 min przechodzi listę od początku. Zapis części wpisów zostawiłby trwałą lukę (następnym razem ostatni wpis 1. strony byłby już znany).
- **Strony:** NFZ Walidacje `.pager li.active + li a`, OW NFZ `.pager span.active + a`, CSIRT i CeZ `.pager__item--next a`, MZ `a.pagination__arrow-right`. Testy na prawdziwych drugich stronach (`page2.html`). CSIRT pokazuje najnowszy artykuł na górze **każdej** strony — deduplikacja po kluczu.
- **Archiwum NFZ** ma na 1. stronie karuzelę „Ważne!” (`.priority-slider`, bez dat) poza listą `.news` — selektor obejmuje oba bloki (inaczej ginęły 2 wpisy). Wpisy bez daty są w trybie archiwum zawsze brane.
- **Bloki NFZ bez stronicowania** (Aktualności dla świadczeniodawców, Słowniki, Komunikaty DI — „10 najnowszych”): przy przepełnieniu lista zapasowa `OverflowUrl` = archiwum „Aktualności Centrali” ze stronicowaniem; brane są wpisy z datą ≥ (dzień ostatniego udanego sprawdzenia − 1 dzień), kolejne strony archiwum, dopóki sięgają tej daty. Archiwum ma wszystkie tematy (też dla pacjentów) — świadomie wolimy nadmiar (Claude oznaczy je jako pominięte) niż utratę. Filtr tematu NFZ działa tylko na pierwszej stronie, więc się na nim nie opieramy.
- **ezdrowie.gov.pl:** zamiast strony HTML (3 wpisy) źródło czyta endpoint, z którego strona ładuje listę: `/Artykuły/ListaArtykulow1/30/*/*/40` (JSON, 40 najnowszych w kategorii „Dla Podmiotów Leczniczych”). Nowy format listy `Json` (pola zamiast selektorów CSS, `LinkTemplate` dla adresu artykułu). Klucze wpisów są te same co z HTML.
- **Limit i ostrzeżenie:** najwyżej `MaxListPages` = 10 stron na sprawdzenie. Gdy nie da się dojść do znanego wpisu (brak kolejnej strony albo limit), sprawdzenie jest udane, ale z **ostrzeżeniem** („część wpisów mogła zostać pominięta”) w historii pobrań, uruchomieniach, stanie źródła i logu — nie cicha utrata. W etapie 4 można dodać push.
- **Walidatory HTTP tylko dla „całej strony”.** Listy są pobierane zawsze w całości (jedno żądanie): nie da się dowieść, że ETag gov.pl (`W/"…"`, wygląda na hash treści) zmienia się razem z listą, a 304 w złym momencie ukryłby nowe wpisy.
- **Do etapu 3:** ostrzeżenie znika ze stanu źródła po następnym czystym sprawdzeniu — raport dzienny powinien brać ostrzeżenia z `source_snapshots` dnia do sekcji „Stan źródeł”.
- **Treść wpisów:** limit pobrań treści podniesiony do 100 na źródło w jednym sprawdzeniu, żeby przy wielu nowościach raport miał treść wszystkich.
- **Sprawdzone na żywo (17.11.2026):** CSIRT ze stanem bazowym z 2. strony → sprawdzenie od 1. strony przeszło na 2., zatrzymało się na znanym wpisie: 5 nowych (bez wyróżnionego duplikatu), bez ostrzeżenia. Komunikaty DI bez znanych wpisów → lista zapasowa (archiwum), wpisy od 16.11, treść pobrana dla wszystkich. Po poprawkach dwa pełne przebiegi 23 źródeł: 0 błędów, 0 nowych, 1 strona na źródło. Endpoint JSON ezdrowie pokazał 5 wpisów z 16.11 — strona HTML (3 wpisy) pominęłaby m.in. „Planowane prace techniczne w dniach 16-20.11.2026” i przerwę SSOZ II.
- **Nie sprawdzone:** limit 10 stron i przejście przez kilka stron archiwum NFZ (tylko logika i testy jednej strony).
- **Aktualizacja konfiguracji na istniejącej instalacji:** lista domyślna nie nadpisuje źródeł z bazy; po aktualizacji aplikacji z nowymi selektorami: `--apply-default-sources [slug]` (zmienione źródła dostają nowy stan bazowy).

# Decyzje — etap 3

## D16. Raport dzienny

- **Wejście:** wszystkie wpisy z `reported_at IS NULL` i nie ze stanu bazowego (R5). Ten sam artykuł (URL) z kilku źródeł to **jeden** wpis wejścia z kilkoma `source_item_ids` (NFZ Aktualności i Komunikaty DI). Aktualizacja wpisu (zmiana tytułu/daty) zeruje `reported_at`, więc wraca do najbliższego raportu jako „aktualizacja”. Treść powyżej 20 000 znaków jest skracana jawnie (adnotacja w wejściu), nie po cichu.
- **Brak wpisów** → raport „Brak zmian – data” bez wywołania API (z sekcją „Stan źródeł”).
- **Są wpisy** → generator → walidacja → przy błędach **jedna** ponowna próba z listą błędów → lepsza z dwóch odpowiedzi; nadal błędy → status „do weryfikacji” (cytaty oznaczone jako niezweryfikowane, uwagi w raporcie).
- **Ponowienie z błędami to nowe, jednoturowe żądanie** (wejście + lista błędów), nie rozmowa z poprzednią odpowiedzią — omija to zasady przekazywania bloków thinking między turami w nowszych modelach.
- **Błąd przejściowy** (429, 5xx, sieć) → ponowienie co 10 min; po 6:20 i co najmniej jednej wcześniejszej próbie → raport „bez analizy” (lista wpisów). **Błąd trwały** (brak klucza, 4xx — klucz, kredyty, parametry; odmowa) → od razu raport „bez analizy”.
- **Harmonogram:** od 5:30; do 6:00 czeka, aż sprawdzanie i ponowienia źródeł się zakończą; jeden raport na dzień (unikalna data). Liczone z bazy jak harmonogram źródeł.
- **„Stan źródeł”** w każdym raporcie: problemy i ostrzeżenia (np. „możliwe pominięte wpisy” z D15) z pobrań od poprzedniego raportu, otwarte incydenty, przywrócenia dostępu.
- **Widok:** `/raporty/rrrr-mm-dd` (R1) — punkty wg ważności, działanie, cytaty z linkiem do źródła i datą, pominięte wpisy z powodami, stan źródeł, wpisy źródłowe. Static SSR, czytelny na telefonie.
- **Start raportów na serwerze:** `--mark-reported-before rrrr-mm-dd` (R9) — wpisy z tygodnia obserwacji nie trafiają hurtem do pierwszego raportu.

## D17. Walidacja cytatów

Cytat musi być dosłownym fragmentem tytułu, opisu albo treści wskazanego wpisu po ujednoliceniu: wielkość liter, białe znaki, cudzysłowy („” → "), myślniki (– — → -), miękkie łączniki. Wielokropek („…”, „...”, „[...]”) dzieli cytat na części, które muszą wystąpić w tej kolejności. Minimum 8 znaków. Każdy punkt musi mieć cytat; każdy wpis wejścia musi być w punkcie albo na liście `ignored` (dla Claude; zewnętrzny producent nie musi rozliczać wszystkiego).

## D18. Claude

- `ClaudeReportGenerator`: oficjalne SDK `Anthropic`, `client.Beta.Messages.Create` (beta tylko dla serwerowego fallbacku modelu `server-side-fallback-2026-07-01` + `Fallbacks = new Default()`), structured outputs z JSON Schema raportu (enumy ważności i kategorii po polsku), prompt systemowy w `ReportComposer.SystemPrompt` z `CacheControl`, model i effort z panelu (domyślnie `claude-sonnet-5-5`, `medium`), klucz z `app_settings` (Data Protection). Bez streamingu, `MaxTokens` 16 000.
- Prompt systemowy: kryteria ważności z planu, kategorie, zasady cytowania, łączenie wpisów o tej samej sprawie, obowiązek rozliczenia każdego wpisu. Profil przychodni, data raportu i wpisy w wiadomości użytkownika (zmienna część poza cache).
- `SampleReportGenerator` (`ZdrowieInfo:Reports:Generator = Sample`, domyślnie w Development i testach): punkt na wpis, cytat = pierwsze zdanie (przechodzi walidację), bez oceny — do pracy bez klucza.
- **Nie sprawdzone z prawdziwym API** (klucz od 18.11.2026): czy schema z `"type": ["string", "null"]` jest akceptowana, czy odpowiedź mieści się w 16 000 tokenów przy wielu wpisach, jakość ocen i cytatów. Kolejność na jutro (lokalnie, baza deweloperska):
  1. uruchom aplikację z generatorem Claude: `cd src/ZdrowieInfo.Server && ZdrowieInfo__Reports__Generator=Claude dotnet run`, wpisz klucz w **Panel → Ustawienia** (i listę systemów w profilu), zatrzymaj aplikację;
  2. ponownie przeanalizuj wpisy z raportu przykładowego z 17.11: `ZdrowieInfo__Reports__Generator=Claude dotnet run -- --generate-report 2026-11-17 --replace` (bez daty polecenie wzięłoby dzień bieżący, a wtedy nie ma nowych wpisów → „Brak zmian”);
  3. przejrzyj `/raporty/2026-11-17`: oceny, cytaty, sekcję „Pominięte”, uwagi walidacji; w logu zużycie tokenów i trafienie cache.
- **Ograniczenia:** raport „bez analizy” też zużywa wpisy — ponowną analizę daje tylko `--replace` dla tej daty (np. po dniach bez klucza). Raport przygotowany ręcznie przed 5:00 blokuje automatyczny raport tego dnia (jeden raport na datę). Zastąpienie, które się nie uda, zostawia poprzedni raport bez zmian.

## D19. Klucze API i API raportów

- Klucz `mon_<8 znaków>_<sekret>` (32 losowe bajty, base64url); w bazie prefiks i SHA-256 sekretu, porównanie w czasie stałym; „ostatnie użycie” zapisywane najwyżej raz na minutę. Pełny klucz widać tylko raz (panel → Klucze API).
- Schemat `ApiKey` (`Authorization: Bearer …`), polityki na zakresy `reports:write` i `mcp:read`, limit 60 zapytań/min na klucz (`/api/*`). Wymuszanie MFA dotyczy tylko sesji z ciasteczka logowania — programy z kluczem nie mają TOTP.
- `GET /api/items/pending` — wpisy czekające na raport (z `item_id` i treścią do cytowania). `POST /api/reports?date=&replace=` — ten sam JSON co od Claude; cytat z `item_id` musi być dosłowny (inaczej 422 z listą błędów); cytat z samym `url` spoza wejścia → niezweryfikowany, raport „do weryfikacji” (R10); istniejący raport → 409, `replace=true` zastępuje (R6). Za zaraportowane uznawane są tylko wpisy użyte w cytatach albo na liście pominiętych.

# Decyzje — etap 5 (dokument i PDF)

## D20. Raport jako dokument

Strona `/raporty/rrrr-mm-dd` wygląda jak dokument, a nie strona internetowa: kartka na tle „biurka”, szeryfowa typografia (Palatino / TeX Gyre Pagella — ta sama co w PDF), metryczka (przygotował, punkty, wpisy, źródła), numerowane sekcje (1. Podsumowanie, 2. Ustalenia z punktami 2.1, 2.2…, dalej pominięte wpisy, stan źródeł, wpisy źródłowe), cytaty z numerowanymi przypisami (tytuł, data, adres źródła) i powrotem do cytatu. Na telefonie kartka na całą szerokość; styl do druku (A4, bez menu). Numeracja punktów i przypisów pochodzi z jednego modelu (`ReportView`) — strona i PDF są zgodne.

## D21. PDF z LaTeX-a

- `.tex` generuje serwer z szablonu Scriban (`src/ZdrowieInfo.Server.Core/Export/report.tex.scriban`, wbudowany) — Claude nie pisze LaTeX-a. Własna formatka: `ZdrowieInfo:Export:TemplatePath` (ten sam model danych; formatkę z planu można przenieść później).
- Każdy tekst przez `latex` (znaki `\ { } $ & # ^ _ % ~` i `"` — aktywny w babel-polish), adresy przez `latex_url` do `\url{}`. Test: tytuł z `\immediate\write18{…}` i `\input{/etc/passwd}` kompiluje się jako zwykły tekst, polecenie się nie wykonuje.
- XeLaTeX: `fontspec` + `babel` (polski), czcionki TeX Gyre Pagella/Heros **po nazwie pliku** (działa bez rejestracji czcionek w systemie), `geometry`, `titlesec`, `fancyhdr`, `footmisc`, `hyperref`. Cytaty jako przypisy u dołu strony.
- Kompilacja: `latexmk -xelatex -no-shell-escape -interaction=nonstopmode -halt-on-error` (domyślnie) albo `tectonic --untrusted` (`ZdrowieInfo:Export:Compiler`); katalog tymczasowy w katalogu danych, limit 60 s z zabiciem drzewa procesów, cache TeX-a (`TEXMFVAR`) w katalogu danych (usługa ma `ProtectSystem=strict`).
- Pliki: `<DataDirectory>/reports/<rok>/raport-<data>.{tex,pdf}`, ścieżki i ostatni błąd (`pdf_error`) w raporcie. Eksport automatycznie po zapisaniu raportu (także z API); błąd nie blokuje raportu — PDF generuje się ponownie przy pobraniu.
- Pobieranie (zalogowany): `/eksport/raport-rrrr-mm-dd.pdf` (otwierany w przeglądarce) i `.tex` (plik). Przyciski „Pobierz PDF” i „.tex” na stronie raportu. CLI: `--export-report [data]`.
- Serwer: `install.sh` przy każdej instalacji i aktualizacji instaluje `texlive-xetex texlive-lang-polish texlive-latex-extra texlive-fonts-recommended fonts-texgyre latexmk` (bez rekomendacji, ok. 300 MB). `fonts-texgyre` jest konieczne: bez rekomendacji TeX Gyre w OpenType się nie instaluje i kompilacja kończy się błędem czcionki (sprawdzone w kontenerze Ubuntu 24.04).
- Odpowiedzi `/eksport/*` nie dostają CSP stron ani `X-Frame-Options` (zostaje `nosniff`): `object-src 'none'`, `sandbox` i `X-Frame-Options: DENY` blokują wbudowaną przeglądarkę PDF (sprawdzone w Chromium).
- Pola jednoliniowe (tytuł, nagłówki, nazwy, metadane PDF) przez `latex_line` — nowa linia jako spacja; `\par` w argumencie `\textbf` przerywałby kompilację.
- Znaki, których nie ma w czcionce (np. emoji, niektóre strzałki), są pomijane w PDF bez błędu (ostrzeżenie w logu LaTeX-a).
- Strona raportu w trybie ciemnym ma ciemną „kartkę” (zgodnie z ustawieniem systemu); PDF zawsze jasny.
- Powiadomienie push o raporcie idzie po zapisaniu raportu, przed eksportem PDF (nie czeka na LaTeX) — D22.

# Decyzje — etap 4 (push i incydenty)

## D22. Powiadomienia push

- WebPush (VAPID). Klucze w `app_settings` (`push.vapid`, zaszyfrowane Data Protection) razem z adresem `PublicUrl`, dla którego powstały. Inny `PublicUrl` przy starcie → nowe klucze i usunięcie wszystkich subskrypcji (stare i tak nie działają w nowej domenie; log ostrzeżenia). Nadawca (`subject`): `mailto:` z `ZdrowieInfo:Sources:ContactEmail`, inaczej adres https aplikacji.
- Endpointy pod `/push` (nie `/api/push` jak w planie — `/api/*` wymaga klucza API): `GET /push/vapid-public-key`, `POST /push/subscriptions`, `POST /push/subscriptions/delete`, `POST /push/test`. Zalogowany użytkownik (ciasteczko) i antiforgery w nagłówku `RequestVerificationToken`. Konto → Powiadomienia: włącz/wyłącz na tym urządzeniu, testowe, lista urządzeń.
- `NotificationDispatcher` wysyła zaległe powiadomienia z bazy (flagi `push_sent` w raporcie, `failure_push_sent`/`recovery_push_sent` w incydencie), więc restart niczego nie dubluje ani nie gubi. Wywoływany po zapisaniu raportu (przed PDF), po każdym sprawdzeniu źródeł i co minutę w harmonogramie. Zdarzenia starsze niż doba są tylko oznaczane (bez zalewu powiadomień po dłuższej przerwie). Incydent zamknięty, zanim poszło powiadomienie o nim → tylko powiadomienie o przywróceniu (z czasem trwania).
- Treść: raport — tytuł, liczba krytycznych i wysokich (albo punktów), podsumowanie; „Bez analizy – …” przy raporcie awaryjnym (np. brak kredytów API), „brak zmian” przy pustym. Kliknięcie otwiera raport albo `/incydenty`.
- 404/410 z serwera push → subskrypcja usunięta. Wysyłka przez interfejs `IPushSender` (w testach fałszywy); osobny `INotificationChannel` z planu nie jest potrzebny, dopóki nie ma drugiego kanału (ntfy).
- `/incydenty` — static SSR, filtry GET `zrodlo`, `od`, `do`.

# Decyzje — etap 6 (wyszukiwanie)

## D23. Wyszukiwanie

- Konfiguracja tekstowa `pl_unaccent` (kopia `simple` + `unaccent`), kolumny generowane `search_vector` w `reports` (tytuł A, podsumowanie B) i `report_findings` (nagłówek A, treść i działanie B), indeksy GIN. Bez polskiego słownika (ispell) — `simple` nie odmienia słów.
- Dlatego dwa przebiegi: pełnotekstowy (`websearch_to_tsquery`: cudzysłowy, `or`, `-słowo`; ranking `ts_rank`) i dopasowanie fragmentu słowa bez polskich znaków (`unaccent(...) ILIKE '%przerw%'`, każde słowo zapytania musi wystąpić) dopisane za trafieniami pełnotekstowymi. „przerw” znajduje „przerwa” i „przerwy”, „zrodlo” znajduje „źródło”. Przy obecnej skali (setki raportów) ILIKE bez indeksu wystarcza; przy dużym wzroście — `pg_trgm`.
- Fragment z podświetleniem (`<mark>`) liczony w C#, nie `ts_headline` (który nie podświetla fragmentów słów); tekst kodowany jako HTML przed wstawieniem znaczników. Trafienia w samym raporcie (tytuł/podsumowanie, np. „Brak zmian”) tylko bez filtrów ważności/kategorii/źródła.
- `/szukaj` — formularz GET (bez JavaScriptu), filtry: daty, ważność, kategoria, źródło (przez cytaty punktu). Do 100 wyników grupowanych po dniu, bez stronicowania (zawężanie filtrami). Wspólna usługa `ReportSearch` dla strony i MCP.

# Decyzje — etap 7 (MCP)

## D24. Serwer MCP

- `ModelContextProtocol.AspNetCore` 2.4.0, Streamable HTTP pod `/mcp`, tryb bezstanowy (domyślny od wersji protokołu 2026-07-28; starsi klienci z `initialize` też działają). Ten sam proces co aplikacja.
- Klucz API z zakresem `mcp:read` (`Authorization: Bearer mon_…`), limit 60 zapytań/min na klucz jak w `/api`. Bez zakresu 403, bez klucza 401.
- Narzędzia tylko do odczytu (adnotacja `readOnlyHint`): `search_reports(query, from, to, importance, category, source, limit)` — to samo wyszukiwanie co `/szukaj`, ważność i kategorie po polsku jak w raporcie, źródło po identyfikatorze z `list_sources`; `get_report(date)` — cały raport (bez daty najnowszy) z numerami punktów, cytatami i adresami źródeł, linkiem do raportu i PDF; `list_sources()` — źródła, stan i otwarty incydent. Wyniki jako JSON (snake_case). Złe parametry → błąd narzędzia z listą dozwolonych wartości.
- Strona błędu (`UseStatusCodePagesWithReExecute`) wyłączona dla `/api`, `/mcp` i `/push`: ponowne wykonanie POST bez klucza na stronie Blazor kończyło się 400 (antiforgery) zamiast 401.
