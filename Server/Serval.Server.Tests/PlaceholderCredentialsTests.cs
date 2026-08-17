using Serval.Server.Configuration;

namespace Serval.Server.Tests;

/// <summary>
/// The startup guard that stops this repository's own example secrets being what a deployment
/// actually runs on. Worth tests rather than trusting a comment: the failure it prevents is silent
/// — a server holding a world-readable signing key works perfectly until someone uses it.
/// </summary>
public class PlaceholderCredentialsTests
{
    [Theory]
    [InlineData("CHANGE-ME")]
    [InlineData("CHANGEME")]
    [InlineData("CHANGE_ME")]
    [InlineData("admin")]
    [InlineData("password")]
    public void Shipped_placeholders_are_refused(string value) =>
        Assert.True(PlaceholderCredentials.IsPlaceholder(value));

    [Theory]
    [InlineData("change-me")]
    [InlineData("Change-Me")]
    [InlineData("PASSWORD")]
    public void Casing_does_not_get_a_placeholder_past(string value) =>
        Assert.True(PlaceholderCredentials.IsPlaceholder(value));

    [Theory]
    [InlineData("  CHANGE-ME  ")]
    [InlineData("\tCHANGE-ME\n")]
    public void Surrounding_whitespace_does_not_either(string value) =>
        Assert.True(PlaceholderCredentials.IsPlaceholder(value));

    [Theory]
    [InlineData("kQ8vN2mP4xR7tY1wZ5aB3cD6eF9gH0jK")]
    [InlineData("a-real-passphrase-nobody-else-has")]
    [InlineData("CHANGE-ME-AND-THEN-I-DID")] // contains one, but is not one
    public void A_real_secret_is_allowed(string value) =>
        Assert.False(PlaceholderCredentials.IsPlaceholder(value));

    /// <summary>
    /// Empty is not this check's business. An unset BootstrapAdminPassword is a legitimate state —
    /// AdminBootstrap warns and skips — and an unset SigningKey is caught by its own guard with a
    /// message about what to generate. Answering true here would replace both with the wrong error.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_is_not_a_placeholder(string? value) =>
        Assert.False(PlaceholderCredentials.IsPlaceholder(value));

    [Fact]
    public void Throwing_names_the_key_and_how_to_replace_it()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PlaceholderCredentials.ThrowIfPlaceholder(
                "CHANGE-ME", "Serval:Auth:SigningKey", "Generate one with `openssl rand -base64 32`."));

        Assert.Contains("Serval:Auth:SigningKey", ex.Message, StringComparison.Ordinal);
        Assert.Contains("openssl rand -base64 32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_secret_passes_the_throwing_form() =>
        PlaceholderCredentials.ThrowIfPlaceholder(
            "kQ8vN2mP4xR7tY1wZ5aB3cD6eF9gH0jK", "Serval:Auth:SigningKey", "irrelevant");
}
