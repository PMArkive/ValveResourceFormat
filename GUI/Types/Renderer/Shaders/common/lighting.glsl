#version 460
//? #include "features.glsl"
//? #include "utils.glsl"
//? #include "LightingConstants.glsl"
//? #include "lighting_common.glsl"
//? #include "texturing.glsl"
//? #include "pbr.glsl"

#define SCENE_PROBE_TYPE 0 // 1 = Individual, 2 = Atlas

#if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
    in vec3 vLightmapUVScaled;
    uniform sampler2DArray g_tIrradiance;
    uniform sampler2DArray g_tDirectionalIrradiance;
    #if (LightmapGameVersionNumber == 1)
        uniform sampler2DArray g_tDirectLightIndices;
        uniform sampler2DArray g_tDirectLightStrengths;
    #elif (LightmapGameVersionNumber == 2)
        uniform sampler2DArray g_tDirectLightShadows;
    #endif
#elif (D_BAKED_LIGHTING_FROM_PROBE == 1)

    uniform sampler3D g_tLPV_Irradiance;

    #if (LightmapGameVersionNumber == 1)
        uniform sampler3D g_tLPV_Indices;
        uniform sampler3D g_tLPV_Scalars;
    #elif (LightmapGameVersionNumber == 2)
        uniform sampler3D g_tLPV_Shadows;
    #endif

    struct LightProbe
    {
        vec3 Position;
        vec3 Min; vec3 Max;
        #if (SCENE_PROBE_TYPE == 2)
            vec3 AtlasOffset; vec3 AtlasScale;
        #endif
    };

    LightProbe GetProbe(vec3 fragPosition)
    {
        LightProbeVolumeData data = g_vLightProbeVolumeData[nEnvMap_LpvIndex.y];

        LightProbe probe;
        probe.Position = mat4x3(data.WorldToLocalNormalizer) * vec4(fragPosition, 1.0);
        probe.Min = data.Min.xyz;
        probe.Max = data.Max.xyz;

        #if (SCENE_PROBE_TYPE == 2)
            probe.AtlasOffset = data.AtlasOffset.xyz;
            probe.AtlasScale = data.AtlasScale.xyz;
        #endif

        return probe;
    }

    vec3 CalculateProbeShadowCoords(vec3 fragPosition)
    {
        LightProbe probe = GetProbe(fragPosition);

        #if (SCENE_PROBE_TYPE == 1)
            return probe.Position;
        #elif (SCENE_PROBE_TYPE == 2)
            return fma(saturate(probe.Position), probe.AtlasScale, probe.AtlasOffset);
        #endif
    }

    vec3 CalculateProbeIndirectCoords(vec3 fragPosition)
    {
        LightProbe probe = GetProbe(fragPosition);

        #if (SCENE_PROBE_TYPE == 1)
            probe.Position.z /= 6;
            // clamp(indirectCoords, g_vLightProbeVolumeLayer0TextureMin.xyz, g_vLightProbeVolumeLayer0TextureMax.xyz);
        #elif (SCENE_PROBE_TYPE == 2)
            probe.Position.z /= 6;
            probe.Position = clamp(probe.Position, probe.Min, probe.Max);
            probe.Position.z *= 6;
            probe.Position = fma(probe.Position, probe.AtlasScale, probe.AtlasOffset);
            probe.Position.z /= 6;
        #endif

        return probe.Position;
    }

uniform sampler2DShadow g_tShadowDepthBufferDepth;

float CalculateSunShadowMapVisibility(vec3 vPosition)
{
    vec4 projCoords = g_matWorldToShadow * vec4(vPosition, 1.0);
    projCoords.xyz /= projCoords.w;

    vec2 shadowCoords = clamp(projCoords.xy * 0.5 + 0.5, vec2(-1), vec2(2));

    // Note: Bias is added of clamp, so that the value is never zero (or negative)
    // as the comparison with <= 0 values produces shadow
    float currentDepth = saturate(projCoords.z) + g_flSunShadowBias;

    // To skip PCF
    // return 1 - textureLod(g_tShadowDepthBufferDepth, vec3(shadowCoords, currentDepth), 0).r;

    float shadow = 0.0;
    vec2 texelSize = 1.0 / textureSize(g_tShadowDepthBufferDepth, 0);
    for(int x = -1; x <= 1; ++x)
    {
        for(int y = -1; y <= 1; ++y)
        {
            float pcfDepth = textureLod(g_tShadowDepthBufferDepth, vec3(shadowCoords + vec2(x, y) * texelSize, currentDepth), 0).r;
            shadow += pcfDepth;
        }
    }
            return probe.Position;
        #endif
    }
#endif

    shadow /= 9.0;
    return 1 - shadow;
}

vec3 GetEnvLightDirection(uint nLightIndex)
{
    return normalize(mat3(g_matLightToWorld[nLightIndex]) * vec3(-1, 0, 0));
}

vec3 GetLightPositionWs(uint nLightIndex)
{
    return g_vLightPosition_Type[nLightIndex].xyz;
}

