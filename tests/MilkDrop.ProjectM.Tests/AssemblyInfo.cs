using Xunit;

// 这套测试共用一个进程级 GL 上下文（`GlContext` 对应 C++ 的全局 gl* 函数），
// 并行跑会互相踩状态，表现为随机的 "HLSL parsing failed" / 编译失败。
// 串行只多花一两秒，换掉这个偶发问题。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
