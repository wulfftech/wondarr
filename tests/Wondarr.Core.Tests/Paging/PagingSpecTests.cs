using Wondarr.Core.Paging;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Paging;

public class PagingSpecTests
{
    [Theory]
    [InlineData(int.MaxValue, PagingSpec.MaxPageSize)]
    [InlineData(int.MaxValue, 200)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MaxValue - 1, 999)]
    public void A_page_number_past_the_end_never_overflows_the_skip(int page, int pageSize)
    {
        var spec = new PagingSpec(page, pageSize, null, false);

        spec.Skip.Should().BeGreaterThanOrEqualTo(0);
        spec.Page.Should().BeLessThanOrEqualTo(PagingSpec.MaxPage);
    }

    [Fact]
    public void A_page_below_one_becomes_the_first_page()
    {
        new PagingSpec(-3, 50, null, false).Skip.Should().Be(0);
    }
}
