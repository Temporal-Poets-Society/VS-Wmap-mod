# 0.5.3-rc.3 (13 November 2026, test build, not released)

- **The coordinates box moved to the top left corner and wears the game's own box colour** (WM-0015). The box with latitude, longitude, local time and heading sat under the game's block coordinates at the top right, on a lighter see-through black that was hard to read over bright ground. It now sits in the top left corner, 10 GUI units from both edges as the game's own boxes are, or under the minimap if that is in the top left. Its background is drawn exactly like the game's coordinates box (Ctrl+V): the dialog brown `#403529` at 80 %, the same shaded edge and padding, and the same way of drawing it, so both boxes come out the same colour. The game's intro tip ("Need help figuring out the game?") slides in below it. With the debug screen open, the system info there overlaps the box.
- **The coordinates box shows the world's time beside the local time** (WM-0016). On Earth and other worlds with longitude it reads `56° 57.0′ N  24° 06.6′ E   17:36 local` over `Heading 270° W   16:00 UTC`. UTC is the world's one universal clock, the solar time at longitude 0: Astra Terra for Earth's universal time (total days × hours per day), which every local time is moved from by the longitude's share of a day, and the game's own hour of day. Where the world has one clock (no longitude, or Astra Terra keeping one sun for the whole world) the box shows the hour once, as before (`Time 16:00` on worlds without geography). The two clocks sit after the coordinates and the heading so the box keeps two lines and stays above the game's intro tip. Minutes are cut, as on the character panel; a hair below the minute no longer reads as the minute before.

Checked: Release build without warnings; 108,789 core assertions; play-test regression checks. On the AgentBridge bench (Earth 2.0.0-rc.11, Cylinder World 1.1.0-rc.4, Hydrology 0.6.2-rc.6, Sea 0.6.0-rc.2, Astra Terra for Earth 0.15.0-dev.25), Riga after `/time set 16:00`: `17:36 local`, `15:59 UTC` (vanilla's `/time set` works in float and lands a few seconds short of the hour; the local time is the same instant plus 1 h 36.4 min); after `/time set 22:30`, `00:06 local`, `22:29 UTC`. A world with World Map alone showed `Time 16:00`. Box move (WM-0015): in the Sahara at noon and at night: over the same sand both boxes read (90, 80, 62) and (90, 79, 61) inside, with matching (78, 69, 54) edges; the box stays clear of the intro tip. Not checked: other GUI scales, a minimap moved to the top left.

# 0.5.3-rc.2 (13 November 2026, test build, not released)

- **The Jonas barometer reads Earth's air pressure** (WM-0006). On an Earth world the atlas's position line adds "Barometer: 1015 hPa, falling": the sea-level pressure where you stand from Earth's weather model and its three-hour tendency (steady within 1 hPa). The weather-now wind reading at the cursor adds the pressure there. Shown wherever the winds are (creative, a carried barometer or another mod's wind-sight grant); without Earth nothing changes. Read by reflection (`EarthMap.EarthWeather.Model.Sample`), so no new dependency.
- **Ore heat maps no longer read 0 where Earth keeps vanilla strata** (WM-0011). With Earth's regional ores (world option `earthOreDeposits: maps`) the map asked Earth's generators without a rock column, so wherever Earth leaves vanilla's rock strata in a column (its `RegionalRock` is 0 above the rock floor: under ice sheets, outside its geology) Earth found no host rock there. The pick hands Earth vanilla's simulated column in that case; the map now makes the same column with a prospecting workspace of its own where the chunk is loaded, and leaves such a cell unknown (asked again later) where it is not. Tiles stored before are not reused (cache key v2). Worlds on vanilla's ore maps, which is Earth 2.0's default ore choice, are unaffected.
- **`/wmapore [ore]`** (server, players with the full map): the ore heat map's answer at your column beside the prospecting pick's own reading there, with where the column's rock came from.
- Docs: the README, the 0.5.0 changelog entry and the decision register no longer say the release and the mid-distance prototype are kept apart from `main` (WM-0009). The Mod DB draft is updated for the next release, with gallery candidates (WM-0012).

Checked: Release build without warnings; 108,785 core assertions; play-test regression checks against Earth's sources. On the AgentBridge bench (Earth 2.0.0-rc.9, Cylinder World 1.1.0-rc.3, Hydrology 0.6.2-rc.6, Sea 0.6.0-rc.1, Astra Terra for Earth 0.15.0-dev.25): the barometer read 1015 hPa at Riga, the same as `/earthweather`. In a regional-ores world, `/wmapore` matched the pick for all 79 ores at Riga (Earth's rock) and on the Greenland ice sheet, where the whole column keeps vanilla strata (31 ores non-zero). Not checked: cells in unloaded vanilla-strata areas on the atlas in play, and multiplayer.

# 0.5.3-rc.1 (13 November 2026, test build, not released)

0.5.2 plus the local time in the coordinates box (WM-0010).

- **The coordinates box shows the local time.** After the latitude and longitude it gives the hour as HH:MM, for example `56° 57.0′ N  24° 06.6′ E   17:36` at Riga. On a world without geography the box now shows just `Time 16:00` (until now it showed nothing there).
  - With **Astra Terra for Earth** (or the original Astra Terra) installed, it is Astra Terra's local solar time: World Map asks Astra Terra for the longitude its sky turns by (`ObserverLongitude.ForObserver`) and applies Astra Terra's formula, so noon on the clock is when its sun stands highest. It agrees with the hour Astra Terra puts on the character panel when that panel shows local solar time, Astra Terra's default. If a player set Astra Terra's `CalendarDisplay` to keep the hour off the character panel, the box leaves it out too.
  - Without Astra Terra: on an Earth world, the solar time at Earth's sky longitude (`LongitudeAt`); on a Cylinder World planet, at the map's longitude; elsewhere the game's hour of day. Without Astra Terra the game's sun keeps one time for the whole world, so on Earth this is the hour the sun would show at that longitude, not the one in the sky.
  - The new client setting `CoordinatesHudTime` (default `true`) hides the time; Ctrl+Shift+V still hides the whole box.
- The offline play-test checks build again against Earth's current sources, which now use `System.Uri`.

Checked: Release build without warnings; 108,785 core assertions (13 new for the clock); play-test regression checks pass against Earth 2.0's sources. On the AgentBridge bench, in a new Earth 1:100 world (Earth 2.0.0-rc.8, Cylinder World 1.1.0-rc.3, Hydrology 0.6.2-rc.6, Astra Terra for Earth 0.15.0-dev.25) with the server clock stopped at 16:00: Riga showed 17:36, with the sun low in the west, and San Francisco 07:50, with the sun in the east. Both matched the hour on Astra Terra's character panel. In a vanilla world (World Map alone) the box showed `Time 16:00`, the same as `/time`, and with `CoordinatesHudTime: false` there was no box. Not checked: multiplayer, a Cylinder World without Earth, and Astra Terra's `CalendarDisplay` set to hide the hour.

No terrain, world format, item ID or network format changes from 0.5.2.

# 0.5.2 (5 November 2026, test build, not released)

This test build joins two branches from 0.5.1: `claude/climate-builtin-heights` (climate raster, river lines) and `claude/ore-heatmaps` (0.5.2-ore.1, ore heat maps).

