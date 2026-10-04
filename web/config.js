// Konfiguracja strony: skąd brać dane i jak często sprawdzać zmiany.
//
// Adres danych (pierwsze pasujące):
//   1. parametr w adresie strony, do testów:
//        http://localhost:5080/?data=https://<konto>.blob.core.windows.net/live/latest/
//   2. lokalnie (localhost): data/latest/ (tu publikuje Tf3Bridge)
//   3. strona hostowana jako "Static website" konta Storage (https://<konto>.z<N>.web.core.windows.net):
//        kontener "live" tego samego konta
//   4. każdy inny adres (Static Web Apps, własna domena): AZURE_DATA_URL
const AZURE_DATA_URL = "https://tf3liveblob.blob.core.windows.net/live/latest/";

const dataParam = new URLSearchParams(location.search).get("data");
const isLocal = ["localhost", "127.0.0.1", "[::1]", ""].includes(location.hostname);
const storageSite = location.hostname.match(/^([a-z0-9]{3,24})\.z\d+\.web\.core\.windows\.net$/);

function dataBaseUrl() {
  if (dataParam) return dataParam.replace(/\/?$/, "/");
  if (isLocal) return "data/latest/";
  if (storageSite) return `https://${storageSite[1]}.blob.core.windows.net/live/latest/`;
  return AZURE_DATA_URL;
}

export const config = {
  dataBaseUrl: dataBaseUrl(),
  pollMs: 5000,
  // meta.updatedAtUtc starsze niż to = gra wstrzymana albo Tf3Bridge nie działa
  staleAfterMs: 30000,
  // Kanał streamera (np. "https://www.twitch.tv/<kanał>"): link w okienku budowy (js/build.js); pusty = bez linku.
  twitchUrl: "",
};
