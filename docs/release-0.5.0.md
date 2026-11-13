# World Map 0.5.0 — first public development release

The author's 31 October request is to commit, push and bump the version for first publication. The follow-up explicitly rejects 1.0 because development continues. The selected version is **0.5.0**, with public name **World Map** and unchanged mod ID `wmap`. The mid-distance renderer remains experimental.

Source branch: `release/0.5.0`; tag: `v0.5.0`. This branch contains the installed pre.15 fixes and the later Earth+ ordering patch. It is kept separate from `main`, respecting the prototype's earlier merge condition. The existing GitHub repository remains private. No ModDB upload or installation of 0.5.0 is part of this release preparation; the installed package remains pre.15 with the Earth+ asset revision.

## Included work

The atlas starts in Chart, follows the player and opens at a local zoom. Glaciers retain mountain contours and relief, including 512-high worlds. A carried/granted Jonas barometer unlocks worldwide weather and twelve monthly prevailing-wind layers in gameplay; calm regions no longer paint false belt stripes. Its inventory icon is upright. A blank map costs eight parchments around the ink-and-quill tool. Direct Earth+ categories, identical labels and the client-only ordering patch are retained.

The first publication also includes the existing chart/minimap, exploration, waypoints, prospecting filters, automatic markers, paper-map exchange, per-world settings and experimental distant/recorded terrain. It does not add a new terrain or network format. The manifest and atlas title use World Map; assembly version follows the release version. The runtime archive includes the existing LICENSE.

## Verification

Final publication Release build: **PASS, zero warnings/errors**, with assembly version **0.5.0.0**. Core suite: **107404 assertions PASS**. The focused production-shader GPU checks pass: ice contour changes 14.3%, calm-circle coverage 1.86% without stripes, 512 real boundary pixels and zero false distant belt pixels. **Native plain-world smoke PASS** on the final 0.5.0 package (`.local/smoke-plain-20261031-202018/WmapSmoke/report.txt`): loaded item/recipe checks, paper-map exchange, actual Chart/follow/player-center/local-zoom startup at four screen scales, permissions, cache/network behavior and production GPU shader checks. Sea was absent in that profile, so its provider-integration checks were skipped. The smoke helper has its seven existing CA1416 platform warnings; the production build has none. Package validation passes: every ZIP entry matches build output, LICENSE/world settings/Earth+ ordering are present, and all assets are byte-identical to the active pre.15 candidate after its Earth+ ordering update. Earlier pre.15 evidence is in [WM-D43](decisions.md#wm-d43-chart-startup-and-worldwide-barometer-050-pre15-31-october-2026): 107404 core assertions, Earth-backed production sampler/GPU checks and a native plain-world smoke including recipe, orientation and startup at four screen scales. The Earth+ ordering patch has also been confirmed in game by the author.

The plain-world native smoke did not include Sea, and current graphical/multiplayer acceptance of every combination remains pending. Automated checks are not a claim of complete in-game verification. Build target and checked game: Vintage Story 1.22.7; the manifest minimum 1.22.0 is unchanged.

## Reproduce

Build with `dotnet build -c Release`; run `dotnet run --project tests/WmapTests -c Release`. The repository uses the installed VS references prepared by `tools/Prepare-References.ps1`. `tests/PlaytestTests` also requires the read-only Earth source checkout through `EarthRoot`; its `--chart-wind` mode renders the embedded production chart/wind shaders. `tools/Run-Smoke.ps1 -Profile plain -SuiteRoot <suite-root> -Wait` starts only an isolated disposable server.

Runtime artifact: `release/wmap_0.5.0.zip`. Source artifact: `release/wmap_0.5.0-source.zip`, generated with `git archive` from the tagged commit, excluding local game assemblies, caches, saves and earlier binaries. Publication HTML, form information and selected logo are included in the source tree under `docs/moddb-*`.


Runtime ZIP SHA-256: `d011d4d13951718d1fafe23555de0e57256cb6d690aa3d3ec2397b2e011af801` (333088 bytes, 28 entries). The source archive is generated from the final tagged source after commit. Logs are retained locally under `.local/release-0.5.0-{core,gpu}.txt`.
