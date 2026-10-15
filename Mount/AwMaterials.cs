using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Sandbox.Mounting;

[StructLayout( LayoutKind.Sequential )]
struct AwVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector4 TexCoord;
}

/// <summary>
/// Terrain vertex for shaders/aw_terrain.shader: TEXCOORD4 = layer0 uv (xy) + layer1 uv (zw),
/// TEXCOORD5 = layer2 uv (xy) + blend atlas uv (zw). TEXCOORD0/1 repeat layer0/1 for the engine's own use.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
struct AwTerrainVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord0;
	[VertexLayout.TexCoord( 1 )] public Vector2 TexCoord1;
	[VertexLayout.TexCoord( 4 )] public Vector4 LayerUv01;
	[VertexLayout.TexCoord( 5 )] public Vector4 LayerUv2Blend;
}

/// <summary>Grass clump vertex for shaders/aw_grass.shader: colour red = wind weight.</summary>
[StructLayout( LayoutKind.Sequential )]
struct AwGrassVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
	[VertexLayout.Color] public Color Color;
}

/// <summary>
/// Water vertex for shaders/aw_water.shader: the tangent points downstream, colour = (flow speed in world units/s,
/// wave strength, foam, unused).
/// </summary>
[StructLayout( LayoutKind.Sequential )]
struct AwWaterVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
	[VertexLayout.Color] public Color Color;
}

/// <summary>
/// Creates and caches s&amp;box materials/textures for Alan Wake assets. Props use the stock complex shader,
/// windows the stock glass shader, terrain uses shaders/aw_terrain.shader (3 layer height blend, same maths as the
/// game's terrain baker).
/// </summary>
static class AwMaterials
{
	const string ObjectShader = "shaders/complex.shader";
	const string GlassShader = "shaders/glass.shader";
	const string TerrainShader = "shaders/aw_terrain.shader";
	const string WaterShader = "shaders/aw_water.shader";
	const string GrassShader = "shaders/aw_grass.shader";

	static readonly object Lock = new();
	static readonly Dictionary<string, Material> Materials = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, Texture> ColorTextures = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, Texture> NormalTextures = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<int, Texture> FlatNormals = new();
	static readonly Dictionary<uint, Texture> SolidTextures = new();
	static int _counter;

	public static void Clear()
	{
		lock ( Lock )
		{
			Materials.Clear();
			ColorTextures.Clear();
			NormalTextures.Clear();
			FlatNormals.Clear();
			SolidTextures.Clear();
		}
	}

	/// <summary>1x1 texture of one RGBA value.</summary>
	static Texture Solid( byte r, byte g, byte b, byte a )
	{
		uint key = (uint)(r | g << 8 | b << 16 | a << 24);
		lock ( Lock )
		{
			if ( SolidTextures.TryGetValue( key, out var t ) )
				return t;
			t = Texture.Create( 1, 1 ).WithData( new byte[] { r, g, b, a } ).Finish();
			SolidTextures[key] = t;
			return t;
		}
	}

	/// <summary>Native DXT upload of a game texture (with its own mip chain). Null if missing/unsupported.</summary>
	public static Texture Color( AlanWakeMount host, string path )
	{
		path = AwArchive.NormalizePath( path );
		if ( string.IsNullOrEmpty( path ) ) return null;

		lock ( Lock )
		{
			if ( ColorTextures.TryGetValue( path, out var cached ) )
				return cached;
		}

		Texture texture = null;
		try
		{
			var bytes = host.Archive.TryRead( path );
			if ( bytes is not null )
				texture = TextureLoader.FromDds( AwTextureFile.ToDds( bytes ) );
		}
		catch ( Exception ex )
		{
			if ( AlanWakeMount.DebugLog ) Log.Warning( $"[AW] texture '{path}': {ex.Message}" );
		}

		lock ( Lock ) ColorTextures[path] = texture;
		return texture;
	}