bool IsDirectionalLight(uint nLightIndex)
{
    return g_vLightPosition_Type[nLightIndex].a == 0.0;
}

vec3 GetLightDirection(vec3 vPositionWs, uint nLightIndex)
{
    if (IsDirectionalLight(nLightIndex))
    {
        return GetEnvLightDirection(nLightIndex);
    }

    vec3 lightPosition = GetLightPositionWs(nLightIndex);
    vec3 lightVector = normalize(lightPosition - vPositionWs);

    return lightVector;
}

vec3 GetLightColor(uint nLightIndex)
{
    vec3 vColor = g_vLightColor_Brightness[nLightIndex].rgb;
    float flBrightness = g_vLightColor_Brightness[nLightIndex].a;

    return vColor * flBrightness;
}

void CalculateDirectLighting(inout LightingTerms_t lighting, inout MaterialProperties_t mat)
{
    const float MIN_ALPHA = 0.0001;

    #if (LightmapGameVersionNumber == 1)
        #if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
            vec4 dls = texture(g_tDirectLightStrengths, vLightmapUVScaled);
            vec4 dli = texture(g_tDirectLightIndices, vLightmapUVScaled);
        #elif (D_BAKED_LIGHTING_FROM_PROBE == 1)
            vec3 vLightProbeShadowCoords = CalculateProbeShadowCoords(mat.PositionWS);
            vec4 dls = textureLod(g_tLPV_Scalars, vLightProbeShadowCoords, 0.0);
            vec4 dli = textureLod(g_tLPV_Indices, vLightProbeShadowCoords, 0.0);
        #else
            vec4 dls = vec4(1, 0, 0, 0);
            vec4 dli = vec4(0, 0, 0, 0);
        #endif

        vec4 vLightStrengths = pow2(dls);
        uvec4 vLightIndices = uvec4(dli * 255.0);

        //const uint testIndex = 35;
        //lighting.DiffuseDirect = vec3(any(equal(vLightIndices.xyw, uvec3(testIndex)))) + ((vLightIndices.z == testIndex) ? vec3(1) : vec3(0));
        //lighting.DiffuseDirect *= vLightStrengths.xyw;
        //return;

        for (int i = 0; i < 4; i++)
        {
            float visibility = vLightStrengths[i];
            if (visibility <= MIN_ALPHA)
            {
                continue;
            }

            uint uLightIndex = vLightIndices[i];
            bool bLightmapBakedDirectDiffuse = true;

            if (IsDirectionalLight(uLightIndex))
            {
                bLightmapBakedDirectDiffuse = false;
                visibility *= CalculateSunShadowMapVisibility(mat.PositionWS);
            }

            if (visibility > MIN_ALPHA)
            {
                vec3 lightVector = GetLightDirection(mat.PositionWS, uLightIndex);
                vec3 lightColor = GetLightColor(uLightIndex);

                vec3 previousDiffuse = lighting.DiffuseDirect;
                CalculateShading(lighting, lightVector, visibility * lightColor, mat);

                if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1 && bLightmapBakedDirectDiffuse)
                {
                    lighting.DiffuseDirect = previousDiffuse + (visibility * lightColor);
                }
            }
        }

    #elif (LightmapGameVersionNumber == 2)
        vec4 dlsh = vec4(1, 1, 1, 1);

        #if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
            dlsh = textureLod(g_tDirectLightShadows, vLightmapUVScaled, 0.0);
        #elif (D_BAKED_LIGHTING_FROM_PROBE == 1)
            vec3 vLightProbeShadowCoords = CalculateProbeShadowCoords(mat.PositionWS);
            dlsh = textureLod(g_tLPV_Shadows, vLightProbeShadowCoords, 0.0);
        #endif

        const uint uLightIndex = 0;

        float visibility = 1.0 - dlsh[uLightIndex];

        if (visibility > MIN_ALPHA && uLightIndex == 0)
        {
            visibility *= CalculateSunShadowMapVisibility(mat.PositionWS);
        }

        if (visibility > MIN_ALPHA)
        {
            vec3 lightColor = GetLightColor(uLightIndex);
            vec3 lightVector = GetLightDirection(mat.PositionWS, uLightIndex);
            CalculateShading(lighting, lightVector, visibility * lightColor, mat);
        }
    #else
        // Non lightmapped scene
        const uint uLightIndex = 0;
        vec3 lightColor = GetLightColor(uLightIndex);
        if (length(lightColor) > 0.0)
        {
            float visibility = CalculateSunShadowMapVisibility(mat.PositionWS);
            vec3 lightVector = GetEnvLightDirection(uLightIndex);
            CalculateShading(lighting, lightVector, visibility * lightColor, mat);
        }
    #endif
}


#if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)

#define UseLightmapDirectionality 1

uniform float g_flDirectionalLightmapStrength = 1.0;
uniform float g_flDirectionalLightmapMinZ = 0.05;
const vec4 g_vLightmapParams = vec4(0.0); // ???? directional non-intensity?? it's set to 0.0 in all places ive looked

