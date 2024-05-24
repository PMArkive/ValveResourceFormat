#version 460

layout (location = 5) in uint sceneObjectId; // Index into the instance buffer

struct PerInstancePackedShaderData_t
{
    uint m_Data[8];
};

layout(std430, binding = 4) readonly buffer g_instanceBuffer
{
    PerInstancePackedShaderData_t instance[];
};


layout(std430, binding = 5) readonly buffer g_transformBuffer
{
    mat4 transform[];
};

PerInstancePackedShaderData_t GetInstanceData()
{
    return instance[sceneObjectId];
}

int GetTransformBufferOffset(PerInstancePackedShaderData_t packedData)
{
    return int(packedData.m_Data[1]);
}

int CalculateLightProbeIndex(PerInstancePackedShaderData_t packedData)
{
    return int(packedData.m_Data[2]);
}

mat4 CalculateObjectToWorldMatrix(int nTransformBufferOffset)
{
    return transform[nTransformBufferOffset];
}

mat4 CalculateObjectToWorldMatrix()
{
    int nTransformBufferOffset = GetTransformBufferOffset(GetInstanceData());
    return CalculateObjectToWorldMatrix(nTransformBufferOffset);
}

vec4 UnpackTintColorRGBA32(uint nColor)
{
    vec4 vResult;
    vResult.a = (nColor >> 24);
    vResult.b = (nColor >> 16) & 0xff;
    vResult.g = (nColor >> 8) & 0xff;
    vResult.r = (nColor >> 0) & 0xff;
    vResult.rgba *= ( 1 / 255.0 );
    //vResult.rgb = SrgbGammaToLinear(vResult.rgb);
    return vResult;
}

struct InstanceData_t
{
    vec4 vTint;
    int nTransformBufferOffset;
    int nLightProbeIndex;
};

InstanceData_t DecodePackedInstanceData(PerInstancePackedShaderData_t packedData)
{
    InstanceData_t extraShaderData;
    extraShaderData.vTint = UnpackTintColorRGBA32(packedData.m_Data[0]);
    extraShaderData.nTransformBufferOffset = GetTransformBufferOffset(packedData);
    extraShaderData.nLightProbeIndex = CalculateLightProbeIndex(packedData);
    return extraShaderData;
}