	/// <summary>
	/// Game normal maps are DXT5nm (X in alpha, Y in green). Decode them into a regular RGB normal map with
	/// roughness in alpha, which is what the complex shader samples from g_tNormal. A few uncompressed maps (the lakes'
	/// water) store the normal with Y up instead: the slopes in red and blue, up in green.
	/// With a specular map (and aw_specular_maps), roughness varies per pixel the way the game's highlight does: the
	/// game scales its highlight by specularMap * g_vSpecularMultiplier, so where that's dark the surface is matte (1)
	/// and where it's bright it gets the material's <paramref name="roughness"/>.
	/// </summary>
	public static Texture Normal( AlanWakeMount host, string path, float roughness, string specularPath = null, float[] specularMultiplier = null )
	{
		path = AwArchive.NormalizePath( path );
		if ( !AlanWakeMount.NormalMaps )
			path = "";
		specularPath = AlanWakeMount.SpecularMaps ? AwArchive.NormalizePath( specularPath ) : null;

		byte r = (byte)Math.Clamp( roughness * 255f, 0, 255 );
		if ( string.IsNullOrEmpty( path ) && string.IsNullOrEmpty( specularPath ) )
			return FlatNormal( r );

		float multiplier = SpecularIntensity( specularMultiplier );
		var key = $"{path}|{r / 16}|{specularPath}|{multiplier:0.00}";
		lock ( Lock )
		{
			if ( NormalTextures.TryGetValue( key, out var cached ) )
				return cached ?? FlatNormal( r );
		}

		Texture texture = null;
		try
		{
			byte[] rgba = null;
			int w = 1, h = 1;
			var bytes = string.IsNullOrEmpty( path ) ? null : host.Archive.TryRead( path );
			if ( bytes is not null )
			{
				var header = AwTextureFile.ReadHeader( bytes );
				rgba = AwTextureFile.DecodeRgba( bytes, AlanWakeMount.NormalMapMaxSize, out w, out h );
				bool swizzled = AwTextureFile.IsDxt5( header.Format );
				bool yUp = !swizzled && IsYUpNormalMap( rgba );
				bool flipGreen = AlanWakeMount.NormalMapFlipGreen;
				for ( int i = 0; i < w * h; i++ )
				{
					int o = i * 4;
					float x = (swizzled ? rgba[o + 3] : rgba[o]) / 255f * 2f - 1f;
					float y = (yUp ? rgba[o + 2] : rgba[o + 1]) / 255f * 2f - 1f;
					if ( flipGreen ) y = -y;
					float z;
					if ( yUp )
					{
						z = rgba[o + 1] / 255f * 2f - 1f;
						float length = MathF.Sqrt( x * x + y * y + z * z );
						if ( length > 1e-4f ) { x /= length; y /= length; z /= length; }
					}
					else
					{
						z = MathF.Sqrt( MathF.Max( 0f, 1f - x * x - y * y ) );
					}
					rgba[o + 0] = (byte)Math.Clamp( (x * 0.5f + 0.5f) * 255f, 0, 255 );
					rgba[o + 1] = (byte)Math.Clamp( (y * 0.5f + 0.5f) * 255f, 0, 255 );
					rgba[o + 2] = (byte)Math.Clamp( (z * 0.5f + 0.5f) * 255f, 0, 255 );
					rgba[o + 3] = r;
				}
			}

			var specularBytes = string.IsNullOrEmpty( specularPath ) ? null : host.Archive.TryRead( specularPath );
			if ( specularBytes is not null )
			{
				var specular = AwTextureFile.DecodeRgba( specularBytes, AlanWakeMount.NormalMapMaxSize, out int sw, out int sh );
				if ( rgba is null )
				{
					// No normal map: a flat one the size of the specular map, to carry its roughness.
					w = sw;
					h = sh;
					rgba = new byte[w * h * 4];
					for ( int o = 0; o < rgba.Length; o += 4 ) { rgba[o] = 128; rgba[o + 1] = 128; rgba[o + 2] = 255; }
				}

				for ( int y = 0; y < h; y++ )
				for ( int x = 0; x < w; x++ )
				{
					int s = ((y * sh / h) * sw + x * sw / w) * 4;
					float intensity = Math.Clamp( (specular[s] * 0.299f + specular[s + 1] * 0.587f + specular[s + 2] * 0.114f) / 255f * multiplier, 0f, 1f );
					rgba[(y * w + x) * 4 + 3] = (byte)Math.Clamp( (1f + (roughness - 1f) * intensity) * 255f, 0, 255 );
				}
			}

			if ( rgba is not null )
			{
				texture = Texture.Create( w, h, ImageFormat.RGBA8888 )
					.WithData( rgba )
					.WithMips()
					.WithStaticUsage()
					.Finish();
			}
		}
		catch ( Exception ex )
		{
			if ( AlanWakeMount.DebugLog ) Log.Warning( $"[AW] normal map '{path}' / specular map '{specularPath}': {ex.Message}" );
		}

		lock ( Lock ) NormalTextures[key] = texture;
		return texture ?? FlatNormal( r );
	}

