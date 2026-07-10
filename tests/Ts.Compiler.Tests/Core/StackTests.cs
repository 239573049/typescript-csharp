using System;
using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class StackTests
{
    [Fact]
    public void PushPopIsLifo()
    {
        var s = new Stack<int>();
        s.Push(1);
        s.Push(2);
        s.Push(3);
        Assert.Equal(3, s.Len);
        Assert.Equal(3, s.Pop());
        Assert.Equal(2, s.Peek());
        Assert.Equal(2, s.Len);
        Assert.Equal(2, s.Pop());
        Assert.Equal(1, s.Pop());
        Assert.Equal(0, s.Len);
    }

    [Fact]
    public void PopOnEmptyThrows()
    {
        var s = new Stack<string>();
        var ex = Assert.Throws<InvalidOperationException>(() => s.Pop());
        Assert.Equal("stack is empty", ex.Message); // Go: panic("stack is empty")
    }

    [Fact]
    public void PeekOnEmptyThrows()
    {
        var s = new Stack<string>();
        var ex = Assert.Throws<InvalidOperationException>(() => s.Peek());
        Assert.Equal("stack is empty", ex.Message);
    }
}
