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
