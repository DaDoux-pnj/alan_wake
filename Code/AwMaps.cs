/// <summary>
/// The playable areas of the Alan Wake mount (its maps/*.scene). The New Game popup lists them by name, so the
/// names are spelled to read well there ("BrightFalls" shows as "Bright Falls"); the first one is the default.
/// </summary>
public enum AwMap
{
	BrightFalls,
	Level1,
	Level2,
	Level3,
	Level5,
	Level9,
	Level10,
	Level11,
	Level12,
	Level15,
	Level16,
	CauldronLake,
	NewYork,
	SouthRim,
	TheSignal,
	TheWriter,
}

public static class AwMaps
{
	/// <summary>
	/// The map to play, picked in the New Game popup. Publishing turns ConVars flagged as game settings into the
	/// popup's options (an enum becomes a dropdown), and starting the game sets them, so this works without the map
	/// switches that only sbox.game's servers can turn on. In the editor, set it from the console before pressing Play.
	/// </summary>
	[ConVar( "aw_map", ConVarFlags.GameSetting ), Title( "Map" )]
	public static AwMap Map { get; set; } = AwMap.BrightFalls;

	/// <summary>The mount's scene for a map.</summary>
	public static string PathOf( AwMap map )
	{
		var name = map switch
		{
			AwMap.Level1 => "level_1",
			AwMap.Level2 => "level2",
			AwMap.Level3 => "level3",
			AwMap.Level5 => "level_5",
			AwMap.Level9 => "level_9",
			AwMap.Level10 => "level_10",
			AwMap.Level11 => "level_11",
			AwMap.Level12 => "level_12",
			AwMap.Level15 => "level_15",
			AwMap.Level16 => "level_16",
			AwMap.CauldronLake => "se_cauldronlake",
			AwMap.NewYork => "new_york",
			AwMap.SouthRim => "sw_rim_south",
			AwMap.TheSignal => "dlc1_brightfalls",
			AwMap.TheWriter => "dlc2_cauldronlake",
			_ => "brightfalls",
		};

		return $"mount://alan_wake/maps/{name}.scene";
	}
}
