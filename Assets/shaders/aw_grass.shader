//=========================================================================================================================
// Alan Wake grass: s&box's foliage shader with the game's grass behaviour (wind on the blade tips, baked shading as
// ambient occlusion, a soft fade at the grass distance). Instances come from AwGrassRenderer.
//=========================================================================================================================
HEADER
{
	Description = "Alan Wake Grass";
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_ALPHA_TEST, 0..1, "Rendering" );
	Feature( F_TRANSMISSIVE, 0..1, "Rendering" );
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"

	// Red: how much the wind moves this vertex (0 at the roots, 1 at the tips).
	float4 vColor : COLOR0 < Semantic( Color ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	// x: wind weight, y: the game's baked brightness at this clump.
	float4 vColor : COLOR0;
};

VS
{
	#include "common/vertex.hlsl"

	float g_flGrassWindStrength < Default( 3.0 ); Range( 0.0, 20.0 ); UiGroup( "Grass Wind" ); >;
	float g_flGrassWindSpeed < Default( 1.2 ); Range( 0.0, 10.0 ); UiGroup( "Grass Wind" ); >;
	float g_flGrassWindWaveScale < Default( 0.004 ); Range( 0.0, 0.05 ); UiGroup( "Grass Wind" ); >;
	float2 g_vGrassWindDirection < Default2( 0.8, 0.6 ); UiGroup( "Grass Wind" ); >;

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );

		float3x4 matObjectToWorld = GetTransformMatrix( i.nInstanceTransformID );
		float3 vOriginWs = mul( matObjectToWorld, float4( 0, 0, 0, 1 ) );

		// The mount stores the game's baked shading of the spot in the instance scale: 1 + darkness / 1000.
		float flScale = length( float3( matObjectToWorld[0].x, matObjectToWorld[1].x, matObjectToWorld[2].x ) );
		float flBrightness = 1.0 - saturate( ( flScale - 1.0 ) * 1000.0 );

		// Wind: the tips sway along the wind, in gusts rolling across the field, with a little flutter on top.
		float flWindWeight = i.vColor.r;
		float2 vWindDir = normalize( g_vGrassWindDirection + float2( 0.0001, 0.0 ) );
		float flWave = dot( vOriginWs.xy, vWindDir ) * g_flGrassWindWaveScale;
		float flWindTime = g_flTime * g_flGrassWindSpeed;
		float flGust = sin( flWindTime - flWave ) * 0.6 + sin( flWindTime * 2.37 - flWave * 1.9 + vOriginWs.x * 0.01 ) * 0.4;
		float flFlutter = sin( flWindTime * 5.3 + dot( vOriginWs.xy, float2( 0.37, 0.61 ) ) ) * 0.15;
		float3 vWindOffset = float3( vWindDir * ( flGust * 0.5 + 0.5 + flFlutter ), 0.0 ) * g_flGrassWindStrength * flWindWeight;

		o.vPositionWs = mul( matObjectToWorld, float4( i.vPositionOs.xyz, 1.0 ) ) + vWindOffset;
		o.vPositionPs.xyzw = Position3WsToPs( o.vPositionWs.xyz );
		o.vColor = float4( flWindWeight, flBrightness, 0.0, 1.0 );

		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"
	#include "common/classes/Light.hlsl"

	StaticCombo( S_ALPHA_TEST, F_ALPHA_TEST, Sys( ALL ) );
	StaticCombo( S_TRANSMISSIVE, F_TRANSMISSIVE, Sys( ALL ) );

	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	#if S_ALPHA_TEST
		TextureAttribute( LightSim_Opacity_A, g_tColor );
		float g_flAlphaDistanceStart < Default( 500.0 ); Range( 0.0, 5000.0 ); UiGroup( "Alpha Test" ); >;
		float g_flAlphaDistanceEnd < Default( 2000.0 ); Range( 0.0, 10000.0 ); UiGroup( "Alpha Test" ); >;
	#endif

	float g_flWrapAmount < Default( 0.5 ); Range( 0.0, 1.0 ); UiGroup( "Foliage" ); >;
	float g_flWrapStrength < Default( 0.3 ); Range( 0.0, 1.0 ); UiGroup( "Foliage" ); >;
	float g_flMinRoughness < Default( 0.5 ); Range( 0.0, 1.0 ); UiGroup( "Foliage" ); >;
	float g_flGrassNormalUp < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Foliage" ); >;

	// Inches from the camera: the grass thins out between these two distances.
	float g_flGrassFadeStart < Default( 750.0 ); Range( 0.0, 10000.0 ); UiGroup( "Grass Fade" ); >;
	float g_flGrassFadeEnd < Default( 1000.0 ); Range( 0.0, 10000.0 ); UiGroup( "Grass Fade" ); >;

	#if S_TRANSMISSIVE
		CreateInputTexture2D( TextureTransmissiveColor, Srgb, 8, "", "_color", "Material,10/60", Default3( 1.0, 1.0, 1.0 ) );
		Texture2D g_tTransmissiveColor < Channel( RGB, Box( TextureTransmissiveColor ), Srgb ); OutputFormat( BC7 ); SrgbRead( true ); >;
		float g_flTransmissionScale < Default( 1.0 ); Range( 0.0, 10.0 ); UiGroup( "Transmissive" ); >;
	#endif

	#if S_ALPHA_TEST
	// Relaxes the alpha cutoff with distance so thin blades don't vanish (same as the stock foliage shader).
	float DistanceAlphaScale( float dist )
	{
		float distFactor = saturate( ( dist - g_flAlphaDistanceStart ) / max( g_flAlphaDistanceEnd - g_flAlphaDistanceStart, 0.001 ) );
		float alphaRef = lerp( g_flAlphaTestReference, 0.1, distFactor );
		return g_flAlphaTestReference / max( alphaRef, 0.001 );
	}
	#endif

	// Wrap diffuse and back-scatter through the blades, added as emission (from the stock foliage shader).
	void ApplyGrassLighting( inout Material m, float3 lightDir, float3 lightColor, float visibility, float3 viewDir, float3 transmissionTint )
	{
		const float flSunlightBoost = 1.5;
		const float flSunlightShadowLeak = 0.2;
		const float flScatterDistortion = 0.2;
		const float flScatterPower = 3.0;
		const float flScatterAmbient = 0.1;

		float scatterShadow = lerp( flSunlightShadowLeak, 1.0, visibility );

		if ( g_flWrapStrength > 0.0 )
		{
			float NdotL = dot( m.Normal, lightDir );
			float wrapped = saturate( ( NdotL + g_flWrapAmount ) / ( 1.0 + g_flWrapAmount ) );
			float wrapContribution = max( wrapped - saturate( NdotL ), 0.0 );
			m.Emission += m.Albedo * lightColor * wrapContribution * g_flWrapStrength * flSunlightBoost;
		}

		#if S_TRANSMISSIVE
			float3 scatterDir = normalize( lightDir + m.Normal * flScatterDistortion );
			float backlit = pow( saturate( dot( viewDir, -scatterDir ) ), flScatterPower );
			m.Emission += m.Albedo * transmissionTint * lightColor * ( backlit + flScatterAmbient ) * scatterShadow * g_flTransmissionScale * flSunlightBoost;
		#endif
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		#if S_TRANSMISSIVE
			float3 transmissiveColor = g_tTransmissiveColor.Sample( TextureFiltering, i.vTextureCoords.xy ).rgb;
		#else
			float3 transmissiveColor = 1.0;
		#endif

		Material m = Material::From( i );

		m.Roughness = max( m.Roughness, g_flMinRoughness );
		m.Normal = normalize( lerp( m.Normal, float3( 0, 0, 1 ), g_flGrassNormalUp ) );

		// The game shades its grass ambient light by the terrain's baked brightness (dark under porches and trees).
		m.AmbientOcclusion *= i.vColor.y;

		float3 viewDir = normalize( g_vCameraPositionWs - m.WorldPosition );
		float dist = length( i.vPositionWithOffsetWs.xyz );

		#if S_ALPHA_TEST
			m.Opacity *= DistanceAlphaScale( dist );
			m.Opacity *= saturate( ( g_flGrassFadeEnd - dist ) / max( g_flGrassFadeEnd - g_flGrassFadeStart, 1.0 ) );
		#endif

		#if S_TRANSMISSIVE
			if ( g_DirectionalLightEnabled )
			{
				float3 sunDir = -normalize( g_DirectionalLightDirection.xyz );
				float3 sunColor = g_DirectionalLightColor.rgb;
				float sunVis = DirectionalLightShadow::GetVisibility( m.WorldPosition, m.ScreenPosition );
				ApplyGrassLighting( m, sunDir, sunColor, sunVis, viewDir, transmissiveColor );
			}
		#endif

		return ShadingModelStandard::Shade( i, m );
	}
}