const float colorSpaceMul = 254 / 255;

// I don't actually understand much of this, but it's Valve's code.
vec3 ComputeLightmapShading(vec3 irradianceColor, vec4 irradianceDirection, vec3 normalMap)
{

#if UseLightmapDirectionality == 1
    vec3 vTangentSpaceLightVector;

    vTangentSpaceLightVector.xy = UnpackFromColor(irradianceDirection.xy);

    float sinTheta = dot(vTangentSpaceLightVector.xy, vTangentSpaceLightVector.xy);

#if LightmapGameVersionNumber == 1
    // Error in HLA code, fixed in DeskJob
    float cosTheta = 1.0 - sqrt(sinTheta);
#else
    vTangentSpaceLightVector *= (colorSpaceMul / max(colorSpaceMul, length(vTangentSpaceLightVector.xy)));

    float cosTheta = sqrt(1.0 - sinTheta);
#endif
    vTangentSpaceLightVector.z = cosTheta;

    float flDirectionality = mix(irradianceDirection.z, 1.0, g_flDirectionalLightmapStrength);
    vec3 vNonDirectionalLightmap = irradianceColor * saturate(flDirectionality + g_vLightmapParams.x);

    float NoL = ClampToPositive(dot(vTangentSpaceLightVector, normalMap));

    float LightmapZ = max(vTangentSpaceLightVector.z, g_flDirectionalLightmapMinZ);

    irradianceColor = (NoL * (irradianceColor - vNonDirectionalLightmap) / LightmapZ) + vNonDirectionalLightmap;
#endif

    return irradianceColor;
}

#endif


void CalculateIndirectLighting(inout LightingTerms_t lighting, inout MaterialProperties_t mat)
{
    lighting.DiffuseIndirect = vec3(0.3);

    // Indirect Lighting
#if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
    vec3 irradiance = texture(g_tIrradiance, vLightmapUVScaled).rgb;
    vec4 vAHDData = texture(g_tDirectionalIrradiance, vLightmapUVScaled);

    lighting.DiffuseIndirect = ComputeLightmapShading(irradiance, vAHDData, mat.NormalMap);

    lighting.SpecularOcclusion = vAHDData.a;

#elif (D_BAKED_LIGHTING_FROM_PROBE == 1)
    vec3 vIndirectSampleCoords = CalculateProbeIndirectCoords(mat.PositionWS);

    // Take up to 3 samples along the normal direction
    vec3 vDepthSliceOffsets = mix(vec3(0, 1, 2) / 6.0, vec3(3, 4, 5) / 6.0, step(mat.AmbientNormal, vec3(0.0)));
    vec3 vAmbient[3];

    vec3 vNormalSquared = pow2(mat.AmbientNormal);

    lighting.DiffuseIndirect = vec3(0.0);

    for (int i = 0; i < 3; i++)
    {
        vAmbient[i] = textureLod(g_tLPV_Irradiance, vIndirectSampleCoords + vec3(0, 0, vDepthSliceOffsets[i]), 0.0).rgb;
        lighting.DiffuseIndirect += vAmbient[i] * vNormalSquared[i];
    }

#elif (D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1)
    lighting.DiffuseIndirect = vPerVertexLightingOut.rgb;
#endif

    // Environment Maps
#if defined(S_SPECULAR) && (S_SPECULAR == 1)
    vec3 ambientDiffuse;
    float normalizationTerm = GetEnvMapNormalization(GetIsoRoughness(mat.Roughness), mat.AmbientNormal, lighting.DiffuseIndirect);

    lighting.SpecularIndirect = GetEnvironment(mat) * normalizationTerm;
#endif
}


uniform float g_flAmbientOcclusionDirectDiffuse = 1.0;
uniform float g_flAmbientOcclusionDirectSpecular = 1.0;

// AO Proxies would be merged here
void ApplyAmbientOcclusion(inout LightingTerms_t o, MaterialProperties_t mat)
{
#if defined(DIFFUSE_AO_COLOR_BLEED)
    SetDiffuseColorBleed(mat);
#endif

    // In non-lightmap shaders, SpecularAO always does a min(1.0, specularAO) in the same place where lightmap
    // shaders does min(bakedAO, specularAO). That means that bakedAO exists and is a constant 1.0 in those shaders!
    mat.SpecularAO = min(o.SpecularOcclusion, mat.SpecularAO);

    vec3 DirectAODiffuse = mix(vec3(1.0), mat.DiffuseAO, g_flAmbientOcclusionDirectDiffuse);
    float DirectAOSpecular = mix(1.0, mat.SpecularAO, g_flAmbientOcclusionDirectSpecular);

    o.DiffuseDirect *= DirectAODiffuse;
    o.DiffuseIndirect *= mat.DiffuseAO;
    o.SpecularDirect *= DirectAOSpecular;
    o.SpecularIndirect *= mat.SpecularAO;
}
