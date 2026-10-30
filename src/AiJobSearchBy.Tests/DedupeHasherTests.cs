using AiJobSearchBy.Services;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: гейты дедупа (architecture.md §10): композит ДО
// нормализации, детерминизм, защита от склеек.
// Вектор Compute пинит ВЕРХНИЙ регистр hex — так в .NET
// (Convert.ToHexString).
public class DedupeHasherTests
{
    [Fact]
    public void Compute_ReturnsKnownSha256Hex()
    {
        // Известный вектор SHA256("abc").
        Assert.Equal(
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            DedupeHasher.Compute("abc"));
    }

    [Fact]
    public void Compute_IsDeterministicUppercaseHex()
    {
        var first = DedupeHasher.Compute("remoteok|123|https://remoteok.com/remote-jobs/123");
        var second = DedupeHasher.Compute("remoteok|123|https://remoteok.com/remote-jobs/123");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.All(first, c => Assert.True(
            char.IsAsciiHexDigitUpper(c),
            $"ожидался ВЕРХНИЙ hex (Convert.ToHexString), получен '{c}'"));
    }

    [Fact]
    public void ComputeComposite_IsDeterministic_And64Hex()
    {
        var first = DedupeHasher.ComputeComposite("remoteok", "123", "https://remoteok.com/remote-jobs/123");
        var second = DedupeHasher.ComputeComposite("remoteok", "123", "https://remoteok.com/remote-jobs/123");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void ComputeComposite_DelimiterPreventsGluing()
    {
        // '\n'-разделитель: склейка компонентов неоднозначна без него
        // (("ab","c") vs ("a","bc") — классическая ловушка конкатенации).
        var gluedLeft = DedupeHasher.ComputeComposite("ab", "c", "url");
        var gluedRight = DedupeHasher.ComputeComposite("a", "bc", "url");

        Assert.NotEqual(gluedLeft, gluedRight);
    }

    [Fact]
    public void ComputeComposite_DiffersByPortal_AndByUrl()
    {
        var viaRemoteok = DedupeHasher.ComputeComposite("remoteok", "42", "https://x.test/v/42");
        var viaRemotive = DedupeHasher.ComputeComposite("remotive", "42", "https://x.test/v/42");
        var otherUrl = DedupeHasher.ComputeComposite("remoteok", "42", "https://x.test/v/43");

        Assert.NotEqual(viaRemoteok, viaRemotive);
        Assert.NotEqual(viaRemoteok, otherUrl);
    }

    [Fact]
    public void ComputeComposite_HashesRawValues_BeforeNormalization()
    {
        // Семантика «ДО нормализации» (architecture.md §10): хэшер не тримит и не чистит —
        // сырые значения. Разные сырые URL → разные хэши.
        var raw = DedupeHasher.ComputeComposite("belmeta", "7", "https://belmeta.com/vacansii/7 ");
        var trimmed = DedupeHasher.ComputeComposite("belmeta", "7", "https://belmeta.com/vacansii/7");

        Assert.NotEqual(raw, trimmed);
    }

    [Fact]
    public void ComputeComposite_NullExternalId_ParticipatesAsEmpty()
    {
        var withNull = DedupeHasher.ComputeComposite("portal", null, "https://x.test/v/1");
        var withEmpty = DedupeHasher.ComputeComposite("portal", string.Empty, "https://x.test/v/1");

        Assert.Equal(withNull, withEmpty);
    }

    [Theory]
    [InlineData("", "1", "https://x.test/v/1")]
    [InlineData(" ", "1", "https://x.test/v/1")]
    [InlineData("portal", "1", "")]
    [InlineData("portal", "1", " ")]
    public void ComputeComposite_BlankPortalIdOrUrl_Throws(string portalId, string externalId, string canonicalUrl)
    {
        Assert.Throws<ArgumentException>(
            () => DedupeHasher.ComputeComposite(portalId, externalId, canonicalUrl));
    }
}