	/// <summary>The brightness of g_vSpecularMultiplier (the game's highlight colour/intensity), 1 when absent.</summary>
	static float SpecularIntensity( float[] multiplier )
		=> multiplier is { Length: >= 3 } ? Math.Max( 0f, multiplier[0] * 0.299f + multiplier[1] * 0.587f + multiplier[2] * 0.114f )
			: multiplier is { Length: >= 1 } ? Math.Max( 0f, multiplier[0] ) : 1f;

	/// <summary>
	/// True for normal maps stored with Y up (green brighter than blue on average); tangent space maps keep up in blue.
	/// </summary>
	static bool IsYUpNormalMap( byte[] rgba )
	{
		long green = 0, blue = 0;
		int count = 0;
		for ( int o = 0; o + 3 < rgba.Length; o += 4 * 7 )
		{
			green += rgba[o + 1];
			blue += rgba[o + 2];
			count++;
		}

		return count > 0 && (green - blue) / (float)count > 38f;
	}

	static Texture FlatNormal( byte roughness )
	{
		int key = roughness / 16;
		lock ( Lock )
		{
			if ( FlatNormals.TryGetValue( key, out var t ) )
				return t;
			t = Texture.Create( 1, 1 ).WithData( new byte[] { 128, 128, 255, (byte)(key * 16 + 8) } ).Finish();
			FlatNormals[key] = t;
			return t;
		}
	}

	/// <summary>Water.rfx (lakes, ponds) and River.rfx (rivers, waterfalls): built with <see cref="AwWaterVertex"/>.</summary>
	public static bool IsWater( AwMeshFile.MaterialDef def )
		=> def is not null && (def.Shader.StartsWith( "Water", StringComparison.OrdinalIgnoreCase ) || def.Shader.StartsWith( "River", StringComparison.OrdinalIgnoreCase ));

	public static Material ForMesh( AlanWakeMount host, AwMeshFile.MaterialDef def )
	{
		if ( def.Shader.StartsWith( "Glass", StringComparison.OrdinalIgnoreCase ) )
			return ForGlass( host, def );
		if ( IsWater( def ) )
			return ForWater( host, def );

		var colorPath = def.Texture( "g_sColorMap" ) ?? def.Texture( "g_sVideoMap" ) ?? "";
		var normalPath = def.Texture( "g_sNormalMap" ) ?? "";
		var tint = def.Vector( "g_vColorMultiplier" );
		float roughness = RoughnessFor( def );
		bool foliage = def.Shader.StartsWith( "Foliage", StringComparison.OrdinalIgnoreCase );
		bool cutout = def.AlphaTest || (foliage && HasAlpha( host, colorPath ));

		// Per pixel roughness from the specular map, for the game's plain materials (chrome and car paint keep theirs).
		bool plain = def.Shader.StartsWith( "StandardMaterial", StringComparison.OrdinalIgnoreCase ) || def.Shader.StartsWith( "Character", StringComparison.OrdinalIgnoreCase );
		var specularPath = plain ? def.Texture( "g_sSpecularMap" ) : null;
		var specularMultiplier = def.Vector( "g_vSpecularMultiplier" );

		var key = $"{def.Shader}|{def.Flags}|{def.Blend}|{def.Cull}|{colorPath}|{normalPath}|{(tint is null ? "" : string.Join( ',', tint ))}|{roughness:0.00}|{specularPath}|{(specularMultiplier is null ? "" : string.Join( ',', specularMultiplier ))}";
		lock ( Lock )
		{
			if ( Materials.TryGetValue( key, out var cached ) )
				return cached;
		}

		var material = Material.Create( $"alan_wake/materials/m{System.Threading.Interlocked.Increment( ref _counter )}", ObjectShader, false );

		var color = Color( host, colorPath );
		material.Set( "g_tColor", color ?? Texture.White );

		material.Set( "g_tNormal", Normal( host, normalPath, roughness, specularPath, specularMultiplier ) );
		material.Set( "g_tAmbientOcclusion", Texture.White );
		material.Set( "g_flMetalness", def.Shader.StartsWith( "Chrome", StringComparison.OrdinalIgnoreCase ) ? 0.5f : 0f );
		material.Set( "g_flRoughnessScaleFactor", 1.0f );

		var c = tint is { Length: >= 3 } ? new Color( tint[0], tint[1], tint[2], tint.Length > 3 ? tint[3] : 1f ) : global::Color.White;
		material.Set( "g_vColorTint", c );

		if ( cutout )
		{
			material.SetFeature( "F_ALPHA_TEST", 1 );
			material.Set( "g_flAlphaTestReference", 0.5f );
		}
		else if ( def.Translucent )
		{
			material.SetFeature( "F_TRANSLUCENT", 1 );
		}

		if ( def.TwoSided || cutout )
			material.SetFeature( "F_RENDER_BACKFACES", 1 );

		lock ( Lock ) Materials[key] = material;
		return material;
	}

