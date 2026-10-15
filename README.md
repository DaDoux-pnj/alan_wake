# alan_wake
A work in progress Alan Wake mount for S&box

## Layout
- `Mount/`: the mount itself, built as a standalone mount like the ones in `sbox/mount` (s&box doesn't compile this folder).
- `Assets/shaders/`: the mount's shaders (terrain, water, grass), installed next to the mount DLL.
- `Code/`: the grass and foliage renderer components. Cell prefabs reference them by name, so a game needs these two files to show grass and trees.
- `Editor/`: the `aw_list_levels`, `aw_place_level` and `aw_place_world` editor commands.

## Building the mount
Close s&box (it keeps loaded mount DLLs locked), then:

```
dotnet build Mount/alan_wake.mount.csproj
```

This builds `alan_wake.dll` and installs it with the shaders into `sbox/mount/alan_wake`; s&box loads it on the next start. If s&box isn't in `E:/SteamLibrary/steamapps/common/sbox`, add `-p:SboxDir=<your sbox folder>`. Rebuild after s&box updates that change the engine API.

## Maps
The mount turns each playable area into a map, with all of the area's parts: `mount://alan_wake/maps/brightfalls.scene` holds the four Bright Falls parts. The DLC areas are named `dlc1_brightfalls` and `dlc2_cauldronlake`. Each map gets a sun, a sky and a spawn point on open ground near the middle. Areas the player can't walk on (distant scenery only) get no map.

- **In this project:** open `scenes/play.scene` (also the startup scene), set the map on the Map object, and press Play. The game is multiplayer: the Player Spawner creates the lobby and gives everyone who joins their own player (`prefabs/aw_player.prefab`) on the map's spawn point, once the map has loaded. Players pick the map with the "Map" option of the New Game popup: the `aw_map` game setting in `Code/AwMaps.cs`, which s&box adds to the popup when the game is published (the popup's own map picker depends on switches only sbox.game's servers can turn on). In the editor, set it from the console before pressing Play, e.g. `aw_map TheSignal`.
- **Players need the mount:** a published game can't include it, so players need Alan Wake installed and a copy of the `sbox/mount/alan_wake` folder.
- **In other games:** any game that lets you pick a map lists them under Alan Wake. Grass and trees only show in games that include `Code/AwGrassRenderer.cs` and `Code/AwFoliageRenderer.cs`.

## Settings
`sbox/mount/alan_wake/alan_wake.json` is written with the defaults the first time the game is mounted. After editing it, toggle the alan_wake mount off and on (or restart s&box) and place the level again.
