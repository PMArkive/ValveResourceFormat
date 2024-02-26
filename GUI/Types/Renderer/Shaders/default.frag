#version 460

#define F_TRANSLUCENT 0

in vec4 vtxColor;
layout (location = 0) out vec4 outputColor;

#if (F_TRANSLUCENT == 1)
#include "common/translucent.glsl"
#endif

void main(void) {
    outputColor = vtxColor;

    #if (F_TRANSLUCENT == 1)
        outputColor = WeightColorTranslucency(outputColor);
    #endif
}