- **A new Earth world on full GLO-90 heights no longer waits about two minutes for the climate raster.** The raster behind the atlas's Terrain colours samples Earth's forest and climate across the whole globe, once per world. With Earth's GLO-90 land heights (Earth 1.0.3, decision 0051) and the whole tile store on the machine, each sample's forest read the column's own GLO-90 height, so the raster decoded tiles all over the globe: 116.0 s in the author's new 1:10 world (5 November). It now asks Earth for its built-in heights (`FineElevation.BuiltInOnly`) around each of its samples and puts the old value back after each one. Offline, the same raster on Earth 1.0.3-tall.8 with the whole store took 6.8 s and read no GLO-90 tiles; without the switch it took 87.9 s and read 70,817. Far terrain's forest and climate tiles near the player still read GLO-90, so nearby terrain is unchanged. In the raster, forest differs in 1.5 % of texels (alpine tree lines), temperature and rain nowhere. The server log says "on Earth's built-in heights" when the switch is used. Earth before 1.0.3 and worlds without Earth are unaffected. A raster already cached is kept. See WM-D45.
- **Big rivers no longer get a second, darker river beside them on the atlas and minimap.** Reported on the Niagara in the author's 1:10 world at 3.7 blocks per pixel: a dark band, straight-edged where your recorded terrain cut it off, lay round the river. Detail tiles coarser than one block drew river lines from the world chart's river channel. That channel is interpolated from texels 489 blocks wide at 1:10, so a big river's channel became a lens up to a texel long round the tile's own river. Every detail tile now draws rivers from its own water only, as one-block tiles have since 0.4.3. The world chart itself still draws its river lines when zoomed out, and while detail fades in. Offline renders of the Niagara (Earth 1.0.3-tall.8 at the author's geometry, the production shader) change only in the lens, about 16,000 pixels, in both styles; nothing else in the reported view changes. A new GPU check catches the old shader (20,499 and 27,000 river-blue pixels on land) and passes with the new one (0). A river narrower than a tile's texels now shows only when zoomed in far enough for the tile to hold it. Another patch in the same report, a straight shore with a dark band on Lake Erie, is how the world chart looks where a detail tile has not arrived yet (8 were pending). See WM-D46.
- **The atlas's grid labels follow the ° / °′ button.** With decimal degrees chosen, the readouts said 169.4513° W while the lines were labelled in degrees and minutes, so the line labelled 169°45′W, 0.3° west of the player, looked like the player's own longitude (reported on St Lawrence Island). The positions always agreed; the labels now read 169.75°W, 169.5°W, 169.25°W with decimal degrees and keep 169°45′W with minutes.

**Ore heat maps.** The author asked for ore heat maps in Creative, to see each ore's spawn and to debug "feels like little ore".

- **Ore heat** on the Creative map (creative mode, the `wmap` privilege or `AllowAllPlayers`): a menu picks one ore, or "ores the pick lists". The map shows it as a translucent colour layer under the labels, with a legend at the top right; the line under the map gives the cell under the cursor in blocks per chunk and the pick's density word and factor.
- **The heat map follows every zoom and is stored (6 November).** The first version asked for one grid over the view and drew nothing new until that grid was finished. The author found it "only renders once selected in given zoom, then it does not update", and wanted maps worked out ahead and stored so any zoom shows one. The map now comes in tiles, the atlas's own: 32 × 32 cells each, one chunk per cell when zoomed in, up to one tile for the whole world. The server works a tile out for every ore at once and stores it in the world's cache folder beside the map tiles (under the same disk limit), so a tile is worked out once per world and mod set, and switching ores over the same view is instant. The first request on an Earth world also starts storing the coarse levels of the whole world ahead of time (up to 1,000 tiles, two background threads below normal priority). Each zoom shows the tiles it has at once; a missing tile is drawn from the nearest coarser one, in its larger cells, until it arrives, and the status line counts the tiles still being worked out.
- Earth worlds answer by the generators' own prospecting reading, anywhere on the planet, without generating chunks. Vanilla worlds answer from the loaded regions' ore maps (an upper bound: host rock is not counted); their tiles are not stored while a region is missing and are asked for again after 20 s. "Ores the pick lists" is offered only on Earth.
- Never on the game chart. Each player's newest request replaces the tiles of the one before; the server serves players in turn, a tile at a time, on up to four threads off the main thread.
- Protocol: `AtlasInfo` gains the offered ore codes and the method (fields 40, 41), and two message types are appended (`OreHeatRequest`, `OreHeatResponse`) to the `wmap-v2` channel. Client and server need this same version, as for any network change.

No terrain, world format or item ID changes. The ore heat maps change the network format (above), so client and server need this same version. Decisions: WM-D45 (climate raster), WM-D46 (rivers), [WM-D47](docs/decisions.md#wm-d47-ore-heat-maps-on-the-creative-map-052-ore1-5-november-2026) (ore heat maps), [WM-D48](docs/decisions.md#wm-d48-ore-heat-maps-in-stored-tiles-at-every-zoom-052-6-november-2026) (stored tiles at every zoom).

# 0.5.1 (1 November 2026)

- Blank maps can be crafted in survival again: the ink and quill is now a kept ingredient (`consume: false`) instead of a tool. It has no durability, so a tool ingredient's durability cost blocked the recipe. The recipe is otherwise unchanged (eight parchments around the ink and quill, one map), and the native smoke check now requires the quill to be kept at no durability cost.
- Without Earth, the Customize tab that holds Wmap's two world options is labelled "Earth" instead of the raw key `worldconfig-category-earth`.
- Manifest author is "Andris", as in the other suite mods.

No terrain, world format, item ID or network format changes.

# 0.5.0 (31 October 2026) — first public development release

World Map's first publication uses a 0.x version because development continues. This packages the current pre.15 fixes and the shared Earth+ tab ordering; the mid-distance renderer remains experimental. The internal mod ID stays `wmap`. The manifest, atlas title and documentation now use the public name **World Map**, and the runtime archive includes the existing GPL-3.0 license. No terrain, world format, item ID or network format changes for publication.

Includes Chart/follow/player-zoom startup, glacier contours in tall worlds, worldwide barometer weather and monthly prevailing winds, calm-wind stripe and inventory-icon fixes, and eight-parchment blank maps. Earth+ appears below Everything with the direct declarations in each suite mod.

Release source is on `release/0.5.0`, kept separate from `main` under the prototype's existing merge condition (since 12 November 2026 that line is `main` itself, and versions are tags). See [release details](docs/release-0.5.0.md). The earlier pre-release sections below are development history.

# 0.5.0-pre.15 (31 October 2026)

- Chart starts centered on and following the player at 2 blocks per GUI pixel, including with an older saved Terrain preference. The Whole world, pan, zoom and style controls remain available.
- Glaciers retain land contours and relief under their pale tint and ice hatching, so snowy peaks no longer look cut flat. Keeps pre.11's full-height encoding for 512-high worlds.
- A Jonas barometer now grants worldwide weather and prevailing winds for each month, in gameplay atlas and minimap; player and cursor readings use the selected layer. No instrument still means no gameplay winds. Legacy range field retained.
- Fix blue vertical stripes in calm wind regions: a zero field is no longer treated as a belt boundary. Real wind sign changes retain dashed boundaries.
- Turn the Jonas barometer upright in inventory (GUI transform only).
- Blank maps cost eight parchments around the ink-and-quill tool, for one map. Copying remains unchanged.

Verification and limits: WM-D43 in [decisions](docs/decisions.md).

# 0.5.0-pre.14 (31 October 2026)

Add per-world `wmapSharedChart` and `wmapExploreBySight` boolean switches under Customize > Earth, both on by default for new worlds and editable after creation (restart to apply). The server applies each saved value before starting the atlas and uses the existing `AtlasInfo` sync for chart access and sight exploration. Missing keys retain the corresponding `wmap.json` setting; no old-world migration or global-config rewrite. Add offline fallback/override and declaration/localization checks. Build, executable tests and in-game verification are pending the coordinator. See WM-D42.

# 0.5.0-pre.13 (play-test fix, not released)

On `claude/wmap-zoom-detail-caa73b` from pre.12: zooming in over ground you have explored shows block detail again (#36). pre.12 drew the four-block recordings over the one-block prediction at every zoom, so explored ground stayed in 4-block steps. A finer prediction now draws over a coarser recording wherever the recorded samples confirm it, cell by cell; where a recording contradicts it (an old chunk without a newly predicted river, a recorded lake) the recording still shows. See [WM-D41](docs/decisions.md#wm-d41-play-test-36-zoom-detail-over-recorded-ground-050-pre13-31-october-2026).

# 0.5.0-pre.12 (play-test fixes, not released)

On `codex/playtest-issues-2026-10-30`: natural fine-recorded cliffs and glaciers no longer become structure pillars; snow-covered foliage records the ground, and incomplete canopy scans retry without recording air. Structure evidence is stored separately from fine-recording coverage, preserving older colours, lights and exploration.

Earth pre.8's RGI glacier surfaces and frozen fresh-water tops are now included in predictions, removing the reproduced glacier omission and isolated recorded glacier patches. Recorded terrain and water now overrides every prediction at every zoom, including cached fallbacks: an existing dry chunk no longer shows a newer predicted river through finer tiles or zoom fades. This handles either old or pre.8 chunks without changing river widths. Core regressions pass; final build/GPU/smoke verification is pending the coordinator. See [WM-D40](docs/decisions.md#wm-d40-play-test-19-and-22-surface-recording-050-pre12-30-october-2026) for evidence and limits.

# 0.5.0-pre.11 (fix branch, not released)

Branch `fix/height-range-2026-10-27` from `fix/far-sea-coast-2026-10-26` (pre.10), for Earth's taller worlds (512 blocks, sea level 110): the map drew the Tibetan plateau as one flat sheet and cut the Himalayas.

**Heights over the whole world.** The chart and tiles store a height relative to the sea in one byte over a fixed 256 blocks, so everything higher than 256 above the sea (Y 366 from sea level 110) was stored as 256: no relief, contours or slope above it, on the atlas, the minimap and far terrain, and line of sight read it as 255. The range is now the world's own, `Semantic.HeightRangeFor`: 256, or the height from the sea surface to the top of a taller world (403 for 512 and 110). The server sends it in `AtlasInfo.HeightRange` (0 from older servers, which used 256); the map shader takes it as `uHeightRange`, far terrain in `wmapChartInfo.w` as before, and line of sight keeps heights as 16-bit. Near the top a step of the curve is about 6 blocks instead of 4. Worlds up to 256 above the sea keep 256, and so their charts and tiles byte for byte; a taller world's cache identity names its range, so the charts and tiles made before with the cut are made again.

**No far sea up steep coasts.** On that world the far sea climbed the cliffs above Nice, up to 9 blocks over the water (pre.10 too: Earth 0.17's relief makes the coast steeper). Filtering the chart and the near band blends a sea texel's bed with the cliff texel beside it, so where the filtered coast distance still said water the filtered height was already several blocks up, and water was drawn up to it as if standing above the sea, like a lake. Water is now drawn only up to the level of the water texels among the four the filter blends (`wmapWaterLevel`: the highest of them, at least the sea surface), so a lake keeps its own surface and the sea stays at the sea; the recordings keep their own heights.

**Smoke on a taller world.** The shore and Baikal checks pass on the 512-block world. The torch-behind-the-curve check now follows the geometry: hidden where the curved sea rises over the sight line, shown where the line clears it by more than 2 blocks (on the 512-block world the tower stands 92 blocks above the sea, more than the curve hides at 2900 blocks). The smoke's own far frames take the world's height range, and a tall world's chart must hold ground more than 264 blocks above the sea.


Branch `fix/far-sea-coast-2026-10-26` from `playtest/2026-10-26` (pre.9), for the Earth smoke failure "the far sea climbs the coast" (145 of 119,580 pixels up to 99.5 blocks above the sea, seen in pre.7 and pre.9 with Earth 0.16.14).

**No river specks on distant hills.** The failing pixels were not the far sea but the chart's river lines, which take the same water colour. Past the near band a mesh cell of 16 to 64 blocks smooths a narrow valley into a slope, and the line was painted on that slope, up to 57 blocks above its river: water-coloured specks in the coastal hills above Nice. Earth's newer river data (pool layouts 7 to 10) put rivers there; with Earth 0.16.10, pre.5 passed. A river line is now drawn only where the drawn surface lies within 2 to 6 blocks of the chart's finest ground there. The shore check now tells river lines (near Earth's river segments) from the sea and allows them at most 6 blocks above the valley floor; pre.9's shader fails it (135 of 143 river pixels, up to 57.3; [record](docs/test-results/shore-before-0.5.0-pre.10.txt)).

**No sky through the terraces' edge.** With the shore check passing, the mid-distance view's check found single pixels of sky on the 768-block ring where the terraces end and the smooth mesh begins (2 of 43,249 at view distance 256, at the coast above Nice): both were cut at the ring, and where the smooth mesh began higher than the last terrace nothing closed the step. They now overlap by 16 blocks (`FarFrame.JoinOverlap`), each sinking under the other toward its own cut by up to the land push-down (`wmapJoinPush`; the edge is passed in `wmapPush.w`, unused before). At the ring itself neither is lowered.

# 0.5.0-pre.9 (play-test, not released)

Play-test build of 26 October 2026 on branch `playtest/2026-10-26`: pre.7 with `fix/sunset-glint-flicker-2026-10-26` (pre.8, the far-sea glitter path, issue 14; `ad29e81`) and `fix/seam-graticule-2026-10-26` (pre.7-seam.1, no graticule band at the seam, issue 8; `ea055a4`) merged, with Sea 0.4.7-pre.7. The two touch different files apart from the version lines. Both are described below.

# 0.5.0-pre.8 (play-test, not released)

Play-test build of 26 October 2026 on branch `fix/sunset-glint-flicker-2026-10-26` (from `playtest/2026-10-26`, 0.5.0-pre.7), with Sea 0.4.7-pre.7, after issue 14 of the play-test: the sun's glitter path stopped in a calm, unlit grey band short of the horizon.

**The glitter path reaches the horizon.** The far sea draws the sun's glint and the moon's trail with Sea's own lobe (`wmapSeaGlints`, the same numbers as Sea's `seaGlints`): full while the body's centre is up, gone once its top has set, in the drawn sun's colour, dimmed by cloud. Where the far waves are too short for the pixel, the slope `wmapWaveNormal` fades out is kept as roughness that widens the lobe (the same light spread over it), as Sea's near water does, so the path stays wide toward the horizon. Sea passes the inputs (`seaGlint`, `seaSun`, `seaMoon`, `seaSunColor`, `seaWeather`); without Sea 0.4.7-pre.7 `seaGlint` is 0 and nothing changes.

Checked: core suite (107,160 assertions); Sea's ShaderCheck compiles `farterrain` and `farcells` with the new uniforms live and finds the far glint equal to the near water's on the GPU for the same inputs (largest difference 0.000000). Not run: the smoke profiles. Not seen in game.

# 0.5.0-pre.7-seam.1 (fix branch, not released)

Branch `fix/seam-graticule-2026-10-26` from `playtest/2026-10-26` (pre.7), for issue 8 of the 26 October play-test: a red-brown band down the whole chart at the jump line (169°22'W to 169°03'W at St. Lawrence Island).

**No graticule band at the seam.** It was the graticule's ink, not a zone. The geography texture's longitude jumps by 360 degrees where the ring's ends meet. Earth's gutter used to hide that texel (it is outside geography). Earth 0.16.14's real seam (Earth decision 0030) has no gutter, so the hardware's filter blended across the jump, and the graticule found a line in every pixel of that texel. The shader now filters the geography texture by hand, unwrapping each texel's longitude against its neighbour's, and takes the graticule's width from wrapped longitude steps. New GPU smoke check (`SeamGraticule`, Earth): the chart at the jump line with and without the graticule. With pre.7's shader 50.9 % of the pixels within a geography texel (200 blocks) of the jump line are inked, against 1.8 % and 1.9 % beside it, and the check fails. With the fix it is 2.7 % against 2.1 % and 2.2 % (taken from the run's renders: that run's GPU section stopped later at the near band's far-sea check, which fails the same with pre.7's shader).

# 0.5.0-pre.7 (play-test, not released)

Play-test build of 26 October 2026 on branch `playtest/2026-10-26`: pre.6 (`playtest/sunset-seam-2026-10-25`, `4afb2bb`) with `fix/far-land-horizon-gap` (`2b63f38`) merged in, with Sea 0.4.7-pre.6 and AstraTerra 0.15.0-dev.21. The two touch different lines of `wmapfarshade.ash` and merged cleanly.

**No row of sky under distant coasts.** Issue 6 of the 25 October play-test (a moon seen "under the waves" off Portugal): a one-pixel row of sky ran along the horizon between distant land's lower edge (0.4.11's behind-the-bulge discard line) and the drawn far sea's top edge, a pixel or two lower, and the rising moon showed through it. Far ground behind the bulge is now kept a small skirt below the grazing line (at most 0.004 rad, and at most half way down to the far sea's near edge, so a low eye never sees it through the game's own water). That ground is farther than the sea, which still hides it wherever the sea is drawn. Sea's shader anchors are unaffected.

# 0.5.0-pre.6 (play-test, not released)

Play-test build of 25 October 2026 on branch `playtest/sunset-seam-2026-10-25` (from pre.5), with Sea 0.4.7-pre.5 and AstraTerra 0.15.0-dev.20, after issue 5 of the play-test: a band where Sea's near waves met the far sea, and a sunset sky with no glow.

**The far sea is as rough as the near water.** The far wave normals divided Sea's slopes by 5, still water's divisor; the near water divides by 5 × max(0.2, 1 − oceanity), which is 1 on the open sea, so the far sea had a fifth of its slope. Sea now passes the divisor for the camera's place (`seaWaveFactor`, default 0.2 as before). Sea's near water now fades its wave trains by the same rule as `wmapWaveNormal` (shorter than about four pixels), so the two meet without the near water's crests ending in a line.

**The far haze and far sea take the sunset glow.** AstraTerra dev.20 adds a sunset glow to the sky dome; `wmapSunsetGlow` adds the same glow (passed by Sea as `seaSunset`, zero without AstraTerra) to the sky colour far terrain fades into and to the sky the far sea reflects, so distant land and sea toward a low sun fade into the same warm haze as the sky behind them.

**Far land is no longer as bright as the sky.** In the sunset screenshot the far headland (94, 95, 89) was as bright as the sky behind it (99, 108, 124), with the sun behind it. Far land was lit as open, sunlit ground at any hour; the game also darkens its own terrain in its shadow map, to half at most. Land turned less than about 15 degrees to the sun now counts as in shadow, darkened as the game's shadows are: flat ground goes into shadow as the sun sets, slopes facing away from the sun are darker by day; full once the sun is 2 degrees up, gone as it sets, softer under cloud. Land also starts less hazed (0.15 of the sky colour instead of 0.3) and thickens linearly with distance instead of as fast as the sea's, still complete at the far edge. The sea's lighting and haze are unchanged (calibrated against the near water in pre.2).

Checked: core suite (107,160 assertions); Sea's ShaderCheck compiles `farterrain` and `farcells` with the new uniforms live. Not run: the smoke profiles. Not seen in game; how dark far slopes and sunset coasts look against the game's own terrain at the edge is a judgement for the play-test.

# 0.5.0-pre.5 (play-test, not released)

Play-test build of 24 October 2026: 0.5.0-pre.4 with the review fixes of `claude/review-fixes-wmap` (rounds 1 to 3) merged in, on branch `playtest/2026-10-24`; not merged into main. Where both touched the same code the prototype's behaviour is kept with the fix on top: recordings are still saved at most every 5 seconds, two a tick, and up to eight a tick while more than a quarter of the resident tiles wait to be saved; tiles without colours (predictions, older recordings) still keep no array of zeros, and reading the recorded-colour mark no longer makes one. A new recording's colours are still made off the main thread (the merge's first build moved that 67 KB to the main thread at the recorder's first colour: 29.9 KB a tick in the smoke; now 0.6 KB).

**Round 3 and Earth.** Every refused paper-map drawing stops its upload on the client, and an upload dropped at its deadline or for silence tells the player. With an Earth build that has `ClimatePixelForVanillaLapse` (climate rules v2), far terrain's tint reads that byte, so highlands are no longer cooled twice; older Earth builds keep the old byte (E4-3). A cached world chart is hashed once at start.

**A new world's first fine recordings are kept.** On a world Wmap had not seen before, the block-by-block recordings of the first session (story locations, edits, shores) were deleted on the next connection, taken for 0.4.3's test recordings. Lost recordings come back only where the places are seen again.

**Paper maps are cheaper and safer for the server.** One drawing a player at a time and one start every 5 seconds; an upload grows as its pieces arrive and is dropped at once when the player holds no blank map, or after a minute without a piece. Reading a map is ignored while the same map is still being sent, or within 3 seconds of the last read; it is parsed off the main thread, and markers are compared only with those nearby. Downloads share 16 pieces a tick between all readers. A reader's client decodes a map's terrain a few dozen tiles at a time, just before merging them, and waits while many merged tiles are still to be saved, so the atlas and the recorder keep room.

**Smaller fixes.** `FarTerrainClimate: false` now also turns off climate colours and raised forests in the near band. Far terrain follows a change of game mode that allows or forbids the near band, without reconnecting. A world chart whose cache cannot be written (a read-only folder, a scanner) is still published. With `PredictTerrain` off on an Earth world, latitude and longitude wait for Earth's geometry instead of locking in a linear guess (new manifest field; older servers keep the old rule). Less garbage: exploration saves, the distance transform, atlas markers and cache trimming; line of sight stops each ray where nothing farther can show (same result). Texture uploads are guarded against unpack state other mods leave behind.

**Round 2.** Predicted tiles reuse the samples their neighbours already took (their margins overlap), the same tiles from about a third fewer terrain lookups when an area fills in, about 40 % fewer for a whole near band. A refused drawing now stops the client's upload; uploads also expire by a deadline from their start. The cached world chart is no longer unpacked at every server start when the digest written beside it still matches. Tiles without colours no longer keep an array of zeros. Client and server stay compatible with 0.4.11 both ways.

# 0.5.0-pre.4 (prototype, not released)

Second review round and the play profile of 0.5.0-pre.3; still on its own branch, not merged.

- **Texture units cannot clash with the game's.** Every game sampler in far terrain's programs is pinned below Wmap's, and Wmap's units are planned per program above them; pre.3 knew five names and put the fifth on the loaded-columns mask's unit.
- **After a teleport or a jump across Cylinder World's seam** the map of loaded chunk columns is made again at once, not up to 250 ms later.
- **Less garbage in play.** Recorded boxes and lights are put together into the same arrays, not new ones of up to 2.6 MB every few seconds; new recordings rework the windows at most once a second; the exploration save reuses its copy of the mask (about 27 MB every 30 seconds on an 8192-wide chart, also in 0.4.11); the day and night layer reuses its position.
- **No false lights after a rollback** to a Wmap from before 0.4.4 on tiles 0.5 had recorded: only colours of this format count, cell by cell.

# 0.5.0-pre.3 (prototype, not released)

Review fixes to 0.5.0-pre.2; still on its own branch, not merged.

- **Far terrain no longer covers loaded ground while a chunk column loads.** pre.2 started far terrain at the nearest missing column in every direction; now it draws nearer than 0.75 × view distance only over the columns that are missing.
- **No false lights from older recordings.** Recordings made before 0.4.4 stored the game's colours in a form 0.5.0-pre.2 read as bright lights. Tiles now carry a mark saying their colours are the new kind; older colours are ignored and cleared when the recorder passes again. Paper maps keep each tile's kind.
- New recordings show without moving first; recordings being edited are copied a few per frame, not all at once; the terraces' cell map no longer fails when another mod leaves an unpack buffer bound; the status line is no longer rebuilt every frame.

# 0.5.0-pre.2 (prototype, not released)

After the user's first in-game test of 0.5.0-pre.1 ("looks fantastic"); still on its own branch, not merged into main.

**The far sea meets Sea's sea without a ring.** From where your chunks end to 768 blocks the far sea was drawn by the terraces, which Sea's shader patch never reached: a flat teal ring around Sea's darker, reflecting water. Wmap now declares the far-water uniforms itself (`seaNightFloor`, `seaSkyReflect`, `seaWaveCount`, `seaWave`) and shades the far sea with them in every far program: Sea's night floor, the sky reflected by Fresnel, and with Sea 0.4.7-pre.1 its waves continuing into the distance (normals only, fading out before they would shimmer). With Sea's reflection on, the far sea's colour follows Sea's near water (fitted to in-game screenshots). Sea 0.4.6 keeps working (reflection and night floor, no far waves); without Sea nothing changes.

**No band of sky after changing the view distance.** After switching to a larger view distance (and after a teleport, or sailing fast) the chunk columns out there take a while to load, and the game shows the sky where they are missing. Far terrain now starts at the nearest column not loaded yet, so it fills the gap until they arrive.

**Less garbage.** The play profile showed Wmap's client tick allocating 262 KB per call and far terrain 8.3 KB per frame. Recordings are now saved at most every 5 seconds and their copies reused, the recorder reuses its positions, recorded tiles are taken again at most every 10 seconds with pooled buffers, and far terrain no longer builds a status string every frame; the first frame no longer uploads 25 MiB of zeros. In the smoke checks: the recorder 0 bytes per sample, far terrain about 0.8 KB per frame, the tile client 2.4 KB per tick under a stream of new recordings.

# 0.5.0-pre.1 (prototype, not released)

Built on its own branch (`claude/mid-distance-view-41ba01`) for testing in game; **not merged into main** until it has been tested (WM-P06, [design and results](docs/mid-distance-view.md)).

**Mid-distance view: the real world past the real chunks.** Far terrain showed the predicted world: smooth at mid distance, without buildings or lights. Now, out to the near band's 1536 blocks, it draws what you recorded where you have been, over the prediction:

- **Recorded terrain.** The recorder records every chunk your game has loaded, out to your view distance (before: 4 chunks around you), with the game's own colour and the block light of each sample, within its existing time budget per frame. Far terrain takes those heights and colours over the prediction, and draws recorded buildings and other structures past the terraces as blocks at their real height.
- **Terraces.** From where your chunks fade out to 768 blocks (`TerraceDistance`), far terrain is drawn as two-block cells at whole-block heights with walls between them, like the chunks next to it: recorded where you have been, predicted elsewhere, built on the graphics card each frame.
- **Distant lights.** Lit places you recorded (torches, lanterns, lighthouses) glow at night past your chunks, out to 3072 blocks (`LightDistance`): a few points per chunk, hidden by the ground and buildings in front, sinking below the horizon with the planet's curve, fading in fog and by day.

Each part has its own client setting (`MidDistanceRecorded`, `MidDistanceTerraces`, `MidDistanceLights`, all on in this build) and can be switched in game without restarting: **`.wmap mid`** shows what is drawn and how long it takes on the graphics card; `.wmap mid recorded|terraces|lights [on|off]` switches a part. The idea is to play with a smaller view distance (256 or 384) and see as far as at 512 or 1024. No protocol change: client and server stay compatible with 0.4.11 both ways. Recordings keep their format, so 0.4.11 still reads them (and paper maps holding them); recordings made before have no colour until you pass there again, and far terrain keeps its own colours there.

# 0.4.11

**The world is round: coasts sink below the horizon.** Line of sight and far terrain looked over a flat world, so from Athens far terrain showed Africa's coast and from mid-Atlantic Brazil's, and line of sight charted whatever was within reach. On worlds with geography (Earth Map, Cylinder World's planet layout) the ground now sinks by distance² / (2 × radius), the radius being the world's east-west circumference / 2π, so how far you see scales with the world's size. On a 256,000-block ring, from a deck 5 blocks above the sea: the sea's horizon about 640 blocks off, a low coast to about 1,300 blocks, a 50-block hill to about 2,700, a 150-block mountain to about 4,100. Far terrain stays flat out to your view distance, where the game's own flat chunks fade into it, and curves from there, so a sea curving away under the fading water leaves no gap; ground behind the sea's bulge is hidden even before your chunks load. Far terrain's horizon is therefore your view distance farther than line of sight's. New server setting `PlanetScale` (1; 0 to 16): larger is a flatter planet that shows farther, 0 is flat as before. `SightDistance` (2048) still caps line of sight. Client and server stay compatible with 0.4.10 both ways; a 0.4.10 server sends no radius, so a 0.4.11 client sees a flat world there.

# 0.4.10

**Rivers no longer cross lakes on the terrain map.** Earth's river courses run on through the lakes they pass (the Saint Lawrence's from Superior down to Ontario), and the terrain style drew them as blue lines across the water when zoomed out; zooming in hid them again because the detail tiles left them out. Now rivers stop at the shore at every zoom, in both styles and on the minimap. No new settings; client and server stay compatible with 0.4.9 both ways.

# 0.4.9

**Line of sight reaches 2048 blocks, not 4096.** Sailing charted coasts far beyond anything you could make out. `SightDistance` is still the server's setting (512 to 16384); a `wmap.json` written before keeps its value until edited.

**Small hidden patches are charted with what surrounds them.** Line of sight left the backs of hills and small hollows as dark holes, so the chart came out patchy. A hidden patch that what you see encloses (together with what you charted before) is now charted too, up to the area of a 256-block square (`SightHoleSize`, 0 to 1024; 0 turns it off). A patch reaching the edge of your sight stays hidden: it may be the start of a valley you have not seen.

**Latitude, longitude and heading beside the minimap.** On worlds with geography (Earth Map) a small box under the HUD minimap (over it in a bottom corner, top right without it) shows where you are and your compass heading, in the atlas's degree format. It moves below the game's own block coordinates (Ctrl+V) when both are shown. Ctrl+Shift+V or the client setting `CoordinatesHud` turns it on and off.

Client and server stay compatible with 0.4.8 both ways; an older server fills no patches.

# 0.4.8

**Paper maps: share what you have discovered.** Craft a blank map from parchment and an ink and quill (the quill is kept). Hold it and right-click to draw onto it, with a title, any of: your charted area, your recorded terrain, your waypoints, your automatic markers and your prospecting readings: everything you have discovered of those, as it is now. Hand the drawn map to another player; holding it, they right-click and tick what to copy into their own atlas. Their copies of waypoints and readings are made by the server as their own game waypoints and readings (ones they already have are skipped, automatic markers count against `AutoMarkLimit`); charted area and terrain fill in only where they have recorded nothing themselves. A map is a snapshot: exploring further does not change it. A drawn map and a blank map in the crafting grid make a copy (the drawn one is kept); copies share one snapshot. The user's design (WM-P05, now WM-D34..WM-D36; see docs/paper-maps.md).

The map's content lives in the world save; the item carries only its id and a description, so a map costs nothing to carry around. Waypoints and readings are taken from the server's own lists, not from the drawer's client; charted area and terrain come from the client and are checked for format. New server settings `PaperMaps` (on) and `PaperMapMaxMiB` (16). Needs client and server 0.4.8; with an older server the maps say so and do nothing.

# 0.4.7

**Small lakes are drawn as lakes.** The recorder records block by block only the 4 x 4-block cells that differ from the prediction, so a lake the prediction does not know was recorded with nothing around it. The chart took those unrecorded cells for more water: the lake had no shoreline where it met them, the coastal waterlines ran round a coast that was not there, and the lake ended in the recording's soft, stepped edge. Unrecorded cells now count as what the prediction under them says, so the shore lies at the lake's edge and is inked all round. The cells next to a shore (water where land was predicted, or land where water was) are now recorded block by block too, also across a chunk's edge, so a lake's edge is no longer cut to four-block steps. Lakes are no longer stippled as sandbanks: their depth is not stored, and the chart read it as none. A recording's faded edge no longer draws a pale rim. Lakes recorded before keep their steps until you pass by again (a chunk near the player is recorded again within five minutes). No new settings; client and server stay compatible with 0.4.6 both ways.

# 0.4.6

**Waypoints and prospecting readings no longer turn into giant circles when you zoom out.** The game sizes these markers for its own map's zoom (0.25 to 6 pixels a block) and shrinks them as the zoom falls; at the atlas's far zoom the size went negative and the markers turned inside out into red circles wider than a continent. Other mods' map layers are now told a zoom within the game's range; where they sit is unchanged.

**Place and edit waypoints on the atlas.** A right-click opens the game's own waypoint dialog at that point (its icons, colours and title), so the marker is the server's and shows on the minimap, to your group and to other mods. A right-click on a waypoint edits it, and on a prospecting reading offers to delete it, as on the game's map. In creative the right-click menu offers **Add waypoint…** next to **Teleport here**. Right-clicks act when the button comes up, as on the game's map.

**Survival no longer shows the Creative / Game button after leaving creative.** The window was built again as soon as the game mode changed, still with creative's permissions; the server's new answer arrived a moment later and did not rebuild it. Now the window is rebuilt whenever what it offers changes.

**Automatic markers.** Choose under **Auto-mark** what gets a waypoint by itself: berries, mushrooms, surface ore, wild beehives, resin, fruit trees, clay, translocators and traders, marked **when you start harvesting or breaking** them and/or **when you look at them while sneaking**. The server makes a game waypoint with the kind's icon and colour, named after the thing ("Blueberry bush"), unless one of the same name is within a few blocks, and says so in chat. They are ordinary waypoints: edit or delete them like any other. Server settings `AutoMarks` and `AutoMarkLimit` (1000 a player). Needs client and server 0.4.6; an older server simply makes none.

**Markers follow the zoom, and filters.** The atlas now draws waypoints and prospecting readings itself. Your own waypoints stay at every zoom and shrink to about half size on the whole world; automatic markers and readings fade out between 4 and 16 blocks a pixel, so the wide view keeps only what you placed (a pinned automatic marker stays). **Show** chooses what is drawn (your waypoints, automatic markers, prospecting, other mods' layers) and **Ores** shows only the readings that mention one ore, coloured by its strength. A pinned waypoint is drawn once on Cylinder World's ring, not again at the edge of each copy.

Settings are new on the client (`AutoMarkOnUse`, `AutoMarkOnSneak`, `AutoMarkKinds`, `Show…`, `ProspectingOre`) and server (`AutoMarks`, `AutoMarkLimit`). A 0.4.6 client and a 0.4.5 server, or the reverse, still work together without automatic markers.

# 0.4.5

**Survival no longer shows the whole world to a single-player host.** The server gave the full map to anyone holding `controlserver`, and the host of a single-player world always does, so the Creative / Game button and the predicted world stayed available in survival. Now only creative mode, the `wmap` privilege (admins do not get it automatically) or `AllowAllPlayers` open it.

**Charting by line of sight is on by default** (`ExploreBySight: true`). An existing `wmap.json` keeps the value it was written with: set `ExploreBySight` to `true` there, on the server (in single player, your own), and restart.

**Kelp is no longer charted as islands.** The recorder walks down through trees and plants to the ground under them, and did the same with kelp and seaweed that reach the water's surface: it recorded the seabed under them as land, so the sea showed small dark islands with coastlines in the plants' shapes. A plant standing in water now counts as water, with the bed under it. Recordings already made are corrected when you pass by again (a chunk near the player is recorded again within five minutes, and the cells recorded block by block on every visit). No new settings; client and server stay compatible with 0.4.4 both ways (a 0.4.4 server still gives its admins the full map in survival).

# 0.4.4

Fixes and performance work from a code review (Claude and Codex). No new settings; client and server stay compatible with 0.4.3 both ways.

**Recordings reach the zoomed-out map again.** 0.4.3's recorder kept, at each level coarser than four blocks, only a sample on the cell's centre block, and no four-block sample ever lies there: newly charted terrain stopped reaching those levels, so worlds without a prediction (plain worlds, `PredictTerrain` off) showed new exploration only close up, and Terra Prety and Cylinder-only worlds lost the correction of their approximate continents when zoomed out. Each coarser cell now keeps the four-block sample nearest its centre, wherever the ring starts against the samples' lattice; a cell cut off by the world's east or south edge is centred on its part inside the world. Blocks recorded one by one go only into the one- and two-block levels.

**No samples lost while recordings load.** A sample for a recording tile still loading from disk was dropped, but its chunk counted as recorded and was not visited again for five minutes. Now a chunk with a sample not kept is recorded again a second later, with its reported changes; what was kept stays, so a recording that cannot be read holds up only its own level.

**Recordings follow the world.** A 4 x 4-block cell recorded block by block is recorded again on every visit, so a structure that was removed since no longer draws over the prediction. The bed under water follows edits: the recorder scans down through the water instead of taking the generated bed, so a bed raised, dug out or built on (a platform with water under it) counts. Reported block changes far from the player are forgotten instead of piling up on long voyages.

**Recordings are written in order across quick reconnects.** A session that closed while the previous one was still writing its recordings could write first and have its newer cells overwritten.

**Map layers on every copy of the ring.** Waypoints and other mods' map layers (Magelan's ships) on the atlas are drawn on each copy of Cylinder World's ring the view shows: they vanished after panning a lap and were missing across the seam. Hover text uses the copy under the cursor. A pinned waypoint can show an extra copy at the map edge while the seam is in view.

**Far terrain respects the server's chart.** Far terrain stops when the server no longer shares the world chart with the player (survival with `ShareWorldChart` off after a creative session), instead of drawing unexplored terrain on the horizon. Shader reloads forget the old uniform locations. Two shading terms that relied on undefined `smoothstep` behaviour (cliff rock, the rivers' edge; a doldrums line on the wind layer) are well defined now.

