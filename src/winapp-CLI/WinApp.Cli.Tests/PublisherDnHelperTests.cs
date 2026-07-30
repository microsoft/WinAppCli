// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class PublisherDnHelperTests
{
    #region IsDistinguishedName

    [TestMethod]
    [DataRow("CN=Simple", true, DisplayName = "Simple CN")]
    [DataRow("CN=Company, O=Org", true, DisplayName = "Multi-component CN")]
    [DataRow("OU=Finance, DC=corp, DC=com", true, DisplayName = "OU-based DN")]
    [DataRow("O=Contoso Ltd, C=US", true, DisplayName = "O-based DN")]
    [DataRow("DC=example, DC=com", true, DisplayName = "DC-based DN")]
    [DataRow("CN=\"Company, Inc.\"", true, DisplayName = "Quoted CN value")]
    [DataRow("cn=lowercase", true, DisplayName = "Lowercase attribute type")]
    [DataRow("", false, DisplayName = "Empty string")]
    [DataRow("   ", false, DisplayName = "Whitespace only")]
    [DataRow("Hello", false, DisplayName = "Bare name")]
    [DataRow("=", false, DisplayName = "Just equals sign")]
    public void IsDistinguishedName_ReturnsExpected(string input, bool expected)
    {
        Assert.AreEqual(expected, PublisherDnHelper.IsDistinguishedName(input));
    }

    [TestMethod]
    public void IsDistinguishedName_NullInput_ReturnsFalse()
    {
        Assert.AreEqual(false, PublisherDnHelper.IsDistinguishedName(null!));
    }

    #endregion

    #region Normalize

    [TestMethod]
    [DataRow("SimpleName", "CN=SimpleName", DisplayName = "Bare name gets CN= prefix")]
    [DataRow("CN=Already", "CN=Already", DisplayName = "CN DN passes through")]
    [DataRow("OU=Finance, DC=corp, DC=com", "OU=Finance, DC=corp, DC=com", DisplayName = "Non-CN DN passes through")]
    [DataRow("  CN=Trimmed  ", "CN=Trimmed", DisplayName = "Whitespace trimmed")]
    [DataRow("\"CN=Quoted\"", "CN=Quoted", DisplayName = "Wrapper quotes stripped")]
    [DataRow("'CN=SingleQuoted'", "CN=SingleQuoted", DisplayName = "Single wrapper quotes stripped")]
    [DataRow("Last, First", "CN=\"Last, First\"", DisplayName = "Bare name with comma is escaped")]
    [DataRow("A&B Corp", "CN=A&B Corp", DisplayName = "Bare name with ampersand")]
    [DataRow("cn=lowercase", "cn=lowercase", DisplayName = "Lowercase attribute type passes through")]
    public void Normalize_ReturnsExpected(string input, string expected)
    {
        var result = PublisherDnHelper.Normalize(input);
        // Compare via X500DistinguishedName RawData for semantic equality
        var expectedDn = new System.Security.Cryptography.X509Certificates.X500DistinguishedName(expected);
        var actualDn = new System.Security.Cryptography.X509Certificates.X500DistinguishedName(result);
        Assert.IsTrue(
            expectedDn.RawData.AsSpan().SequenceEqual(actualDn.RawData.AsSpan()),
            $"DN mismatch.\nExpected: {expected}\nActual:   {result}");
    }

    [TestMethod]
    public void Normalize_NullInput_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PublisherDnHelper.Normalize(null!));
    }

    [TestMethod]
    [DataRow("", DisplayName = "Empty string")]
    [DataRow("   ", DisplayName = "Whitespace only")]
    [DataRow("\"\"", DisplayName = "Empty wrapper quotes")]
    public void Normalize_RejectsEmptyInput(string input)
    {
        Assert.ThrowsExactly<ArgumentException>(() => PublisherDnHelper.Normalize(input));
    }

    [TestMethod]
    public void Normalize_PreservesInternalQuotes()
    {
        // A DN with quoted value should NOT have its quotes stripped
        var result = PublisherDnHelper.Normalize("CN=\"Company, Inc.\"");
        Assert.IsTrue(PublisherDnHelper.IsDistinguishedName(result));
    }

    #endregion

    #region GetDisplayName

    [TestMethod]
    [DataRow("CN=SimplePublisher", "SimplePublisher", DisplayName = "Simple CN → bare name")]
    [DataRow("CN=Company, O=Org, C=US", "CN=Company, O=Org, C=US", DisplayName = "Multi-component → full DN")]
    [DataRow("OU=Finance, DC=corp, DC=com", "OU=Finance, DC=corp, DC=com", DisplayName = "Non-CN multi → full DN")]
    [DataRow("OU=Finance", "OU=Finance", DisplayName = "Non-CN single → full DN")]
    [DataRow("CN=\"Company, Inc.\"", "Company, Inc.", DisplayName = "Quoted CN → unquoted value")]
    [DataRow("CN=a\\,b", "a\\,b", DisplayName = "Backslash-escaped comma is not a component separator")]
    public void GetDisplayName_ReturnsExpected(string dn, string expected)
    {
        Assert.AreEqual(expected, PublisherDnHelper.GetDisplayName(dn));
    }

    [TestMethod]
    [DataRow(null, DisplayName = "Null input")]
    [DataRow("", DisplayName = "Empty string")]
    [DataRow("   ", DisplayName = "Whitespace only")]
    public void GetDisplayName_HandlesNullAndEmpty(string? dn)
    {
        // Should return the input as-is without throwing
        Assert.AreEqual(dn, PublisherDnHelper.GetDisplayName(dn!));
    }

    #endregion

    #region XmlEscape

    [TestMethod]
    [DataRow("CN=Simple", "CN=Simple", DisplayName = "No special chars")]
    [DataRow("CN=\"Company, Inc.\"", "CN=&quot;Company, Inc.&quot;", DisplayName = "Quotes escaped")]
    [DataRow("CN=A&B", "CN=A&amp;B", DisplayName = "Ampersand escaped")]
    [DataRow("CN=<Test>", "CN=&lt;Test&gt;", DisplayName = "Angle brackets escaped")]
    [DataRow("O=Tom's Co", "O=Tom&apos;s Co", DisplayName = "Apostrophe escaped")]
    [DataRow("", "", DisplayName = "Empty string passes through")]
    public void XmlEscape_ReturnsExpected(string input, string expected)
    {
        Assert.AreEqual(expected, PublisherDnHelper.XmlEscape(input));
    }

    [TestMethod]
    public void XmlEscape_NullInput_ReturnsNull()
    {
        Assert.IsNull(PublisherDnHelper.XmlEscape(null!));
    }

    #endregion
}
