// Konfiguracja strony: skąd brać dane i jak często sprawdzać zmiany.
//
// Adres danych (pierwsze pasujące):
//   1. parametr w adresie strony, do testów:
//        http://localhost:5080/?data=https://<konto>.blob.core.windows.net/live/latest/
//   2. strona hostowana jako "Static website" konta Storage (https://<konto>.z<N>.web.core.windows.net):
//        dane z kontenera "live" tego samego konta, https://<konto>.blob.core.windows.net/live/latest/
//   3. lokalnie: data/latest/ (tu publikuje Tf3Bridge)
const dataParam = new URLSearchParams(location.search).get("data");
const storageSite = location.hostname.match(/^([a-z0-9]{3,24})\.z\d+\.web\.core\.windows\.net$/);

function dataBaseUrl() {
  if (dataParam) return dataParam.replace(/\/?$/, "/");
  if (storageSite) return `https://${storageSite[1]}.blob.core.windows.net/live/latest/`;
  return "data/latest/";
}

export const config = {
  dataBaseUrl: dataBaseUrl(),
  pollMs: 5000,
  // meta.updatedAtUtc starsze niż to = gra wstrzymana albo Tf3Bridge nie działa
  staleAfterMs: 30000,
};
