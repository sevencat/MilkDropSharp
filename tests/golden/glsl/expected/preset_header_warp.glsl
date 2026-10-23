#version 330
vec2 matrix_row0(mat2 m, int i) { return vec2( m[0][i], m[1][i] ); }
vec2 matrix_row0(mat2 m, float i_float) { int i=int(i_float); return vec2( m[0][i], m[1][i] ); }
vec2 matrix_row0(mat2x3 m, int i) { return vec2( m[0][i], m[1][i]); }
vec2 matrix_row0(mat2x3 m, float i_float) { int i=int(i_float); return vec2( m[0][i], m[1][i]); }
vec2 matrix_row0(mat2x4 m, int i) { return vec2( m[0][i], m[1][i]); }
vec2 matrix_row0(mat2x4 m, float i_float) { int i=int(i_float); return vec2( m[0][i], m[1][i]); }
vec3 matrix_row0(mat3 m, int i) { return vec3( m[0][i], m[1][i], m[2][i] ); }
vec3 matrix_row0(mat3 m, float i_float) { int i=int(i_float); return vec3( m[0][i], m[1][i], m[2][i] ); }
vec3 matrix_row0(mat3x2 m, int i) { return vec3( m[0][i], m[1][i], m[2][i] ); }
vec3 matrix_row0(mat3x2 m, float i_float) { int i=int(i_float); return vec3( m[0][i], m[1][i], m[2][i] ); }
vec3 matrix_row0(mat3x4 m, int i) { return vec3( m[0][i], m[1][i], m[2][i] ); }
vec3 matrix_row0(mat3x4 m, float i_float) { int i=int(i_float); return vec3( m[0][i], m[1][i], m[2][i] ); }
vec4 matrix_row0(mat4 m, int i) { return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }
vec4 matrix_row0(mat4 m, float i_float) { int i=int(i_float); return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }
vec4 matrix_row0(mat4x3 m, int i) { return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }
vec4 matrix_row0(mat4x3 m, float i_float) { int i=int(i_float); return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }
vec4 matrix_row0(mat4x2 m, int i) { return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }
vec4 matrix_row0(mat4x2 m, float i_float) { int i=int(i_float); return vec4( m[0][i], m[1][i], m[2][i], m[3][i] ); }
vec2  m_scalar_swizzle20(float x) { return  vec2(x, x); }
ivec2 m_scalar_swizzle20(int   x) { return ivec2(x, x); }
vec3  m_scalar_swizzle30(float x) { return  vec3(x, x, x); }
ivec3 m_scalar_swizzle30(int   x) { return ivec3(x, x, x); }
vec4  m_scalar_swizzle40(float x) { return  vec4(x, x, x, x); }
ivec4 m_scalar_swizzle40(int   x) { return ivec4(x, x, x, x); }
uvec2 m_scalar_swizzle20(uint  x) { return uvec2(x, x); }
uvec3 m_scalar_swizzle30(uint  x) { return uvec3(x, x, x); }
uvec4 m_scalar_swizzle40(uint  x) { return uvec4(x, x, x, x); }
vec2 bvecTernary0(bvec2 cond, vec2 trueExpr, vec2 falseExpr) { vec2 ret; ret.x = cond.x ? trueExpr.x : falseExpr.x; ret.y = cond.y ? trueExpr.y : falseExpr.y; return ret; }
vec3 bvecTernary0(bvec3 cond, vec3 trueExpr, vec3 falseExpr) { vec3 ret; ret.x = cond.x ? trueExpr.x : falseExpr.x; ret.y = cond.y ? trueExpr.y : falseExpr.y; ret.z = cond.z ? trueExpr.z : falseExpr.z; return ret; }
vec4 bvecTernary0(bvec4 cond, vec4 trueExpr, vec4 falseExpr) { vec4 ret; ret.x = cond.x ? trueExpr.x : falseExpr.x; ret.y = cond.y ? trueExpr.y : falseExpr.y; ret.z = cond.z ? trueExpr.z : falseExpr.z; ret.w = cond.w ? trueExpr.w : falseExpr.w; return ret; }
mat3 mat3_from() { return mat3(0,0,0,0,0,0,0,0,0); }
in vec2 frag_TEXCOORD0;
out vec4 rast_FragData[1];
uniform vec4 rand_frame;
uniform vec4 rand_preset;
uniform vec4 _c0;
uniform vec4 _c1, _c2, _c3, _c4;
uniform vec4 _c5;
uniform vec4 _c6;
uniform vec4 _c7;
uniform vec4 _c8;
uniform vec4 _c9;
uniform vec4 _c10;
uniform vec4 _c11;
uniform vec4 _c12;
uniform vec4 _c13;
uniform vec4 _qa;
uniform vec4 _qb;
uniform vec4 _qc;
uniform vec4 _qd;
uniform vec4 _qe;
uniform vec4 _qf;
uniform vec4 _qg;
uniform vec4 _qh;
uniform mat3x4 rot_s1;
uniform mat3x4 rot_s2;
uniform mat3x4 rot_s3;
uniform mat3x4 rot_s4;
uniform mat3x4 rot_d1;
uniform mat3x4 rot_d2;
uniform mat3x4 rot_d3;
uniform mat3x4 rot_d4;
uniform mat3x4 rot_f1;
uniform mat3x4 rot_f2;
uniform mat3x4 rot_f3;
uniform mat3x4 rot_f4;
uniform mat3x4 rot_vf1;
uniform mat3x4 rot_vf2;
uniform mat3x4 rot_vf3;
uniform mat3x4 rot_vf4;
uniform mat3x4 rot_uf1;
uniform mat3x4 rot_uf2;
uniform mat3x4 rot_uf3;
uniform mat3x4 rot_uf4;
uniform mat3x4 rot_rand1;
uniform mat3x4 rot_rand2;
uniform mat3x4 rot_rand3;
uniform mat3x4 rot_rand4;
sampler2D sampler_main;
sampler2D sampler_blur1;
sampler2D sampler_blur2;
sampler2D sampler_blur3;
mat3 g_rot;
vec4 PS(vec2 uv) {
    vec3 col = vec3( (texture(sampler_main, uv)).xyz );
    (col += ((((((texture(sampler_blur1, uv)).xyz * vec3 ((_c5).x)) + vec3 ((_c5).y)) * vec3 (float(0.25))) + ((((texture(sampler_blur2, uv)).xyz * vec3 ((_c5).z)) + vec3 ((_c5).w)) * vec3 (float(0.125)))) + ((((texture(sampler_blur3, uv)).xyz * vec3 ((_c6).x)) + vec3 ((_c6).y)) * vec3 (float(0.0625)))));
    (col = ((g_rot)*(col)));
    float l = float( dot(col, vec3(float(0.32), float(0.49), float(0.29))) );
    (col = mix(col, vec3(l, l, l), vec3 (float(0.25))));
    (col *= vec3 ((float(1) + ((_c3).x * float(0.5)))));
    return vec4(col, float(1));
}
void main() {
    vec2 uv;
    uv = frag_TEXCOORD0;
    vec4 result = PS(uv);
    rast_FragData[0] = result;
}
