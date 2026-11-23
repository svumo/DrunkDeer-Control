using WpfApp;
using Xunit;

public class AppVersionTests
{
    private static AppVersion V(string s) => AppVersion.TryParse(s) ?? throw new System.ArgumentException(s);

    [Theory]
    [InlineData("v2.7.1", "2.7.1")]
    [InlineData("2.7.1-beta.9", "2.7.1-beta.9")]
    [InlineData("2.7.1.9", "2.7.1")]
    [InlineData("2.7.2-beta.1+7b88c14abc", "2.7.2-beta.1")]
    public void Parses(string input, string expected) => Assert.Equal(expected, V(input).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("vnext")]
    [InlineData("2.7.1-")]
    public void Rejects(string input) => Assert.Null(AppVersion.TryParse(input));

    [Theory]
    [InlineData("2.7.2-beta.1", "2.7.2-beta.2")]
    [InlineData("2.7.2-beta.2", "2.7.2-beta.10")]   // numeric, not text, order
    [InlineData("2.7.2-beta.9", "2.7.2")]           // the stable release beats its betas
    [InlineData("2.7.1", "2.7.2-beta.1")]           // a beta of the next version beats the current stable
    [InlineData("2.7.1-beta.9", "2.7.1")]
    [InlineData("2.6.1", "2.7.0")]
    public void Orders(string older, string newer)
    {
        Assert.True(V(older) < V(newer));
        Assert.True(V(newer) > V(older));
    }

    [Fact]
    public void Equal_versions_compare_equal() => Assert.Equal(0, V("v2.7.2-beta.3").CompareTo(V("2.7.2-beta.3")));
}