**The exploration mask spans the ring exactly.** On a ring that is not a whole number of 32-block texels, charted areas were shifted toward the east and a disc across the seam was charted short on the far side.

**Less work per frame.** A still atlas or HUD minimap no longer selects, uploads and sorts its tiles every frame, and the atlas's latitude and longitude labels are kept until the view changes. Far terrain works out its tile windows only when the camera has moved 16 blocks. The exploration mask's mipmaps are rebuilt at most once a second. Line of sight collects each charted cell once on its worker (a 16384-block reach handed the main thread over six million points per cast). The latitude and longitude grid is built off the main thread, and the recorder no longer asks the game for a colour it never draws.

**Known limitations.** A pinned waypoint can show a second copy at the atlas's edge while the seam is in view. A 0.4.3 client on this server waits 10 s after a "busy" answer instead of 1.5 s. See the README's known limitations for the rest.

**Server.** With its tile queue full (many players at once) the server answers "busy" and the client asks again 1.5 seconds later, instead of the request vanishing until a 15-second timeout. Cache reading, trimming and writing survive files that cannot be accessed: the tile is made again. Per-player state is cleared when a player leaves. Near-band tiles from a server without biome tiles are reused from the disk cache.

# 0.4.3

**Day and night on the atlas.** The **Day / Night** button shades the world by the sun's elevation, as day-and-night world maps do: day, civil, nautical and astronomical twilight (to 6, 12 and 18 degrees below the horizon) in darkening steps, then night, with a line where the sun is on the horizon. The elevation is the game's own (`Calendar.GetSunPosition` at each place, sampled over a 256 × 128 grid every 5 seconds while the overlay is on), so it follows latitude and the seasons (polar night and midnight sun), and longitude with AstraTerra's longitude-aware sun (Earth Map gives AstraTerra the longitude of each X; Earth Map alone does not move the sun); without AstraTerra the whole world shares one time of day. Client setting `DayNightOverlay` (default `false`; the button stores it).

