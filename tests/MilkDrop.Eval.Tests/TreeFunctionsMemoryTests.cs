using Xunit;

namespace ProjectMEvalSharp.Tests;

public class TreeFunctionsMemoryTests : TreeFunctionsTestBase
{
    [Fact]
    public void MemoryAccess()
    {
        var ctx = new CompileContext();
        var memNode = ctx.Call("megabuf", ctx.Constant(42.0));
        var setNode = ctx.Call("_set", memNode, ctx.Constant(50.0));

        Assert.Equal(50.0, ctx.Evaluate(setNode));
        Assert.Equal(50.0, ctx.LocalMemory[42]);
    }

    [Fact]
    public void FreeMemoryBuffer()
    {
        var ctx = new CompileContext();
        var freembufNode = ctx.Call("freembuf", ctx.Constant(10.0));
        ctx.LocalMemory[65536 * 10 + 10] = 123.0;

        Assert.Equal(10.0, ctx.Evaluate(freembufNode));
        Assert.Equal(123.0, ctx.LocalMemory[65536 * 10 + 10]);
    }

    [Fact]
    public void MemoryCopyWithOverlap()
    {
        var ctx = new CompileContext();
        var memcpyNode = ctx.Call("memcpy", ctx.Constant(65536.0), ctx.Constant(65636.0), ctx.Constant(200.0));

        for (int index = 0; index < 300; ++index)
        {
            ctx.LocalMemory[65536 + index] = index + 1;
        }

        Assert.Equal(65536.0, ctx.Evaluate(memcpyNode));

        for (int index = 0; index < 300; ++index)
        {
            if (index < 200)
            {
                Assert.Equal(index + 101, (int)ctx.LocalMemory[65536 + index]);
            }
            else
            {
                Assert.Equal(index + 1, (int)ctx.LocalMemory[65536 + index]);
            }
        }
    }
}