	/// <summary>
	/// The game's StandardMaterial has no environment reflections: only a Phong-like light highlight with exponent
	/// 3 / (1 - g_fGlossiness), scaled by the specular map. s&amp;box reflects the sky instead, nearly completely at
	/// grazing angles when smooth, which turned distant glossy surfaces (bridge decks) into sky-coloured mirrors.
	/// Use the roughness matching the game's highlight, floored so environment reflections stay faint; materials
	/// without a specular map have no highlight at all. Chrome and car paint do reflect in the game and stay smoother.
	/// </summary>
	static float RoughnessFor( AwMeshFile.MaterialDef def )
	{
		float gloss = Math.Clamp( def.Value( "g_fGlossiness", 0.2f ), 0f, 0.98f );
		if ( def.Shader.StartsWith( "Chrome", StringComparison.OrdinalIgnoreCase ) )
			return Math.Clamp( 1f - gloss, 0.15f, 1f );
		if ( def.Shader.StartsWith( "CarPaint", StringComparison.OrdinalIgnoreCase ) )
			return 0.35f;
		if ( def.Texture( "g_sSpecularMap" ) is null )
			return 0.85f;

		float exponent = 3f / (1f - gloss);
		return Math.Clamp( MathF.Sqrt( 2f / (exponent + 2f) ), 0.55f, 1f );
	}

	/// <summary>
	/// Lakes (Water.rfx) and rivers (River.rfx) on shaders/aw_water.shader (Water Tool 2's water): the game's water normal
	/// map drifting over a depth tinted, refracting surface with fresnel and screen space reflections. Rivers carry
	/// their normals and foam downstream (see <see cref="AwModelLoader.BuildWaterMesh"/>).
	/// </summary>
	static Material ForWater( AlanWakeMount host, AwMeshFile.MaterialDef def )
	{
		bool river = def.Shader.StartsWith( "River", StringComparison.OrdinalIgnoreCase );
		var normalPath = def.Texture( "g_sNormalMap" ) ?? "";
		var key = $"water|{river}|{normalPath}";
		lock ( Lock )
		{
			if ( Materials.TryGetValue( key, out var cached ) )
				return cached;
		}

		var material = Material.Create( $"alan_wake/materials/m{System.Threading.Interlocked.Increment( ref _counter )}", WaterShader, false );
		material.SetFeature( "F_TRANSLUCENT", 1 );
		material.SetFeature( "F_RENDER_BACKFACES", 1 );
		material.SetFeature( "F_DO_NOT_CAST_SHADOWS", 1 );

		var normal = Normal( host, normalPath, 0f );
		material.Set( "g_tMainNormal", normal );
		material.Set( "g_tSecondNormal", normal );
		material.Set( "g_vNormalTiling", river ? new Vector2( 0.01f, 0.01f ) : new Vector2( 0.006f, 0.006f ) );
		material.Set( "g_flSpeedMainNormal", 50f );
		material.Set( "g_flSpeedSecondNormal", -25f );
		material.Set( "g_flNormalStrength", river ? 0.06f : 0.04f );
		material.Set( "g_flFlowCycle", 2f );

		material.Set( "g_flRefractionStrength", 0.1f );
		material.Set( "g_vShallowColor", new Color( 0.0f, 0.0f, 0.0f, 0.98f ) );
		material.Set( "g_vDeepColor", new Color( 0.0f, 0.0f, 0.0f, 0.8f ) );
		material.Set( "g_flDepthMax", 1000f );
		material.Set( "g_flDepthMultiplier", 0.7f );
		material.Set( "g_flDepthFalloff", 0.5f );
		material.Set( "g_flDepthBlend", 1f );
		material.Set( "g_flShoreOpacity", 0f );
		material.Set( "g_flShoreOpacityRange", 10f );

		material.Set( "g_flFoamIntensity", 0f );
		material.Set( "g_flFlowFoamIntensity", 1f );
		material.Set( "g_flFlowFoamScale", 6f );
		material.Set( "g_vFoamColor", global::Color.White );

		material.Set( "g_flFresnelBias", 0.2f );
		material.Set( "g_flFresnelPower", 5f );
		material.Set( "g_vFresnelColor", new Color( 0.678f, 0.647f, 0.665f, 1f ) );

		material.Set( "g_bUseScreenSpaceReflection", true );
		material.Set( "g_flReflectionStrength", 1f );
		material.Set( "g_flReflectionStepSize", 500f );
		material.Set( "g_flFoamReflectionStrength", 0f );

		material.Set( "g_flRoughness", 0f );
		material.Set( "g_flContrast", 1.3f );

		lock ( Lock ) Materials[key] = material;
		return material;
	}

