using Microsoft.AspNetCore.Http;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Ported with the code.
/// </summary>
public class RequestNavigationTests
{
    [Theory]
    // A click or a form submit on one of our pages, and what follows its redirect.
    [InlineData("navigate", "same-origin", "document", "?1", "user")]
    [InlineData("navigate", "none", "document", "?1", "direct")]
    [InlineData("navigate", "same-origin", "document", null, "scripted")]
    [InlineData("navigate", "cross-site", "document", "?1", "cross-site")]
    [InlineData("navigate", "same-origin", "iframe", null, "frame")]
    [InlineData("cors", "same-origin", "empty", null, "fetch")]
    [InlineData("same-origin", "same-origin", "empty", null, "fetch")]
    // Header values are compared without regard to case.
    [InlineData("NAVIGATE", "None", "Document", null, "direct")]
    // curl, a test client and older browsers send none of them.
    [InlineData(null, null, null, null, null)]
    // Sec-Fetch-User alone says nothing about how the request was started.
    [InlineData(null, null, null, "?1", null)]
    public void The_fetch_headers_reduce_to_one_word(string? mode, string? site, string? dest, string? user, string? expected)
    {
        var headers = new HeaderDictionary();
        Add(headers, "Sec-Fetch-Mode", mode);
        Add(headers, "Sec-Fetch-Site", site);
        Add(headers, "Sec-Fetch-Dest", dest);
        Add(headers, "Sec-Fetch-User", user);

        Assert.Equal(expected, RequestNavigation.Describe(headers));
    }

    [Fact]
    public void The_six_words_are_the_published_constants()
    {
        // The values are queried in a shared log store, so they are as fixed as the names.
        Assert.Equal(
            ["fetch", "frame", "direct", "cross-site", "user", "scripted"],
            new[]
            {
                RequestNavigation.Fetch, RequestNavigation.Frame, RequestNavigation.Direct,
                RequestNavigation.CrossSite, RequestNavigation.User, RequestNavigation.Scripted
            });
    }

    [Fact]
    public void Null_headers_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => RequestNavigation.Describe(null!));
    }

    private static void Add(HeaderDictionary headers, string name, string? value)
    {
        if (value is not null)
        {
            headers[name] = value;
        }
    }
}
