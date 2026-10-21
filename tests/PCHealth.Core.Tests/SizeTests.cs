using System.Reflection;

namespace PCHealth.Core.Tests;

public class SizeTests
{
    [Fact]
    public void Measured_size_rejects_negative_bytes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeasuredSize(-1));
    }

    [Fact]
    public void Measured_sizes_add_up()
    {
        Assert.Equal(new MeasuredSize(30), new MeasuredSize(10) + new MeasuredSize(20));
        Assert.Equal(0, MeasuredSize.Zero.Bytes);
    }

    [Fact]
    public void Measured_size_addition_fails_on_overflow()
    {
        Assert.Throws<OverflowException>(() => new MeasuredSize(long.MaxValue) + new MeasuredSize(1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Estimated_size_requires_a_basis(string basis)
    {
        Assert.Throws<ArgumentException>(() => new EstimatedSize(100, basis));
    }

    [Fact]
    public void Estimated_size_rejects_negative_bytes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EstimatedSize(-1, "guess"));
    }

    [Fact]
    public void Measured_and_estimated_sizes_are_never_equal_or_convertible()
    {
        object measured = new MeasuredSize(100);
        object estimated = new EstimatedSize(100, "sum of candidate files");

        Assert.NotEqual(measured, estimated);
        Assert.DoesNotContain(typeof(MeasuredSize).GetMethods(BindingFlags.Public | BindingFlags.Static), IsConversion);
        Assert.DoesNotContain(typeof(EstimatedSize).GetMethods(BindingFlags.Public | BindingFlags.Static), IsConversion);
    }

    [Fact]
    public void Strings_say_whether_a_size_is_measured_or_estimated()
    {
        Assert.Contains("measured", new MeasuredSize(5).ToString());
        Assert.Contains("estimated: cache total", new EstimatedSize(5, "cache total").ToString());
    }

    private static bool IsConversion(MethodInfo method) => method.Name is "op_Implicit" or "op_Explicit";
}
