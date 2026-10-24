using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Tests;

/// <summary>
/// 转译回归用的 Shadertoy 代码样本。
/// </summary>
/// <remarks>
/// 仓库里没有真实的 shadertoy 导出（计划 P2 说的 "tests/golden 里 20 段" 实际是 HLSL→GLSL 的语料，
/// 与 Shadertoy 无关），下面按网页上常见的写法手写了一批，覆盖 raymarch / hash 噪声 / buffer 自反馈 /
/// cubemap / keyboard / 旧 WebGL1 语法 / 用户自定义 main 等分支。
/// 拿到真实导出后应替换/追加，保持"每段都能编译通过"。
/// </remarks>
internal static class ShaderCorpus
{
    /// <summary>(名字, pass 类型, 代码)。</summary>
    public static IReadOnlyList<(string Name, ShaderPassType Type, string Code)> Cases { get; } =
    [
        ("gradient", ShaderPassType.Image, """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = fragCoord / iResolution.xy;
                vec3 col = 0.5 + 0.5 * cos(iTime + uv.xyx + vec3(0.0, 2.0, 4.0));
                fragColor = vec4(col, 1.0);
            }
            """),

        ("raymarch", ShaderPassType.Image, """
            #define MAX_STEPS 96
            #define MAX_DIST 20.0
            #define SURF_DIST 0.001

            float sdSphere(vec3 p, float r) { return length(p) - r; }

            float sdBox(vec3 p, vec3 b)
            {
                vec3 q = abs(p) - b;
                return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
            }

            float map(vec3 p)
            {
                return min(sdSphere(p - vec3(0.0, 0.0, 3.0), 1.0), sdBox(p, vec3(3.0, 0.1, 3.0)) + 1.0);
            }

            vec3 getNormal(vec3 p)
            {
                vec2 e = vec2(0.001, 0.0);
                return normalize(vec3(
                    map(p + e.xyy) - map(p - e.xyy),
                    map(p + e.yxy) - map(p - e.yxy),
                    map(p + e.yyx) - map(p - e.yyx)));
            }

            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
                vec3 ro = vec3(0.0, 1.0, 0.0);
                vec3 rd = normalize(vec3(uv, 1.0));

                float t = 0.0;
                float d = 0.0;
                for (int i = 0; i < MAX_STEPS; i++)
                {
                    vec3 p = ro + rd * t;
                    d = map(p);
                    if (d < SURF_DIST || t > MAX_DIST)
                    {
                        break;
                    }
                    t += d;
                }

                vec3 col = vec3(0.0);
                if (d < SURF_DIST)
                {
                    vec3 p = ro + rd * t;
                    vec3 n = getNormal(p);
                    col = n * 0.5 + 0.5;
                }
                fragColor = vec4(col, 1.0);
            }
            """),

        ("hash-noise", ShaderPassType.Buffer, """
            float hash21(vec2 p)
            {
                p = fract(p * vec2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return fract(p.x * p.y);
            }

            float valueNoise(vec2 p)
            {
                vec2 i = floor(p);
                vec2 f = fract(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + vec2(1.0, 0.0));
                float c = hash21(i + vec2(0.0, 1.0));
                float d = hash21(i + vec2(1.0, 1.0));
                return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
            }

            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = fragCoord / iResolution.xy;
                float n = 0.0;
                float amp = 0.5;
                for (int octave = 0; octave < 5; octave++)
                {
                    n += valueNoise(uv * pow(2.0, float(octave))) * amp;
                    amp *= 0.5;
                }
                fragColor = vec4(vec3(n), 1.0);
            }
            """),

        ("buffer-feedback", ShaderPassType.Buffer, """
            // Buffer A：读上一帧的自己（iChannel0 指向本 pass 的 ping-pong 另一侧）
            float hash21(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = fragCoord / iResolution.xy;
                vec2 texel = 1.0 / iResolution.xy;

                vec4 prev = texture(iChannel0, uv);
                float dx = texture(iChannel0, uv + vec2(texel.x, 0.0)).x
                         - texture(iChannel0, uv - vec2(texel.x, 0.0)).x;
                float dy = texture(iChannel0, uv + vec2(0.0, texel.y)).x
                         - texture(iChannel0, uv - vec2(0.0, texel.y)).x;

                vec4 next = prev * 0.995 + vec4(dx, dy, 0.0, 1.0) * 0.005;
                if (iFrame < 2)
                {
                    next = vec4(hash21(uv * 1000.0), 0.5, 0.5, 1.0);
                }
                fragColor = clamp(next, 0.0, 1.0);
            }
            """),

        ("keyboard", ShaderPassType.Image, """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = fragCoord / iResolution.xy;
                // 键盘通道是 256x3：x = keyCode，第 0 行是按下的键
                float keyA = texture(iChannel0, vec2((65.0 + 0.5) / 256.0, 0.5 / 3.0)).x;
                float keyS = texture(iChannel0, vec2((83.0 + 0.5) / 256.0, 0.5 / 3.0)).x;
                fragColor = vec4(vec3(keyA, keyS, 0.0) + vec3(0.05), 1.0);
            }
            """),

        ("cubemap", ShaderPassType.Cubemap, """
            void mainCubemap(out vec4 fragColor, in vec2 fragCoord, in vec3 rayOri, in vec3 rayDir)
            {
                float t = 0.5 * (-rayOri.y + sqrt(rayOri.y * rayOri.y - 2.0 * rayDir.y));
                vec3 p = rayOri + rayDir * t;
                vec2 uv = p.xz * 0.25;
                float checker = mod(floor(uv.x * 8.0) + floor(uv.y * 8.0), 2.0);
                fragColor = vec4(vec3(checker), 1.0);
            }
            """),

        ("legacy-webgl1-main", ShaderPassType.Image, """
            #version 100
            #extension GL_OES_standard_derivatives : enable

            void main()
            {
                vec2 uv = gl_FragCoord.xy / vec2(640.0, 360.0);
                gl_FragColor = vec4(uv, abs(dFdx(uv.x)) * 100.0, 1.0);
            }
            """),

        ("legacy-texture2d", ShaderPassType.Image, """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = fragCoord / iResolution.xy;
                vec2 distorted = uv + 0.02 * sin(uv.yx * 20.0 + iGlobalTime);
                fragColor = texture2D(iChannel0, distorted) * texture2D(iChannel1, uv);
            }
            """),

        ("structs-and-arrays", ShaderPassType.Image, """
            struct Ray
            {
                vec3 origin;
                vec3 dir;
            };

            const mat2 rot = mat2(0.8, 0.6, -0.6, 0.8);
            float weights[3];

            vec3 palette(float t)
            {
                vec3 a = vec3(0.5);
                vec3 b = vec3(0.5);
                vec3 c = vec3(1.0, 1.0, 1.0);
                vec3 d = vec3(0.263, 0.416, 0.557);
                return a + b * cos(6.28318 * (c * t + d));
            }

            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                Ray ray;
                ray.origin = vec3(0.0);
                ray.dir = normalize(vec3((fragCoord - 0.5 * iResolution.xy) / iResolution.y, 1.0));

                weights[0] = 0.5;
                weights[1] = 0.3;
                weights[2] = 0.2;

                vec3 col = vec3(0.0);
                for (int i = 0; i < 3; i++)
                {
                    col += palette(float(i) + iTime * 0.1) * weights[i] * ray.dir.z;
                }

                col = vec3(rot * col.xy, col.z);
                fragColor = vec4(col, 1.0);
            }
            """),

        ("builtin-uniforms", ShaderPassType.Image, """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = fragCoord / iResolution.xy;
                vec2 mouse = iMouse.xy / iResolution.xy;
                float pulse = 0.5 + 0.5 * sin(iTime * iFrameRate * 0.01);

                vec3 col = vec3(uv, mouse.x) * pulse;
                if (iMouse.z > 0.0)
                {
                    col = mix(col, vec3(1.0, 0.0, 0.0), 0.2);
                }

                col += 0.0001 * float(iFrame);
                col *= iTimeDelta * 60.0;
                col += iDate.w * 0.0;
                col += iSampleRate * 0.0;
                col += iChannelTime[0] * 0.0;
                fragColor = vec4(col, 1.0);
            }
            """),

        ("texel-fetch", ShaderPassType.Buffer, """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                ivec2 size = ivec2(iChannelResolution[0].xy);
                ivec2 p = ivec2(mod(fragCoord, vec2(size)));
                vec4 center = texelFetch(iChannel0, p, 0);
                vec4 up = texelFetch(iChannel0, ivec2(p.x, max(p.y - 1, 0)), 0);
                fragColor = vec4(center.rgb - up.rgb * 0.5, 1.0);
            }
            """),

        ("hw-performance", ShaderPassType.Image, """
            #if HW_PERFORMANCE == 1
                #define ITERATIONS 64
            #else
                #define ITERATIONS 16
            #endif

            float accumulate(vec2 uv)
            {
                float sum = 0.0;
                for (int i = 0; i < ITERATIONS; i++)
                {
                    sum += 1.0 / (1.0 + float(i) + length(uv));
                }
                return sum / float(ITERATIONS);
            }

            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = fragCoord / iResolution.xy * 2.0 - 1.0;
                float v = accumulate(uv + 0.01 * iTime);
                fragColor = vec4(vec3(v), 1.0);
            }
            """),

        ("texture-lod-and-mip", ShaderPassType.Image, """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = fragCoord / iResolution.xy;
                vec4 sharp = textureLod(iChannel0, uv, 0.0);
                vec4 blurry = textureLod(iChannel0, uv, 4.0);
                vec4 biased = texture(iChannel0, uv, 1.0);
                fragColor = mix(sharp, blurry, 0.5) * 0.5 + biased * 0.5;
            }
            """),

        ("while-and-out-param", ShaderPassType.Buffer, """
            float march(vec3 ro, vec3 rd, out int steps)
            {
                float t = 0.0;
                steps = 0;
                while (t < 10.0 && steps < 48)
                {
                    float d = length(ro + rd * t) - 1.0;
                    if (d < 0.001)
                    {
                        break;
                    }
                    t += d;
                    steps++;
                }
                return t;
            }

            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
                int steps;
                float t = march(vec3(0.0, 0.0, -3.0), normalize(vec3(uv, 1.0)), steps);
                fragColor = vec4(vec3(float(steps) / 48.0) * step(t, 9.9), 1.0);
            }
            """),
    ];
}
