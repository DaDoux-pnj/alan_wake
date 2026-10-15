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

## Settings
`sbox/mount/alan_wake/alan_wake.json` is written with the defaults the first time the game is mounted. After editing it, toggle the alan_wake mount off and on (or restart s&box) and place the level again.
