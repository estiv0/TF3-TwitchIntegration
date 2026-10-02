// Konfiguracja strony: skąd brać dane i jak często sprawdzać zmiany.
// Lokalnie: Tf3Bridge (Development) publikuje do web/data/latest/.
// Na Azure: URL kontenera Blob Storage, np. "https://<konto>.blob.core.windows.net/live/latest/".
export const config = {
  dataBaseUrl: "data/latest/",
  pollMs: 5000,
  // meta.updatedAtUtc starsze niż to = gra wstrzymana albo Tf3Bridge nie działa
  staleAfterMs: 30000,
};
