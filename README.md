# Jellyfin ČSFD plugin

Metadata provider pre **Jellyfin 12.x**: slovenské názvy a popisy, hlavné hodnotenie ČSFD, dátumy premiér (SK → CZ), žánre, sezóny a epizódy. Obrázky a herci zostávajú z TMDb (hybrid), ČSFD ich iba dopĺňa.

```
Jellyfin ──► Jellyfin.Plugin.Csfd ──► csfd-api (Docker, NAS :3080) ──► ČSFD     (metadáta)
                     └──────────────────────────────────────────────► csfd.sk/cz (TV tipy, rebríčky, zaujímavosti, Chcem vidieť, hodnotenie)
```

**Metadáta** (názvy, popisy, hodnotenie, premiéry, epizódy) parsuje [node-csfd-api](https://github.com/bartholomej/node-csfd-api) v kontajneri; plugin k nim pridáva cache a párovanie.
**TV tipy, rebríčky, zaujímavosti, „Chcem vidieť“ a hodnotenie filmov** sidecar nemá – tieto stránky plugin sťahuje z csfd.sk/csfd.cz sám a výzvu Anubis (proof-of-work) rieši tiež sám.

## 1. Sidecar csfd-api (Portainer)

1. Portainer → **Stacks → Add stack** → názov `csfd-api` → vlož `deploy/csfd-api/docker-compose.yml`.
2. **Environment variables**: `CSFD_API_KEY` = náhodný reťazec (`openssl rand -hex 24`).
3. Deploy, potom test z PC:
   ```powershell
   curl.exe -H "x-api-key: KLUC" "http://192.168.1.201:3080/movie/8852?language=sk"
   ```
   Prvý request trvá dlhšie (rieši sa proof-of-work).

## 2. Inštalácia pluginu

**Cez katalóg (odporúčané):** Dashboard → Plugins → **Repositories** → `+` →
`https://raw.githubusercontent.com/Yossarian222/jellyfin-plugin-csfd/main/manifest.json` → Catalog → **ČSFD** → Install → reštart Jellyfinu.

**Ručne:** rozbaľ `csfd_x.y.z.w.zip` z Releases do `/config/plugins/Csfd_x.y.z.w/` v Jellyfin kontajneri a reštartuj.

## 3. Nastavenie

Dashboard → Plugins → **ČSFD**: URL `http://192.168.1.201:3080`, API kľúč → Uložiť → **Otestovať spojenie**.

V každej knižnici (Filmy, Seriály, Dokumenty) → *Manage library*:

| Nastavenie | Hodnota |
|---|---|
| Preferred metadata language | Slovak |
| Country | Slovakia |
| Metadata downloaders (filmy, seriály, sezóny, epizódy) | **ČSFD**, TheMovieDb, The Open Movie Database |
| Image fetchers | **TheMovieDb**, ČSFD |

Potom *Scan → Replace all metadata* (obrázky nemusíš nahrádzať).

### Odkiaľ sa berie čo

| Pole | Zdroj |
|---|---|
| Názov | ČSFD SK názov; ak SK neexistuje, ostane TMDb |
| Popis | ČSFD SK → ČSFD EN → TMDb/OMDb (CZ len ak zapneš) |
| Hodnotenie | ČSFD % ako CommunityRating (90 % → 9.0), voliteľne aj CriticRating |
| Premiéra | SK kino → SK → CZ kino → CZ → najskoršia |
| Žánre, krajiny, tagy | ČSFD |
| Plagáty, pozadia, logá, herci s fotkami | TMDb (ČSFD len ako záloha) |
| Epizódy | SK názov a popis z ČSFD, ak existujú; inak TMDb |

### Ručné priradenie

*Identify* → do poľa názvu vlož ČSFD URL (`https://www.csfd.sk/film/8852-…/`), `csfd:8852` alebo len `8852`.

### Môj ČSFD účet

V nastaveniach pluginu sa dá zadať odkaz na ČSFD profil (čítanie vlastných hodnotení) a prezývka + heslo (hodnotenie filmov z klienta).
Heslo je uložené v konfigurácii pluginu na serveri; konfiguráciu pluginu vidia a menia len administrátori Jellyfinu.
Voľba **Hodnotiť môžu všetci používatelia** (predvolene zapnutá) – po vypnutí môžu hodnotiť len administrátori (napr. kvôli deťom a hosťom).

## Endpointy pre klientov

Volajú ich klienti (Wholphinix, Nasflix) s tokenom prihláseného používateľa Jellyfinu (`Authorization: MediaBrowser Token=…`).

| Endpoint | Kto | Čo vráti |
|---|---|---|
| `GET /Csfd/Watchlist?limit=20&missing=10` | ktorýkoľvek používateľ | „Chcem vidieť“ z ČSFD profilu v nastaveniach (csfd.sk, záloha csfd.cz; súkromný zoznam cez prihlásený účet): najprv tituly v jeho knižnici v poradí z ČSFD, potom `missing` chýbajúcich s detailmi pre Seerr; rovnaký formát ako TvTips, cache 3 h |
| `GET /Csfd/TvTips?day=0&limit=10&missing=0` | ktorýkoľvek používateľ | TV tipy dňa (`day` −1…7) zúžené na jeho knižnicu, voliteľne `missing` najlepších chýbajúcich; admin alebo API kľúč môže pridať `userId` (knižnica iného používateľa, napr. pre MCP server s API kľúčom) |
| `GET /Csfd/Ranks` | ktorýkoľvek používateľ | ČSFD ID → pozícia v rebríčkoch najlepších filmov/seriálov |
| `GET /Csfd/MyRatings` | ktorýkoľvek používateľ | ČSFD ID → hviezdy (0 = odpad, 1–5) z profilu v nastaveniach |
| `POST /Csfd/MyRatings/{csfdId}?stars=0..5` | používateľ, ak je povolené hodnotenie pre všetkých; inak len admin | `{ ok, message }`; hodnotí účtom z nastavení, najviac 1 hodnotenie za sekundu (inak 429) |
| `GET /Csfd/Trivia/{csfdId}?limit=4` | ktorýkoľvek používateľ | zaujímavosti k titulu (bez spoilerov) |

Endpointy pod `/Plugins/Csfd/…` (test spojenia, test prihlásenia, vymazanie cache) sú len pre administrátorov.

## Výkon

Každý titul = 1 request (+1 ak chýba SK popis), epizódy 1 request na kus + 1 na sezónu. Pri 2,5 s rozostupe ~1 400 requestov/h. Výsledky sú v cache (predvolene 30 dní) v `/config/plugins/Jellyfin.Plugin.Csfd/cache/`; vymazať sa dá tlačidlom v nastaveniach pluginu.

**Nočné prednačítanie** – naplánovaná úloha „ČSFD: nočné prednačítanie“ (kategória ČSFD, predvolene denne o 3:00, dá sa spustiť ručne v Ovládací panel → Naplánované úlohy) obnoví TV tipy na dnes a zajtra, „Chcem vidieť“ a zaujímavosti všetkých filmov/seriálov s ČSFD ID, ktorým cache chýba alebo vyprší do 3 dní. Rozostup medzi titulmi `PrefetchDelayMs` (predvolene 3000 ms, minimum 1000), vypnúť sa dá `PrefetchEnabled`.

## Nasflix

Nasflix číta z Jellyfin API `ProviderIds.Csfd` (odkaz/ID), `CommunityRating × 10` = ČSFD %, `Overview` (SK) a `PremiereDate`.

## Build

GitHub Actions: push = test + build. Nové vydanie: **Actions → build → Run workflow**, zadaj verziu (napr. `1.0.1`) → vytvorí sa release, tag a aktualizuje `manifest.json`. Jellyfin potom ponúkne update v katalógu.
Lokálne: `dotnet test Jellyfin.Plugin.Csfd.Tests && dotnet publish Jellyfin.Plugin.Csfd -c Release`.
