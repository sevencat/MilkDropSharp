#define M_PI 3.14159265359
#define SQUARE(x) ((x)*(x))
#pragma pack_matrix(row_major)
#if 0
float disabled = 1.0;
#else
float enabled = SQUARE(M_PI);
#endif
#line 200 "injected.hlsl"
float after = enabled;
