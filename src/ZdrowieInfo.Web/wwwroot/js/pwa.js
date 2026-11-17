// Rejestracja service workera (PWA). Zakres "/" – plik serwowany z katalogu głównego.
(function () {
    if (!('serviceWorker' in navigator)) return;
    window.addEventListener('load', function () {
        navigator.serviceWorker.register('/service-worker.js', { scope: '/' }).catch(function (err) {
            console.warn('Nie udało się zarejestrować service workera:', err);
        });
    });
})();
