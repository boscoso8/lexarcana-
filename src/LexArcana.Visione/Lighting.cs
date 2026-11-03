using System.Numerics;
using Raylib_cs;

namespace LexArcana.Visione;

/// <summary>
/// Lo shader della scena: luce ambiente, luna, fino a quattro luci puntiformi calde
/// (la lanterna del personaggio, il fuoco dell'altare) e nebbia notturna.
/// </summary>
internal sealed class Lighting : IDisposable
{
    public const int MaxLights = 4;

    private const string Vertex = """
        #version 330
        in vec3 vertexPosition;
        in vec3 vertexNormal;
        uniform mat4 mvp;
        uniform mat4 matModel;
        uniform mat4 matNormal;
        out vec3 fragPosition;
        out vec3 fragNormal;
        void main()
        {
            fragPosition = vec3(matModel * vec4(vertexPosition, 1.0));
            fragNormal = normalize(vec3(matNormal * vec4(vertexNormal, 0.0)));
            gl_Position = mvp * vec4(vertexPosition, 1.0);
        }
        """;

    private const string Fragment = """
        #version 330
        in vec3 fragPosition;
        in vec3 fragNormal;
        uniform vec4 colDiffuse;
        uniform vec3 viewPos;
        uniform vec3 ambient;
        uniform vec3 fogColor;
        uniform float fogDensity;
        uniform int lightCount;
        uniform vec3 lightPos[4];
        uniform vec3 lightColor[4];
        uniform float lightRange[4];
        uniform int pattern;      // 1 = lastre del pavimento
        uniform float emissive;   // 1 = brilla di luce propria (fuoco)
        out vec4 finalColor;

        void main()
        {
            vec3 n = normalize(fragNormal);
            vec3 base = colDiffuse.rgb;
            if (pattern == 1)
            {
                vec2 cell = fragPosition.xz / 2.0;
                vec2 f = fract(cell);
                float tone = mod(floor(cell.x) + floor(cell.y), 2.0);
                base *= mix(0.84, 1.0, tone);
                float grout = step(0.965, max(f.x, f.y)) + step(min(f.x, f.y), 0.035);
                base *= mix(1.0, 0.55, clamp(grout, 0.0, 1.0));
            }

            vec3 light = ambient;
            light += vec3(0.20, 0.22, 0.30) * max(dot(n, normalize(vec3(0.35, 1.0, 0.25))), 0.0);
            for (int i = 0; i < 4; i++)
            {
                if (i >= lightCount) break;
                vec3 d = lightPos[i] - fragPosition;
                float dist = length(d);
                float att = clamp(1.0 - dist / lightRange[i], 0.0, 1.0);
                light += lightColor[i] * (0.25 + 0.75 * max(dot(n, d / max(dist, 0.001)), 0.0)) * att * att;
            }

            // compressione morbida dei toni: le zone molto illuminate non bruciano
            vec3 lit = vec3(1.0) - exp(-base * light * 1.4);
            vec3 color = mix(lit, base, emissive);
            float dist = length(viewPos - fragPosition);
            float fog = 1.0 - exp(-pow(dist * fogDensity, 2.0));
            finalColor = vec4(mix(color, fogColor, clamp(fog, 0.0, 1.0)), colDiffuse.a);
        }
        """;

    private readonly int _viewPos, _ambient, _fogColor, _fogDensity, _lightCount, _lightPos, _lightColor, _lightRange, _pattern, _emissive;

    /// <summary>Lo stesso shader in GLSL ES 1.00, per il browser (raylib per il web usa OpenGL ES 2 su WebGL).</summary>
    private static string ForEs2(string source, bool fragment)
    {
        var s = source.Replace("#version 330", fragment ? "#version 100\nprecision mediump float;" : "#version 100", StringComparison.Ordinal);
        if (fragment)
            s = s.Replace("in vec3 ", "varying vec3 ", StringComparison.Ordinal)
                 .Replace("out vec4 finalColor;", "", StringComparison.Ordinal)
                 .Replace("finalColor = ", "gl_FragColor = ", StringComparison.Ordinal);
        else
            s = s.Replace("in vec3 ", "attribute vec3 ", StringComparison.Ordinal)
                 .Replace("out vec3 ", "varying vec3 ", StringComparison.Ordinal);
        return s;
    }

    public Lighting()
    {
        bool es2 = Rlgl.GetVersion() == GlVersion.OpenGlEs20;
        Shader = es2 ? Raylib.LoadShaderFromMemory(ForEs2(Vertex, false), ForEs2(Fragment, true)) : Raylib.LoadShaderFromMemory(Vertex, Fragment);
        _viewPos = Raylib.GetShaderLocation(Shader, "viewPos");
        _ambient = Raylib.GetShaderLocation(Shader, "ambient");
        _fogColor = Raylib.GetShaderLocation(Shader, "fogColor");
        _fogDensity = Raylib.GetShaderLocation(Shader, "fogDensity");
        _lightCount = Raylib.GetShaderLocation(Shader, "lightCount");
        _lightPos = Raylib.GetShaderLocation(Shader, "lightPos");
        _lightColor = Raylib.GetShaderLocation(Shader, "lightColor");
        _lightRange = Raylib.GetShaderLocation(Shader, "lightRange");
        _pattern = Raylib.GetShaderLocation(Shader, "pattern");
        _emissive = Raylib.GetShaderLocation(Shader, "emissive");

        Raylib.SetShaderValue(Shader, _ambient, new Vector3(0.26f, 0.22f, 0.20f), ShaderUniformDataType.Vec3);
        var fog = Theme.Fog;
        Raylib.SetShaderValue(Shader, _fogColor, new Vector3(fog.R / 255f, fog.G / 255f, fog.B / 255f), ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(Shader, _fogDensity, 0.042f, ShaderUniformDataType.Float);
        Pattern(false);
        Emissive(false);
    }

    public Shader Shader { get; }

    /// <summary>La distanza oltre la quale la nebbia copre tutto: inutile disegnare più in là.</summary>
    public const float VisibleDistance = 48f;

    public void Frame(Vector3 eye, ReadOnlySpan<(Vector3 Position, Vector3 Color, float Range)> lights)
    {
        Raylib.SetShaderValue(Shader, _viewPos, eye, ShaderUniformDataType.Vec3);
        int count = Math.Min(lights.Length, MaxLights);
        var positions = new float[MaxLights * 3];
        var colors = new float[MaxLights * 3];
        var ranges = new float[MaxLights];
        for (int i = 0; i < count; i++)
        {
            var (p, c, r) = lights[i];
            (positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]) = (p.X, p.Y, p.Z);
            (colors[i * 3], colors[i * 3 + 1], colors[i * 3 + 2]) = (c.X, c.Y, c.Z);
            ranges[i] = r;
        }
        Raylib.SetShaderValue(Shader, _lightCount, count, ShaderUniformDataType.Int);
        Raylib.SetShaderValueV(Shader, _lightPos, positions, ShaderUniformDataType.Vec3, MaxLights);
        Raylib.SetShaderValueV(Shader, _lightColor, colors, ShaderUniformDataType.Vec3, MaxLights);
        Raylib.SetShaderValueV(Shader, _lightRange, ranges, ShaderUniformDataType.Float, MaxLights);
    }

    public void Pattern(bool floor) => Raylib.SetShaderValue(Shader, _pattern, floor ? 1 : 0, ShaderUniformDataType.Int);
    public void Emissive(bool on) => Raylib.SetShaderValue(Shader, _emissive, on ? 1f : 0f, ShaderUniformDataType.Float);

    public void Dispose() => Raylib.UnloadShader(Shader);
}