	/// <summary>
	/// Glass.rfx (every window): a fresnel weighted reflection (20% head-on, rising at grazing angles) over whatever
	/// sits behind the pane (curtains, interiors), with an optional dirt layer (g_sDirtMap, alpha = coverage, first
	/// uv set). Mapped onto s&amp;box's glass shader: refraction of the scene behind, environment reflections and the
	/// dirt as its overlay layer. aw_glass_refraction 0 switches to its cheaper alpha blended mode.
	/// </summary>
	static Material ForGlass( AlanWakeMount host, AwMeshFile.MaterialDef def )
	{
		var dirtPath = AwArchive.NormalizePath( def.Texture( "g_sDirtMap" ) ?? "" );
		bool refraction = AlanWakeMount.GlassRefraction;
		var key = $"glass|{dirtPath}|{def.TwoSided}|{refraction}";
		lock ( Lock )
		{
			if ( Materials.TryGetValue( key, out var cached ) )
				return cached;
		}

		var material = Material.Create( $"alan_wake/materials/m{System.Threading.Interlocked.Increment( ref _counter )}", GlassShader, false );

		// Head-on the game lets about 80% of the background through; the simple mode blends by colour alpha.
		material.Set( "g_tColor", refraction ? Texture.White : Solid( 255, 255, 255, 40 ) );
		material.Set( "g_flTintColor", new Vector3( 0.85f, 0.85f, 0.85f ) );
		material.Set( "g_tNormal", FlatNormal( 255 ) );
		material.Set( "g_tRma", Solid( 13, 0, 255, 0 ) ); // roughness 0.05, no metalness, no occlusion
		if ( !refraction )
			material.SetFeature( "F_GLASS_QUALITY", 1 );

		var dirt = Color( host, dirtPath );
		if ( dirt is not null )
		{
			material.SetFeature( "F_OVERLAY_LAYER", 1 );
			material.Set( "g_tColorB", dirt );
			material.Set( "g_tNormalB", FlatNormal( 255 ) );
			material.Set( "g_tRmaB", Solid( 204, 0, 255, 0 ) ); // matte smudges
			material.Set( "g_flTintColorB", Vector3.One );
		}

		if ( def.TwoSided )
			material.SetFeature( "F_RENDER_BACKFACES", 1 );

		lock ( Lock ) Materials[key] = material;
		return material;
	}

