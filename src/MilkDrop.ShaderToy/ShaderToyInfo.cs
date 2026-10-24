namespace MilkDrop.ShaderToy;

/// <summary>移植线的版本与来源信息（对着 <c>refcode/desktop-shadertoy-0.4.3</c>）。</summary>
public static class ShaderToyInfo
{
    /// <summary>上游版本（目录名）。</summary>
    public const string UpstreamVersion = "desktop-shadertoy 0.4.3";

    /// <summary>上游用的是 Vulkan(Daxa)+glslang+RmlUi；本移植改成 Silk.NET + ANGLE/GLES3 + ImGui。</summary>
    public const string Backend = "Silk.NET + ANGLE/OpenGL ES 3.0 + ImGui.NET";

    /// <summary>计划文档名，出问题时方便指路。</summary>
    public const string PlanDocument = "shaderToyPlan.md";
}
