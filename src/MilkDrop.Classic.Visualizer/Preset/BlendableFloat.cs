namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// <c>CBlendableFloat</c>（state.h:54-95, state.cpp:1901-1946）：预设切换时能做线性过渡的标量。
/// </summary>
/// <remarks>
/// 原版把 <c>operator=</c> / <c>+=</c> 等重载成"赋值即终止混合"，只有
/// <see cref="StartBlendFrom"/> 之后的 <see cref="Eval"/> 才会插值。照抄这一点很关键，
/// 否则 per-frame 代码每帧写一次 <c>zoom</c> 就会把过渡打断。
/// </remarks>
public sealed class BlendableFloat
{
    private float _val;
    private bool _blending;
    private float _blendStartTime;
    private float _blendDuration;
    private float _blendFrom;

    /// <summary>是否正处在混合过程中（对应 <c>m_bBlending</c>）。</summary>
    public bool IsBlending => _blending;

    /// <summary>未经混合的原始值（对应 <c>val</c>）。</summary>
    public float Value
    {
        get => _val;
        set
        {
            _val = value;
            _blending = false;
        }
    }

    /// <summary>对应 <c>float eval(float fTime)</c>；<paramref name="time"/> &lt; 0 时直接返回原始值。</summary>
    public float Eval(float time)
    {
        if (time < 0f)
        {
            return _val;
        }

        // 原版的判断是 (blending && 已过终点) || (时间倒流)，&& 优先级高于 ||，照抄
        if ((_blending && time > _blendStartTime + _blendDuration) || time < _blendStartTime)
        {
            _blending = false;
        }

        if (!_blending)
        {
            return _val;
        }

        var mix = (time - _blendStartTime) / _blendDuration;
        return (_blendFrom * (1.0f - mix)) + (_val * mix);
    }

    /// <summary>对应 <c>StartBlendFrom</c>：把起点设为 <paramref name="from"/> 在 <paramref name="animTime"/> 的值。</summary>
    public void StartBlendFrom(BlendableFloat from, float animTime, float duration)
    {
        if (duration < 0.001f)
        {
            return;
        }

        _blendFrom = from.Eval(animTime);
        _blending = true;
        _blendStartTime = animTime;
        _blendDuration = duration;
    }

    public void Multiply(float factor)
    {
        _val *= factor;
        _blending = false;
    }

    public void Divide(float divisor)
    {
        _val /= divisor;
        _blending = false;
    }

    public void Subtract(float amount)
    {
        _val -= amount;
        _blending = false;
    }

    public void Add(float amount)
    {
        _val += amount;
        _blending = false;
    }

    /// <summary>整块拷贝（包括混合中的中间状态），对应原版的 <c>memcpy(CState)</c>。</summary>
    internal void CopyFrom(BlendableFloat other)
    {
        _val = other._val;
        _blending = other._blending;
        _blendStartTime = other._blendStartTime;
        _blendDuration = other._blendDuration;
        _blendFrom = other._blendFrom;
    }

    public override string ToString() => _val.ToString("G9");
}
