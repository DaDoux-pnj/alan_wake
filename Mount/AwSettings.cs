using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Sandbox.Diagnostics;

namespace Sandbox.Mounting;

/// <summary>Marks a static property of <see cref="AlanWakeMount"/> as a setting of <see cref="AwSettings"/>.</summary>
[AttributeUsage( AttributeTargets.Property )]
sealed class AwSettingAttribute : Attribute
{
	public AwSettingAttribute( string key, string help )
	{
		Key = key;
		Help = help;
	}

	public string Key { get; }
	public string Help { get; }
}

/// <summary>
/// The mount's settings. A mount DLL can't register ConVars, so they come from alan_wake.json next to alan_wake.dll,
/// read every time the game is mounted (after editing it, toggle the mount or restart s&amp;box and place the level
/// again). A missing file is written with the defaults, a missing key keeps its default; // comments are allowed.
/// </summary>
static class AwSettings
{
	public const string FileName = "alan_wake.json";

	sealed class Setting
	{
		public PropertyInfo Property;
		public string Key;
		public string Help;
		public object Default;
	}

	static List<Setting> _all;

	/// <summary>Every [AwSetting] property, with its value from before the file was ever applied.</summary>
	static List<Setting> All => _all ??= typeof( AlanWakeMount )
		.GetProperties( BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static )
		.Select( p => (Property: p, Attribute: p.GetCustomAttribute<AwSettingAttribute>()) )
		.Where( x => x.Attribute is not null )
		.Select( x => new Setting { Property = x.Property, Key = x.Attribute.Key, Help = x.Attribute.Help, Default = x.Property.GetValue( null ) } )
		.ToList();

	/// <summary>The settings file, next to the mount DLL (null if the DLL wasn't loaded from a file).</summary>
	public static string FilePath
	{
		get
		{
			var directory = System.IO.Path.GetDirectoryName( typeof( AwSettings ).Assembly.Location );
			return string.IsNullOrEmpty( directory ) ? null : System.IO.Path.Combine( directory, FileName );
		}
	}

	/// <summary>Resets every setting to its default, then applies the file (writing the defaults if it's missing).</summary>
	public static void Load( Logger log )
	{
		ResetToDefaults();

		var path = FilePath;
		if ( path is null )
			return;

		try
		{
			if ( !File.Exists( path ) )
			{
				File.WriteAllText( path, DefaultFile() );
				log.Info( $"Alan Wake: wrote the default settings to {path}" );
				return;
			}

			var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
			using var json = JsonDocument.Parse( File.ReadAllText( path ), options );
			foreach ( var property in json.RootElement.EnumerateObject() )
			{
				var setting = All.FirstOrDefault( s => s.Key.Equals( property.Name, StringComparison.OrdinalIgnoreCase ) );
				if ( setting is null )
					log.Warning( $"{FileName}: unknown setting '{property.Name}'" );
				else if ( !TryApply( setting, property.Value ) )
					log.Warning( $"{FileName}: '{property.Name}' should be {Describe( setting.Property.PropertyType )}, keeping {Format( setting.Default )}" );
			}

			log.Info( $"Alan Wake: settings from {path}" );
		}
		catch ( Exception ex )
		{
			ResetToDefaults();
			log.Warning( $"{FileName}: {ex.Message} (using the defaults)" );
		}
	}

	static void ResetToDefaults()
	{
		foreach ( var setting in All )
			setting.Property.SetValue( null, setting.Default );
	}

	static bool TryApply( Setting setting, JsonElement value )
	{
		var type = setting.Property.PropertyType;
		object result = null;

		if ( type == typeof( bool ) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False) )
			result = value.GetBoolean();
		else if ( type == typeof( int ) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble( out var whole ) )
			result = (int)Math.Round( whole );
		else if ( type == typeof( float ) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble( out var number ) )
			result = (float)number;

		if ( result is null )
			return false;

		setting.Property.SetValue( null, result );
		return true;
	}

	/// <summary>The defaults as a commented JSON file.</summary>
	static string DefaultFile()
	{
		var text = new StringBuilder();
		text.AppendLine( "// Alan Wake mount settings, read every time the game is mounted. After editing, toggle the alan_wake" );
		text.AppendLine( "// mount off and on (or restart s&box) and place the level again. Delete this file to get the defaults back." );
		text.AppendLine( "{" );
		for ( int i = 0; i < All.Count; i++ )
		{
			var setting = All[i];
			if ( i > 0 )
				text.AppendLine();
			if ( !string.IsNullOrEmpty( setting.Help ) )
				text.AppendLine( $"\t// {setting.Help}" );
			text.AppendLine( $"\t\"{setting.Key}\": {Format( setting.Default )}{(i < All.Count - 1 ? "," : "")}" );
		}
		text.AppendLine( "}" );
		return text.ToString();
	}

	static string Format( object value ) => value switch
	{
		bool b => b ? "true" : "false",
		IFormattable formattable => formattable.ToString( null, CultureInfo.InvariantCulture ),
		_ => $"{value}",
	};

	static string Describe( Type type ) => type == typeof( bool ) ? "true or false" : type == typeof( int ) ? "a whole number" : "a number";
}