**More depth and height on the Chart.** The sea now has depth contours, dense on the shelves (8, 12, 16 and 20 blocks, the 20-block line stronger) and dotted every 10 blocks beyond, and the shelves are tinted in steps (under 12 and under 20 blocks); the dotted line at 28 blocks is gone. Land has faint contours every 10 blocks with a stronger one every 50. All contours fade where they would crowd together on screen.

**Lakes in far terrain lie at their own level.** The chart stored water as the height of its bed, and far terrain held all water at the sea's level, so a lake above the sea was drawn as a hollow at sea level or as its bed: under Lake Baikal's real water a grey basin with cliffs showed through (reported in game). Water standing above the sea (lakes, rivers inland) now keeps its surface height in the chart and tiles, and far terrain draws it as water there. The atlas showed such lakes as shallow water before and still does. Worlds re-chart once.

**Far terrain lit like the game's own terrain.** Far terrain took the game's sun colour, which in polar twilight is a deep red: in Antarctica the far snow glowed red-brown beyond grey-blue real snow (reported in game). It is now lit as the game lights its chunks under the open sky: by the game's ambient colour, which carries day and night, with the chunks' own normal shading. At night it is as bright as the real terrain beside it (before, darker).

**Charting by line of sight** (server setting `ExploreBySight`, default off; client and server 0.4.3). The game chart can be charted by what you can see instead of a 1536-block circle: a small circle (`SightCircle`, 64 blocks) and everything your eye sees over the ground in every direction, out to `SightDistance` (4096 blocks). Mountains hide what lies behind them, so sailing the Adriatic charts Italy's coast and the Apennines' eastern slopes but not the Tyrrhenian side; standing on a deck counts, inside the hull too, and a mast top sees farther. It looks over the world chart's heights (half resolution, each cell its highest point), casts in the background after you move 32 blocks or climb 3 (about 2 ms), and leaves out forests, buildings, fog and night. The server's setting applies to every player; without the shared world chart the circle applies.

