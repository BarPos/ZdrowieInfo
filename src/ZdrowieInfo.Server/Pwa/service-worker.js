// ZdrowieInfo – service worker. Wersja wstawiana przez serwer przy każdym żądaniu tego pliku.
const VERSION = '__APP_VERSION__';
const STATIC_CACHE = `zi-static-${VERSION}`;
const PAGES_CACHE = `zi-pages-${VERSION}`;

// Nigdy z cache: logowanie i konto, panel (interaktywny), API, MCP, obwód Blazor.
const BYPASS = ['/Account', '/panel', '/api', '/mcp', '/_blazor', '/healthz', '/service-worker.js'];
const STATIC = ['/_framework/', '/_content/', '/icons/', '/css/', '/js/', '/manifest.webmanifest'];

self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const keep = new Set([STATIC_CACHE, PAGES_CACHE]);
        for (const key of await caches.keys()) {
            if (key.startsWith('zi-') && !keep.has(key)) await caches.delete(key);
        }
        await self.clients.claim();
    })());
});

self.addEventListener('fetch', (event) => {
    const request = event.request;
    const url = new URL(request.url);
    if (url.origin !== self.location.origin) return;

    // Wylogowanie: usuń zapisane strony, żeby nie dało się ich czytać offline po wylogowaniu.
    if (request.method === 'POST' && url.pathname === '/Account/Logout') {
        event.waitUntil(caches.delete(PAGES_CACHE));
        return;
    }
    if (request.method !== 'GET') return;
    if (BYPASS.some((p) => url.pathname.startsWith(p))) return;

    if (STATIC.some((p) => url.pathname.startsWith(p))) {
        event.respondWith(cacheFirst(request));
        return;
    }
    const accept = request.headers.get('accept') || '';
    if (request.mode === 'navigate' || accept.includes('text/html')) {
        event.respondWith(networkFirst(request));
    }
});

async function cacheFirst(request) {
    const cache = await caches.open(STATIC_CACHE);
    const cached = await cache.match(request);
    if (cached) return cached;
    const response = await fetch(request);
    // Tylko prawdziwy zasób: bez przekierowań (np. na logowanie) i bez stron HTML pod adresem pliku.
    const type = response.headers.get('content-type') || '';
    if (response.ok && !response.redirected && !type.startsWith('text/html')) cache.put(request, response.clone());
    return response;
}

async function networkFirst(request) {
    const cache = await caches.open(PAGES_CACHE);
    try {
        const response = await fetch(request);
        if (response.ok && !response.redirected) cache.put(request, response.clone());
        return response;
    } catch (err) {
        const cached = await cache.match(request, { ignoreVary: true });
        if (cached) return cached;
        return new Response(
            '<!doctype html><html lang="pl"><meta charset="utf-8"><meta name="viewport" content="width=device-width">' +
            '<title>Brak połączenia</title><body style="font-family:system-ui;padding:2rem">' +
            '<h1>Brak połączenia</h1><p>Ta strona nie była wcześniej otwierana, więc nie ma jej w pamięci telefonu.</p></body></html>',
            { status: 503, headers: { 'Content-Type': 'text/html; charset=utf-8' } });
    }
}

// Powiadomienia push (wysyłka z serwera – etap 4).
self.addEventListener('push', (event) => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch { data = { body: event.data && event.data.text() }; }
    event.waitUntil(self.registration.showNotification(data.title || 'ZdrowieInfo', {
        body: data.body || '',
        icon: '/icons/icon-192.png',
        badge: '/icons/icon-192.png',
        tag: data.tag,
        data: { url: data.url || '/' },
    }));
});

self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    const target = new URL((event.notification.data && event.notification.data.url) || '/', self.location.origin).href;
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const client of windows) {
            if ('focus' in client) {
                await client.focus();
                if ('navigate' in client) await client.navigate(target);
                return;
            }
        }
        await self.clients.openWindow(target);
    })());
});
