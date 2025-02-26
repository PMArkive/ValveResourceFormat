#version 460

// Common features
#define F_LAYERS 0
#define F_BLEND 0
#define F_PAINT_VERTEX_COLORS 0 // csgo_static_overlay_vfx
#define F_SECONDARY_UV 0
#define F_FORCE_UV2 0
#define F_DETAIL_TEXTURE 0
#define F_FOLIAGE_ANIMATION 0

#if (defined(csgo_foliage_vfx) || (defined(vr_complex_vfx) && (F_FOLIAGE_ANIMATION > 0)))
    #define foliage_vfx_common
#endif

#if defined(vr_standard_vfx) && (F_BLEND > 0)
    #define vr_standard_blend_vfx
#endif

#define D_TOOLS_COLOR_BUFFER 0
#define renderMode_ToolsVertexColor 0
