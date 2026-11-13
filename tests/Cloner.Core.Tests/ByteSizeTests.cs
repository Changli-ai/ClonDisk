using System.Globalization;
using Cloner.Core;

namespace Cloner.Core.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(500107862016L, "465.76 GB")]
    [InlineData(1099511627776L, "1 TB")]
    [InlineData(long.MaxValue, "8 EB")]
    public void FormatUsesBinaryMultiples(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void FormatUsesDecimalSeparatorOfTheCulture()
    {
        Assert.Equal("1,5 KB", ByteSize.Format(1536, CultureInfo.GetCultureInfo("es-ES")));
    }

    [Fact]
    public void FormatRejectsNegativeSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteSize.Format(-1, CultureInfo.InvariantCulture));
    }
}
