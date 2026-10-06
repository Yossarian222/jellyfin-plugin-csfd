# Jellyfin ČSFD plugin

Metadata provider pre **Jellyfin 12.x**: slovenské názvy a popisy, hlavné hodnotenie ČSFD, dátumy premiér (SK → CZ), žánre, sezóny a epizódy. Obrázky a herci zostávajú z TMDb (hybrid), ČSFD ich iba dopĺňa.

```
Jellyfin ──► Jellyfin.Plugin.Csfd ──► csfd-api (Docker, NAS :3080) ──► ČSFD (Anubis PoW, throttling)
```

Parsovanie ČSFD a riešenie Anubis výzvy robí [node-csfd-api](https://github.com/bartholomej/node-csfd-api) v kontajneri. Plugin je tenký klient s cache a párovaním.

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
`https://raw.githubusercontent.com/<github-user>/jellyfin-plugin-csfd/main/manifest.json` → Catalog → **ČSFD** → Install → reštart Jellyfinu.

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

## Výkon

Každý titul = 1 request (+1 ak chýba SK popis), epizódy 1 request na kus + 1 na sezónu. Pri 2,5 s rozostupe ~1 400 requestov/h. Výsledky sú v cache (predvolene 30 dní) v `/config/plugins/Jellyfin.Plugin.Csfd/cache/`; vymazať sa dá tlačidlom v nastaveniach pluginu.

## Nasflix

Nasflix číta z Jellyfin API `ProviderIds.Csfd` (odkaz/ID), `CommunityRating × 10` = ČSFD %, `Overview` (SK) a `PremiereDate`.

## Build

GitHub Actions: push = test + build, tag `v1.0.0` = release + aktualizácia `manifest.json`.
Lokálne: `dotnet test Jellyfin.Plugin.Csfd.Tests && dotnet publish Jellyfin.Plugin.Csfd -c Release`.