**The map follows the game mode.** Development mode is now the **Creative** map and Gameplay the **Game** chart. Creative mode opens the whole predicted world and survival returns to the game chart, as soon as the game mode changes; the server grants creative players the full map (as it does with the `wmap` privilege or `AllowAllPlayers`) and the client asks again when its mode changes. The **Creative / Game** button shows only in creative mode or with that permission.

**Terrain colours by climate** (Earth Map; client and server 0.4.3). The Terrain style coloured land by height alone, so low deserts were the green of lowlands (reported for Algeria). The server now bakes a climate raster after the world chart (forest, temperature and rain from Earth Map, the data far terrain is tinted by; 1024 × 512 texels on the usual ring, cached) and sends it like the chart. Land takes cross-blended hypsometric tints from it (as Natural Earth's maps do): a humid height ramp (green lowlands, tan, brown, grey rock, snow) and an arid one (sand, ochre, brown, grey rock, snow) mixed by how dry each place is, forest deepening the greens and a cold ramp in tundra and ice climates, with vanilla's altitude adjustment, so colour still reads as height while deserts and wet lowlands differ. Without Earth Map, or from an older server, colours stay by height.

**Close zoom shows what the world holds.** Story locations and player edits showed on the atlas at regional zoom but vanished close up (reported in game): closer than 4 blocks per pixel the one-block prediction, which cannot know such changes, drew over the four-block recording. The recorder now compares what it records with the prediction around you (far terrain's near band) and records a 4 × 4-block cell block by block where they differ by land and water or by more than 3 blocks, or where the game reports a block changed; those one-block recordings draw over the prediction, the one-block prediction stays elsewhere. Heights skip trees and plants (the ground, not the canopy). Chunks are recorded when they load or change and again every five minutes, not in an endless loop, and a colour alone is no change, so the map is not rewritten while you stand still. Without a prediction nearby, a cell is recorded block by block where its top differs from the generated terrain by more than plants or water. The every-block recordings of the 0.4.3 test builds are removed once. Decision WM-D29.

**Teleport from the atlas** (creative). In creative mode, a right-click on the atlas opens a small menu, dark ink on chart paper, with that point's position; a left click on **Teleport here** takes you there (a click elsewhere, the mouse wheel or Esc closes it). The server loads the column, generating it if need be, and puts you just above its top block or water surface. Positions across Cylinder World's seam land on the ring's own lap; nothing happens beyond the poles. The server checks creative mode and the `tp` privilege itself and says in chat when one is missing. Client and server need 0.4.3; the server announces it, so a 0.4.3 client never sends the request to an older server. Client setting `RightClickTeleport` (default `true`).

Draw rivers once at full zoom. With one-block detail tiles (about 1 block per pixel and closer), a predicted tile holds every river and stream as its own water, but the world chart's river channel was still drawn over its land: interpolated from 25-block texels, it lies a few blocks off the real river, a second, darker blue band along one bank and then the other (reported in game on the Guadalquivir). One-block tiles now draw rivers from their own water only; coarser tiles keep the chart's river lines, which they cannot resolve themselves.

Record snow-covered polar ice as ice. Cylinder World drifts snow (snow blocks and snow layers) over the polar sea ice and the Wall's top. The terrain recorder classified the top block, and snow is not ice, so it recorded those cells as land a few blocks above the sea: green islands with chunk-shaped edges along your track across the frozen sea, and grey rock on the Wall (reported in game near the North Pole and the seam). Snow now takes the kind of what it covers; its height still counts, as in the prediction of the polar ice surface. Client only. Recordings made by 0.4.2 and earlier correct themselves where you pass again.

Tests: the earth smoke profile now loads AstraTerra (its earthtest build, by name) and checks the day and night overlay against the real calendar at an equinox and both solstices: the longitude binding logged by Earth Map and AstraTerra, local noon where the calendar's hour puts it, polar day and night beyond 68 degrees, a straight boundary at the equinox and a curved one at the solstices, twilight bands about 6 degrees of longitude wide; the Cylinder World and plain profiles, without AstraTerra, show latitude bands only. Tests: a new GPU check renders the Guadalquivir at full zoom and counts pixels of the chart's river channel: 0.4.2 [draws 7,192](docs/test-results/river-before-0.4.3.txt), 0.4.3 none. The dialog check drives the menu with a creative player (a right-click opens it without teleporting, a click elsewhere and Esc close it, its item teleports to the right-clicked point) and a survival player (no menu). A server check teleports to a column across the seam, generated and then loaded; the manifest and request are checked on the wire, old and new. The seam-band check follows Cylinder World 0.11.6, which keeps Earth's real seabed in the band of new Earth-mode worlds. The Earth profile's generated-polar check now also runs the recorder over every generated sea-ice and Wall column and requires the predicted kind, with snow-covered columns among them: 256 columns agree, 192 under snow. Without the snow rule it [fails](docs/test-results/snow-before-0.4.3.txt) at the Wall (64 of 64 columns recorded as land). The smoke report's header now says FAIL when a generated check fails; it said PASS above them.

# 0.4.2

Keep far terrain dark at night. Far terrain blends toward the game's sky colour with distance (at least 30 % on land), but at night the game's sky colour function returns the fog colour, a dim brown, not the dark sky; distant land glowed brown against the night sky (reported in game, looking north at night). The game's own chunks take the sky colour by at most the daylight (`chunkopaque.fsh`); far terrain now does the same. It also gets the same daylight value as the game's sky and chunk shaders (1.25 × the day's light less half the moon's, at least 0.05), instead of the raw calendar value.

Tests: a new GPU smoke check renders the coast at night under a black and a brown fog colour: far terrain must look the same (0.4.1 [differs by up to 41/255](docs/test-results/night-before-0.4.2.txt); 0.4.2 by 5), and at noon a different sky colour must still show in it (107–129/255).

# 0.4.1

Keep the far sea off distant coasts. Far terrain chose sea or land per pixel from the 4-block coast distance, but its mesh near a coast has 8- to 64-block cells, so a cell running from the sea up a cliff is a ramp, and the part of that ramp still on the sea side of the coastline was painted as sea, well above the water. Distant coasts looked flooded, the sea creeping up their cliffs (reported in game at view distance 512, against the same place at 1536). Water is now drawn only where the drawn surface lies at the sea; above it the shore is land, and steep faces are bare rock. No protocol change: a 0.4.1 client works with a 0.4.0 server.

Tests: a new GPU smoke check draws the far sea pure red (through Sea's colour hook) toward the coast above Nice and reads each red pixel's height from the depth buffer: none may be more than 3 blocks above the sea. 0.4.0 [fails it](docs/test-results/shore-before-0.4.1.txt) (5,132 of 126,052 sea pixels, up to 75 blocks up); 0.4.1 draws the highest 0.9 blocks above it.

# 0.4.0

**Near band and climate colours for far terrain** (Earth Map; client and server 0.4.0). Out to 1536 blocks (`NearBandDistance`), far terrain is drawn from Earth's 4-block detail tiles instead of the 25-block world chart: the hills, cliffs and coasts nearest the real chunks have their real shape and height. On land, half the points lie within 0.4 blocks of Earth's terrain and 95 % within 1.1; from the chart alone it was 1.8 and 9 blocks. Beyond the band the chart takes over, blending over 336 blocks. Everywhere, far terrain now takes the game's own climate tints for grass, leaves and water (vanilla's colour maps, with its altitude adjustment), and forests stand about 8 blocks above the ground, from Earth's forest and climate. The far sea's colour now follows the climate too.

The tiles come from the server on their own stream, nearest first: about 43 ms each to sample once, cached on disk and shared by players, about 2 seconds for a whole band on a fresh world. They never hold up the atlas: far terrain has its own request cap and one worker that takes players in turn, and two requests for the same tile share the work. Old clients and servers keep working with 0.4.0 and simply go without. New settings: `FarTerrainNearBand`, `NearBandDistance`, `FarTerrainClimate`. On an RX 6600 far terrain takes about 0.3 ms per frame with the band (0.1 ms before).

Stitched ring edges now average the final displaced height (with canopy and push-down), so they cannot crack. New detail fades in over 1.5 seconds, and in space toward tiles not yet loaded through one continuous field, also at corners and short seam tiles. Crossing Cylinder World's seam keeps the loaded tiles where they are.

Planned and built with adversarial Codex reviews: of the plan (which changed the server scheduling, the stitching, the fallbacks and the tests) and of the implementation in two rounds (which found the server not announcing the feature at all, unfair turns, blending, seam and corner steps, GL state at texture creation, and tiles restarting at the seam). See [docs/far-terrain-near-band.md](docs/far-terrain-near-band.md).

Tests: the core suite checks the toroidal tile window (distinct slots, seam, partial tiles, texel centres), biome tiles, the finer mesh and that shader sources are ASCII (non-ASCII comments broke compilation on AMD). New smoke checks: the far stream on the server (independent of the atlas, capped, fair, drops work for departed players, old and new messages read each other, biome values plausible, costs) and, through transform feedback, the displaced vertices themselves: stitched edges exact, heights equal to the tiles, closer to Earth's terrain than the chart, also across the seam and partial seam tiles; continuity every quarter block beside a missing diagonal tile, between unequal fades and across a 30-block seam tile (the earlier formula: a [46-block step](docs/test-results/corner-before-0.4.0.txt)); tiles kept across a seam crossing; GL state kept; the tint applied; and GPU time. The plain profile's sea-edge check from 0.3.10 now skips the synthetic chart, which has no sea there.

# 0.3.10

Join far terrain to the real sea. The game fades its own chunks out well inside the view distance: water between 75 % and 80 % of it, land between 80 % and 85 %. Far terrain began only 24 blocks short of the view distance and sat twelve blocks low there, so through the fading water you saw far terrain kilometres off, pale with haze: a light band along the edge of the real sea, with coasts showing through the water. Far terrain now begins where the water starts to fade. Its sea lies one block under the real water surface there, rising to it over 512 blocks, and land sinks no more than a block below the sea. The far sea no longer starts hazed, so it meets the real water in its own colour.

Tests: a new GPU smoke check looks out to sea from a boat at a 512-block view distance and requires the far sea right behind the fading water and under its surface; [0.3.9 fails it](docs/test-results/sea-edge-before-0.3.10.txt).

# 0.3.9

Chart Cylinder World 0.11.5's natural seam band. Worlds created with 0.11.5 (layout v10) have a band seabed with relief, a wavy edge and a ramp of at most 1 : 4 that can reach past the band's half-width. Wmap drew them with the old formula: a flat band, ending at the half-width. It now asks the installed Cylinder World for the ground of every column the seam may change (`SeamParams.SeamGround` within `SeamWorkReach`), so the chart follows the world in every layout, and keeps a column Cylinder would leave as it is unchanged. Inside the polar zone, the band's exact seabed follows the same relief. Cylinder builds before 0.11.5 are drawn as before. Worlds re-chart once, as after any Wmap update.

Tests: a new smoke check compares the prediction with the installed Cylinder's `SeamGround` over land and deep sea, out past the ramp, for the world's layout and for the same world as layout v9; [the 0.3.8 code fails it](docs/test-results/seam-band-before-0.3.9.txt). The Earth profile's generated-chunk comparison adds three seam locations (identical part, inner band, past the half-width) in either polar mode.

# 0.3.8

From an adversarial code review, with a look at other terrain generators (Terra Prety). Decisions are now kept in [docs/decisions.md](docs/decisions.md); other generators are analysed in [docs/terrain-generators.md](docs/terrain-generators.md).

Draw recorded terrain over predictions. Since 0.3.0 the chart drew what your client recorded only when the server did not share predictions, so a wrong predicted coast stayed wrong after you had sailed past it. Recordings now draw over the prediction of their level or coarser at detail zoom (over Cylinder World's flat prediction at every zoom), and keep their own water instead of the chart's predicted river courses.

Treat Cylinder World's prediction without Earth Map as what it is: a land/sea mask with invented heights. Far terrain is off for it (it drew flat plates of land on the horizon), and predicted detail no finer than four blocks is sampled. With Terra Prety installed, the mask follows Terra Prety's ocean map; the source line and the server log say so. New server setting `PredictTerrain` (default `true`): `false` charts recorded terrain only, keeping exploration and coordinates.

Record water depth. The terrain recorder stored the water surface as the seabed, so recorded seas had no depth, which in worlds without Earth Map or Cylinder World meant every sea on the map. Existing recordings correct themselves where you pass again.

Follow Cylinder World's `FlattenSeabed` setting: with it off, Wmap no longer predicts a flattened seam band that the world does not have.

Keep the prediction cache across restarts. Its key included Cylinder World's live sea-ice date, so a restart on another in-game day rebuilt the world chart. It now covers only what shapes terrain, and adds Terra Prety's settings. Worlds rebuild their chart once after updating.

Pace the world chart download on the server. A client could request the same 64 KiB chunk without limit; requests are now deduplicated, capped per player and sent at up to 10 MiB/s per player and 20 MiB/s in all, taking turns between players, without main-thread work per request. Hello and tile-request packets are dropped while a player already has 32 waiting on the main thread.

Fix maps that do not wrap (Earth Map without Cylinder World, plain worlds) drawing their west edge east of the east edge when the camera rested there. Nothing is drawn beyond the east and west edges.

Keep recordings and exploration unsaved after a failed disk write, and write them again, instead of losing them. Write exploration in order, so a slow save on leaving cannot be overtaken by an older one, and merge a save still running when you reconnect, or a saved mask that could not be read, instead of overwriting it. A recording that cannot be read for a moment is no longer loaded as empty and then saved over the file.

Tests: 11 new core assertions on view selection and on loading saved exploration (missing, locked, truncated); new smoke checks for chart-request flooding and fairness between three players, a locked recording file, cache identity across restarts and sea-ice dates, `FlattenSeabed` and `PredictTerrain`, and a GPU render of a bounded map's east edge that [fails on 0.3.7](docs/test-results/bounded-edge-before-0.3.8.txt).

# 0.3.7

Add the **Jonas barometer** (`wmap:jonasbarometer`), a clockwork weather glass with a touch of magic. With the Sea mod, a player who carries one in the hotbar or backpack sees the weather now on the gameplay atlas and the HUD minimap. Only the weather within reach is shown: 3000 blocks around the player (`BarometerRange`, a server setting sent to clients), inside a dashed circle. The line under the map reads the wind where you stand and at the cursor in knots with its Beaufort name. The status line names the worst weather within reach and its bearing ("gale 1840 blocks to the WSW"), or fair weather. The field is a 96 × 96 sample of Sea's weather around the player (5 ms), refreshed after a few game minutes or a few hundred blocks of travel. Craft it from Jonas parts, brass, glass and a temporal gear; it has a handbook entry.

Other mods can grant the same view: the server sets the player entity's watched attribute `wmapWindSight` to a short description of the source. Magelan is to set it for everyone aboard a ship with a barometer in her binnacle; see [docs/magelan-barometer.md](docs/magelan-barometer.md). Wmap clears the attribute when a player joins. Items count as wind instruments when their attributes set `wmapWindInstrument`. `WindLayer` now defaults to weather now; in gameplay, Off hides the barometer's winds.

Fix other mods' map layers being hidden by Wmap's own quads. Magelan draws its ships at depth 55, after Wmap. The player arrow's quad wrote depth in its transparent corners, cutting a square out of the ship under it, and the wind layer's whole-map quad (Development mode, any Winds choice) hid every ship. Transparent map fragments are now discarded, and the wind layer no longer writes depth, so ships draw over the arrows and under the player's arrow.

Remove the brown stain blots from the chart's parchment; the mottled grain and darkened edges remain. Draw the graticule (latitude and longitude every few degrees, labelled at the map's edges) a little stronger.

# 0.3.6

Add far terrain. Past the view distance, the world chart is drawn as a hazy 3D landscape out to 8 km (`FarTerrainDistance`, 2–16 km): coasts, islands and mountain ranges on the horizon, the sea as a flat plane at sea level, in the atlas's Terrain colours, lit by the game's sun and blended into the game's own sky colour and weather fog. It starts just behind the real chunks and slightly lower, so the two never overlap, and wraps across Cylinder World's seam.

It is one static clipmap mesh (rings doubling in cell size, stitched without cracks), placed at the camera and displaced on the GPU from the world chart texture the atlas already holds: no new server work, packets or chunk generation, one draw call, about 1.6 MiB of GPU buffers. It needs a world chart (Earth Map or Cylinder World). Wmap raises the camera's far plane while it draws, stays off when Farseer is installed, and reads an optional sea colour from the Sea mod (`FarWaterColor`) by reflection. Settings: `FarTerrain`, `FarTerrainDistance`, `FarTerrainDetail`. Design and costs: [docs/far-terrain.md](docs/far-terrain.md).

The GPU smoke check compiles the far-terrain shader with the game's includes for every shadow, SSAO, godray and dynamic-light setting and renders it from the real chart; the unit tests prove the rings tile without gaps or cracks. The smoke runner now picks the newest sibling pre-release zip (`-pre.N`).

# 0.3.5

Add a wind layer to development mode when the Sea mod is installed. A **Winds** list above the map shows Sea's prevailing wind for any month or for today, or the weather now with its travelling storms. Arrows fly with the wind like a pilot chart's, sized and fletched by strength, on a world-anchored lattice that keeps an even screen spacing at every zoom and closes over the cylinder seam. Dashed lines mark the belt boundaries (horse latitudes, polar front, doldrums), so the seasonal shift of the belts is visible. The cursor readout names the wind, its Beaufort force and belt. Sea is read by reflection; without it nothing changes.

The browser shader preview draws Sea's belts too (W toggles, [ and ] change the month). The Earth smoke run loads Sea when its release is present, checks the trades, westerlies and seasonal shift at Earth places, and renders the arrows on the GPU.

# 0.3.4

Fix the player arrow pointing 180° off in the HUD minimap and the atlas: the facing was computed as if level pitch were 0, but a player's level pitch is π; the arrow now uses π − yaw like vanilla (`Heading.OfPlayer`), with the smoke check corrected the same way.

Draw other mods' registered vanilla map layers (waypoints, Magelan's ships, …) on the atlas, through a vanilla map element mirroring the atlas camera, with their hover text in the position line. Terrain and players stay Wmap's own. Near the cylinder-world seam an overlay may be one lap off.

# 0.3.3

Preserve detail-tile hill shading when the world chart arrives. Previously the shader replaced local slopes with the chart's coarse slopes, softening relief after the bake finished.

Draw cached neighbouring detail levels underneath new zoom tiles while they load. Exclude fallbacks coarser than the finished chart, and preserve uploaded GPU copies when decoded CPU entries are evicted. A 32-byte content fingerprint per GPU slot prevents identical disk reloads from blanking that copy; actual recorded edits still invalidate it. Both caches remain bounded; disk tiles are still reused after eviction. This improves transitions without prebuilding a complete world pyramid or eliminating the first generation of detail in new areas.

Use the same Wmap player arrow in the HUD and atlas, with heading checked against the game's actual forward vector at all four cardinal directions. The native footprint uses a different icon convention. Keep native tracking, entitlement colors for other players, hover text, waypoint markers and vanilla full-map rendering. Lower the detail depth increment so larger caches cannot cover player markers.

Add real GPU regressions for relief before/after chart arrival, cached parent and child fallbacks, promotion of finer detail, GPU reuse after CPU eviction, and both player drawing paths. Document the chart's resolution relative to world size and the limits of the image/detail cache.

# 0.3.2

Fix the map and compass remaining invisible after the world chart downloads. Vintage Story 1.22's projection and modelview getters return the same temporary array. Passing both getters as arguments overwrote the projection with the modelview before either matrix was uploaded, clipping every map quad off-screen. Wmap now copies each matrix before reading the other. The GUI GPU regression now matches the engine's shared-buffer behavior; it fails on 0.3.1 and passes with the fix.

Draw Wmap terrain in the HUD minimap using the atlas's style, world chart and gameplay exploration. Keep native player/waypoint markers, F6 toggling, Ctrl+F6 positioning and minimap zoom. The explicit `.wmap vanilla` full map keeps vanilla terrain. `ReplaceMinimap: false` restores vanilla HUD terrain after a restart. The minimap uses one additional 16 MiB detail page and shares the larger world/mask textures; the full atlas takes priority over its detail requests while open.

# 0.3.1

Fix a blank map stuck at “Charting the world… 100%”. The sampling progress reached 100% before the server finished packing and publishing the chart, and the ready announcement was then suppressed as unchanged progress. Readiness is now announced separately to each client; clients still loading when the chart becomes ready receive it when they enter play. Manifests expose the chart ID only after its packed bytes are available.

Make the map draw in front of the dialog background under strict depth testing, and disable face culling during map drawing while restoring its previous state afterward. New regression checks exercise the ready-announcement race, joining during publication, and the real GUI drawing path with the game's projection, culling and a depth-tested background.

# 0.3.0

Rendering rewrite. The server now bakes the whole world once into an 8192×4096 terrain raster (about 25 blocks per texel on a 204,800-block ring), caches it on disk and streams it to each client once; clients keep it on disk too. It is drawn from a single fully mipmapped GPU texture whose east–west wrap is done by texture addressing, so zooming from the whole world to regional scale needs no loading and the cylinder seam is continuous. Detail tiles are only requested when zoomed in beyond that raster.

Texels now hold terrain meaning (distance to the coast, height, ice, distance to Earth Map's river courses) instead of colors. A new shader evaluates the style per screen pixel: coastlines stay sharp at any zoom and switching style is instant.

- **Chart** (default): an automatically drawn nautical chart. Aged parchment, sepia ink coastline, offshore waterlines, stippled shallows and a dotted 200 m line, pen hatching on shadowed highland slopes, rivers from Earth Map's courses, ice, a graticule with equator, tropics and polar circles, edge labels, a compass rose and a scale bar in sea miles.
- **Terrain**: a topographic map with elevation tints, hill shading and bathymetry.

Gameplay mode now reveals the chart only where you have been, within `ExploreRadius` (1536 blocks, the game's maximum view distance, the same for every player); everything else is blank parchment. Exploration is saved per world and seeded from terrain recorded by earlier Wmap versions. With `ShareWorldChart` (default) every player receives the world chart and predicted detail; development mode (seeing unexplored terrain) still requires the `wmap` privilege. Without a prediction provider, recorded terrain is drawn through the same styles.

Sea ice at sea level is drawn as a pale ice field on the sea, not as coastline; glaciers on land keep their coasts. Rivers fade in from regional zoom, and relief shading is scaled to the world's height range, so Earth's exaggerated lowlands stay clean. The terrain recorder and the polar tests now recognise Cylinder 0.11's sea ice, which sits in the fluid layer.

The window fills most of the screen, zoom is smooth and cursor-anchored, and the player marker shows heading. Wmap owns its OpenGL textures: the game's helpers limit mipmaps to the player's mipmap setting and use nearest-mipmap filtering. The Surface (literal block colors) style is removed; `.wmap vanilla` still opens the original map. The network channel is now `wmap-v2`, so client and server must both run 0.3.0. Prediction caches are rebuilt; recorded observations are kept.

# 0.2.1

Support Cylinder 0.10's Earth polar mode across its full Arctic latitude range, including 0.10.1 pressure ridges and snow. Preserve Antarctica, include the near-wall coating, and keep the legacy generated-sheet mode separate. Prediction caches refresh on version or provider-assembly changes; recorded exploration is preserved.

Fix atlas textures sampling stale mipmaps created while the pages were empty. Allocate pages and staging textures without mipmaps and use explicit map detail levels. Promote a complete visible detail level together, and stop coarse observations from painting enlarged squares over development overviews.

Redraw Terrain with one elevation palette for both sources and smoother relief. Simplify Chart by removing dense contour speckles. Add an explicit Surface style for literal observed block colors. Map style cycles all three and displays the current choice.

# 0.2.0

Fix swapped red/blue channels when rendering recorded terrain, including existing cached observations. Add a persistent Dev / Gameplay button: gameplay displays recorded exploration only; development keeps the predicted overview and requires server permission. Gameplay access no longer requires developer privileges.

Increase default detail, filter empty observation tiles out of the view budget, preserve in-flight requests during zoom/pan, and retain already uploaded parent detail while new tiles load. Smooth magnified coarse tiles with linear filtering.

Include Cylinder World's saved seam, polar shelf, ice-sheet, planing and wall geometry in the pre-chunk terrain prediction. No world-generation changes or new polar layout are imposed by Wmap. Physical chart items and sextant mechanics remain future work.

# 0.1.2

Fix the crash when opening the atlas: size the background from its controls instead of filling an autosized parent. This removes the circular layout dependency that passed invalid bounds into Cairo's background blur. Dispose partial GUI resources if construction fails, and report opening errors in chat/logs instead of letting them terminate the game.

# 0.1.1

Use the normal world-map action (M by default) to open Wmap, preserving the player's remapped map key. Bind after vanilla's startup handler so it is not overwritten. Remove the conflicting Ctrl+M default, which belongs to the macro editor. Keep `.wmap` as a direct opener and add `.wmap vanilla` for the original map. Restore the original handler when leaving the world.

# 0.1.0

First development atlas: independent map window, full-world tile zoom, cylindrical east/west panning, Earth/Cylinder overview adapters, surface observations, compressed caches, bounded loading and GPU atlases, latitude/longitude, terrain and simple parchment styles.

Physical charts, surveying instruments, waypoints and ship route control are planned for subsequent versions.