	/// <summary>
	/// Alpha-tested, two sided material showing the foliage billboard atlas (impostors). The game shades its billboards
	/// with a precomputed lighting atlas we don't use; aw_foliage_impostor_brightness evens them out with the meshes.
	/// </summary>
	public static Material ForImpostor( AlanWakeMount host, string atlasPath )
	{
		float brightness = Math.Clamp( AlanWakeMount.FoliageImpostorBrightness, 0f, 2f );
		var key = $"impostor|{atlasPath}|{brightness:0.00}";
		lock ( Lock )
		{
			if ( Materials.TryGetValue( key, out var cached ) )
				return cached;
		}

		var material = Material.Create( $"alan_wake/materials/m{System.Threading.Interlocked.Increment( ref _counter )}", ObjectShader, false );
		material.Set( "g_tColor", Color( host, atlasPath ) ?? Texture.White );
		material.Set( "g_tNormal", FlatNormal( 230 ) );
		material.Set( "g_tAmbientOcclusion", Texture.White );
		material.Set( "g_flMetalness", 0f );
		material.Set( "g_flRoughnessScaleFactor", 1.0f );
		material.Set( "g_vColorTint", new Color( brightness, brightness, brightness, 1f ) );
		material.SetFeature( "F_ALPHA_TEST", 1 );
		material.Set( "g_flAlphaTestReference", 0.5f );
		material.SetFeature( "F_RENDER_BACKFACES", 1 );

		lock ( Lock ) Materials[key] = material;
		return material;
	}

	/// <summary>
	/// The game's grass clumps (one atlas for all of them) on shaders/aw_grass.shader: alpha tested, two sided, the tips
	/// swaying in the wind (aw_grass_wind), thinning out towards aw_grass_distance.
	/// </summary>
	public static Material ForGrass( AlanWakeMount host, string atlasPath )
	{
		float distance = MathF.Max( 1f, AlanWakeMount.GrassDistance ) * AwSpace.Scale;
		float wind = Math.Clamp( AlanWakeMount.GrassWind, 0f, 20f );
		var key = $"grass|{atlasPath}|{distance:0}|{wind:0.00}";
		lock ( Lock )
		{
			if ( Materials.TryGetValue( key, out var cached ) )
				return cached;
		}

		var material = Material.Create( $"alan_wake/materials/m{System.Threading.Interlocked.Increment( ref _counter )}", GrassShader, false );
		material.Set( "g_tColor", Color( host, atlasPath ) ?? Texture.White );
		material.Set( "g_tNormal", FlatNormal( 255 ) );
		material.Set( "g_tRma", Solid( 230, 0, 255, 0 ) ); // rough, no metalness, no occlusion
		material.Set( "g_flTintColor", Vector3.One );
		material.SetFeature( "F_ALPHA_TEST", 1 );
		material.Set( "g_flAlphaTestReference", 0.5f );
		material.SetFeature( "F_RENDER_BACKFACES", 1 );

		material.Set( "g_flGrassWindStrength", wind );
		material.Set( "g_flGrassWindSpeed", 1.2f );
		material.Set( "g_flGrassWindWaveScale", 0.004f );
		material.Set( "g_vGrassWindDirection", new Vector2( 0.8f, 0.6f ) );
		material.Set( "g_flGrassFadeStart", distance * 0.75f );
		material.Set( "g_flGrassFadeEnd", distance );

		lock ( Lock ) Materials[key] = material;
		return material;
	}

	/// <summary>True when the game texture is stored with an alpha channel (DXT5 or 32 bit).</summary>
	static bool HasAlpha( AlanWakeMount host, string path )
	{
		var header = host.Archive.ReadPrefix( path, 32 );
		if ( header is null || header.Length < 32 )
			return false;
		uint format = BitConverter.ToUInt32( header, 4 );
		return !AwTextureFile.IsDxt1( format );
	}

	public static Material ForTerrain( AlanWakeMount host, string layer0, string layer1, string layer2, Texture blendAtlas, string key )
	{
		lock ( Lock )
		{
			if ( Materials.TryGetValue( key, out var cached ) )
				return cached;
		}

		var material = Material.Create( $"alan_wake/terrain/t{System.Threading.Interlocked.Increment( ref _counter )}", TerrainShader, false );
		material.Set( "g_tLayer0", Color( host, layer0 ) ?? Texture.White );
		material.Set( "g_tLayer1", Color( host, layer1 ) ?? Texture.White );
		material.Set( "g_tLayer2", Color( host, layer2 ) ?? Texture.White );
		material.Set( "g_tBlend", blendAtlas );
		material.Set( "g_flBrightness", AlanWakeMount.TerrainBrightness );

		lock ( Lock ) Materials[key] = material;
		return material;
	}
}
