# Exploration

## Survival: you chart what you have seen

In survival the atlas and minimap show only the land you have seen. Everything else is blank parchment. The chart fills
in as you travel, and it is kept in the world save, for each player.

By default the map is charted **by line of sight**:

- a small circle around you is always charted (`SightCircle`, 64 blocks);
- everything your eye can see over the ground, all around you, out to `SightDistance` (2048 blocks);
- small hidden patches that what you see encloses, such as the far side of a hill or a hollow, are charted with it,
  up to the area of a square `SightHoleSize` blocks wide (256). A hidden patch that reaches the edge of your sight stays
  blank.

Mountains hide what lies behind them: sailing along a coast charts the coast you can see, not the valleys behind the
ridge. On worlds with geography (Earth, or Cylinder World's planet layout) the planet is round: land sinks below the
horizon with distance, so a low coast shows only a few hundred to a thousand blocks off and a high mountain much
farther. How far depends on the world's size (`PlanetScale`). The sight looks over the predicted terrain only: forests,
buildings, fog and night don't block it.

It is the same for every player, whatever their own view distance, and the server works it out for everyone.

The map fills in by **squares**, from 32 blocks on small worlds to about 490 blocks on a 1:10 Earth (the world's
larger side divided by 8192, at least 32), so the edge of what you have charted is that coarse. A square counts as seen when its
centre is; the circle around you charts the squares whose centre lies inside it, and the one you stand in.

With **Wmap: explore by sight** off (on the world-creation screen, or `ExploreBySight` in the server's `wmap.json`), the
map charts a plain circle around where you have been instead (`ExploreRadius`, 1536 blocks). Worlds without predicted
terrain (no Earth or Cylinder World, or `PredictTerrain` off) always use the circle.

Nearby land your game has loaded is also **recorded**: its real heights and water, every four blocks, and block by block
where it differs from the prediction (story locations, ruins, your building and digging). Recorded terrain is drawn over
the prediction, so where you have been the map shows the world as it is.

## Creative: the whole world

In creative mode the atlas can show the **whole predicted world**, charted or not. So can players the server gives the
`wmap` privilege, and everyone on a server that sets `AllowAllPlayers`. They switch with the **Creative / Game** button.

Their own charted area is still recorded by sight, as in survival: that is what their game chart shows and what their
paper maps carry.

For server owners: the `wmap` privilege and `AllowAllPlayers` send those players the whole predicted world (the chart,
the detail and the far view), so they are the switches that lift the survival protection.

## What reaches a survival player's game

From 0.5.4 the unexplored map is for creative only. The server decides what each survival player has seen and sends
their game the map **only of that land**: the chart, the close-up detail and the far view. Land they have not seen never
reaches their game, so a modified game cannot uncover it. The same rule applies to spectators and in a single-player
survival world.

The **far view** reaches farther than the atlas: it shows the land in your line of sight out to 8192 blocks, which is
what you would see on the horizon, while the atlas charts only what is within `SightDistance`. For the far view the
squares next to those in sight count as seen too, so the near slope of a hidden hollow does not show as sky. To put land
on your map you have to get closer to it. See [The far view](The-far-view).

Only terrain is withheld. The grid of latitude and longitude, the day and night shading and the winds cover the whole
world in survival too.

## Sharing what you have charted

A [paper map](Paper-maps) carries your charted area, recorded terrain, waypoints, automatic markers and prospecting
readings to another player.

## Saves

What each player has charted is kept in the world save. World Map 0.5.4 does not support worlds made with earlier
versions: start a new world.
