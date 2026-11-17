// ZdrowieInfo – rejestracja service workera (PWA) i włączanie powiadomień push (strona Konto → Powiadomienia).
(function () {
    if (!('serviceWorker' in navigator)) return;

    window.addEventListener('load', function () {
        navigator.serviceWorker.register('/service-worker.js', { scope: '/' }).catch(function (err) {
            console.warn('Nie udało się zarejestrować service workera:', err);
        });
    });

    // Blazor (enhanced navigation) podmienia treść bez przeładowania – panel inicjujemy też po każdej nawigacji.
    // pwa.js ładuje się po blazor.web.js, więc obiekt Blazor już istnieje.
    document.addEventListener('DOMContentLoaded', initPushPanel);
    if (window.Blazor && Blazor.addEventListener) Blazor.addEventListener('enhancedload', initPushPanel);

    function initPushPanel() {
        var panel = document.querySelector('[data-push-panel]');
        if (!panel || panel.dataset.ready) return;
        panel.dataset.ready = '1';

        var status = panel.querySelector('[data-push-status]');
        var buttons = {
            enable: panel.querySelector('[data-push-action="enable"]'),
            disable: panel.querySelector('[data-push-action="disable"]'),
            test: panel.querySelector('[data-push-action="test"]'),
        };
        var token = (panel.querySelector('input[name="__RequestVerificationToken"]') || {}).value;

        function show(text, state) {
            status.textContent = text;
            buttons.enable.hidden = state !== 'off';
            buttons.disable.hidden = state !== 'on';
            buttons.test.hidden = state !== 'on';
        }

        function post(url, body) {
            return fetch(url, {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
                body: JSON.stringify(body || {}),
            }).then(function (r) {
                if (!r.ok) throw new Error('Serwer odpowiedział ' + r.status);
                return r.json();
            });
        }

        if (!('PushManager' in window) || !('Notification' in window)) {
            var ios = /iPhone|iPad/.test(navigator.userAgent);
            var standalone = window.navigator.standalone || window.matchMedia('(display-mode: standalone)').matches;
            show(ios && !standalone
                ? 'Na iPhonie powiadomienia działają tylko w aplikacji dodanej do ekranu początkowego. Dodaj ją (Udostępnij → Do ekranu początkowego) i otwórz tę stronę w niej.'
                : 'Ta przeglądarka nie obsługuje powiadomień push.', 'none');
            return;
        }
        if (Notification.permission === 'denied') {
            show('Powiadomienia są zablokowane w ustawieniach przeglądarki dla tej strony. Zmień to w ustawieniach, a potem odśwież stronę.', 'none');
            return;
        }

        navigator.serviceWorker.ready.then(function (registration) {
            return registration.pushManager.getSubscription().then(function (subscription) {
                show(subscription ? 'Powiadomienia są włączone na tym urządzeniu.' : 'Powiadomienia są wyłączone na tym urządzeniu.', subscription ? 'on' : 'off');

                buttons.enable.onclick = function () {
                    show('Włączam…', 'busy');
                    Notification.requestPermission().then(function (permission) {
                        if (permission !== 'granted') throw new Error('Nie udzielono zgody na powiadomienia.');
                        return fetch('/push/vapid-public-key', { credentials: 'same-origin' }).then(function (r) { return r.text(); });
                    }).then(function (key) {
                        return registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: base64UrlToBytes(key) });
                    }).then(function (sub) {
                        return post('/push/subscriptions', sub.toJSON());
                    }).then(function () {
                        location.reload();
                    }).catch(function (err) {
                        show('Nie udało się włączyć powiadomień: ' + err.message, 'off');
                    });
                };

                buttons.disable.onclick = function () {
                    show('Wyłączam…', 'busy');
                    registration.pushManager.getSubscription().then(function (sub) {
                        if (!sub) return null;
                        return post('/push/subscriptions/delete', { endpoint: sub.endpoint }).then(function () { return sub.unsubscribe(); });
                    }).then(function () {
                        location.reload();
                    }).catch(function (err) {
                        show('Nie udało się wyłączyć: ' + err.message, 'on');
                    });
                };

                buttons.test.onclick = function () {
                    post('/push/test').then(function (r) {
                        status.textContent = r.sent > 0 ? 'Wysłano powiadomienie testowe (' + r.sent + ' urządzeń).' : 'Nie dostarczono – spróbuj wyłączyć i włączyć powiadomienia ponownie.';
                    }).catch(function (err) { status.textContent = 'Błąd: ' + err.message; });
                };
            });
        });
    }

    function base64UrlToBytes(value) {
        var padding = '='.repeat((4 - value.length % 4) % 4);
        var raw = atob((value + padding).replace(/-/g, '+').replace(/_/g, '/'));
        var bytes = new Uint8Array(raw.length);
        for (var i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
        return bytes;
    }
})();
