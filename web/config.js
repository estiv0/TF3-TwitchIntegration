// Website configuration: where to get the data and how often to check for changes.
//
// Settings of a specific streamer (name, logo, channel, data address) are in site.json next to
// index.html - the file isn't in the repository, template: site.example.json. Without site.json the website works
// with the default name, no logo and no stream.
//
// Data address (the first that matches):
//   1. a parameter in the page address, for testing:
//        http://localhost:5080/?data=https://<account>.blob.core.windows.net/live/latest/
//   2. locally (localhost): data/latest/ (Tf3Bridge publishes here)
//   3. dataUrl from site.json
//   4. a site hosted as a Storage account "Static website" (https://<account>.z<N>.web.core.windows.net):
//        the "live" container of the same account
//   5. data/latest/ next to the site
const site = await fetch("site.json", { cache: "no-cache" })
  .then((r) => (r.ok ? r.json() : {}))
  .catch(() => ({}));

const dataParam = new URLSearchParams(location.search).get("data");
const isLocal = ["localhost", "127.0.0.1", "[::1]", ""].includes(location.hostname);
const storageSite = location.hostname.match(/^([a-z0-9]{3,24})\.z\d+\.web\.core\.windows\.net$/);

function dataBaseUrl() {
  if (dataParam) return dataParam.replace(/\/?$/, "/");
  if (isLocal) return "data/latest/";
  if (site.dataUrl) return site.dataUrl.replace(/\/?$/, "/");
  if (storageSite) return `https://${storageSite[1]}.blob.core.windows.net/live/latest/`;
  return "data/latest/";
}

export const config = {
  dataBaseUrl: dataBaseUrl(),
  pollMs: 5000,
  // meta.updatedAtUtc older than this = the game is paused or Tf3Bridge isn't running
  staleAfterMs: 30000,
  // Name in the top bar and the tab title
  title: site.title || "TF3 Live",
  // Logo before the name (image address relative to the page, e.g. "img/logo.png"); empty = no logo
  logo: site.logo || "",
  // The streamer's channel ("https://www.twitch.tv/<channel>"): the stream button in the top bar with a mini player
  // (js/stream.js) and a link in the build window (js/build.js); empty = no link and no player.
  twitchUrl: site.twitchUrl || "",
};
