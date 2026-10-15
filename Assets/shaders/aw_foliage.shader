//=========================================================================================================================
// Alan Wake trees, undergrowth and their billboards: s&box's foliage shader lit the way the game lights its foliage,
// diffusely only (its FoliagePRT shaders have no specular term, gloss or reflections). Instances come from
// AwFoliageRenderer.
//=========================================================================================================================
HEADER
{
	Description = "Alan Wake Foliage";
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_ALPHA_TEST, 0..1, "Rendering" );
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
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	StaticCombo( S_ALPHA_TEST, F_ALPHA_TEST, Sys( ALL ) );

	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	#if S_ALPHA_TEST
		TextureAttribute( LightSim_Opacity_A, g_tColor );
		float g_flAlphaDistanceStart < Default( 500.0 ); Range( 0.0, 5000.0 ); UiGroup( "Alpha Test" ); >;
		float g_flAlphaDistanceEnd < Default( 2000.0 ); Range( 0.0, 10000.0 ); UiGroup( "Alpha Test" ); >;

		// Relaxes the alpha cutoff with distance so thin leaves don't vanish (same as the stock foliage shader).
		float DistanceAlphaScale( float dist )
		{
			float distFactor = saturate( ( dist - g_flAlphaDistanceStart ) / max( g_flAlphaDistanceEnd - g_flAlphaDistanceStart, 0.001 ) );
			float alphaRef = lerp( g_flAlphaTestReference, 0.1, distFactor );
			return g_flAlphaTestReference / max( alphaRef, 0.001 );
		}
	#endif

	//
	// s&box's standard shading with the specular half left out, as the game has none on its foliage.
	//
	float4 ShadeDiffuseOnly( PixelInput i, Material m )
	{
		m.WorldPositionWithOffset = i.vPositionWithOffsetWs;
		m.WorldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		m.ScreenPosition = i.vPositionSs;
		m.GeometricNormal = i.vNormalWs.xyz;

		Decals::Apply( m.WorldPosition, m );
		AdjustAlphaToCoverage( m );

		LightingTerms_t lightTerms = InitLightingTerms();
		CombinerInput combinerInput = ShadingModelStandard::MaterialToCombinerInput( m );
		ComputeDirectLighting( lightTerms, combinerInput );
		CalculateIndirectLighting( lightTerms, combinerInput );

		float3 diffuseOcclusion = CalculateDiffuseAmbientOcclusion( combinerInput, lightTerms );
		lightTerms.vIndirectDiffuse.rgb *= diffuseOcclusion;
		lightTerms.vDiffuse.rgb *= lerp( float3( 1.0, 1.0, 1.0 ), diffuseOcclusion, combinerInput.flAmbientOcclusionDirectDiffuse );

		float4 color = float4( ( lightTerms.vDiffuse.rgb + lightTerms.vIndirectDiffuse.rgb ) * combinerInput.vDiffuseColor.rgb, m.Opacity );

		if ( DepthNormals::WantsDepthNormals() )
			return DepthNormals::Output( m.Normal, m.Roughness, color.a );

		if ( g_bWireframeMode )
			return g_vWireframeColor;

		return DoAtmospherics( m.WorldPosition, m.ScreenPosition.xy, color );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::From( i );
		m.Metalness = 0;

		#if S_ALPHA_TEST
			m.Opacity *= DistanceAlphaScale( length( i.vPositionWithOffsetWs.xyz ) );
		#endif

		return ShadeDiffuseOnly( i, m );
	}
}
