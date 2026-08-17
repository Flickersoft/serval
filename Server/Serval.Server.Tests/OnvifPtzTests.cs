using System.Text;
using System.Xml.Linq;
using Serval.Server.Onvif;

namespace Serval.Server.Tests;

/// <summary>
/// The parts of ONVIF PTZ decidable without a camera: the WS-Security digest (get the byte order
/// or encoding wrong and every camera rejects the call) and the SOAP envelope shape. The HTTP
/// round-trip, service discovery, and profile selection need a real camera and are verified live.
/// </summary>
public class OnvifPtzTests
{
    private static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    [Fact]
    public void PasswordDigest_matches_an_independent_reference_vector()
    {
        // Reference computed independently (Python: base64(sha1(nonce + created + password))).
        byte[] nonce = Encoding.ASCII.GetBytes("0123456789abcdef");
        const string created = "2020-01-02T03:04:05.678Z";
        const string password = "hunter2";

        string digest = OnvifClient.PasswordDigest(password, nonce, created);

        Assert.Equal("ttB++jzfr/Peg5UJ2ccegn6aXTw=", digest);
    }

    [Fact]
    public void PasswordDigest_is_sensitive_to_concatenation_order()
    {
        // A guard against the classic bug of hashing password+nonce+created (or any reorder):
        // the reference vector above only holds for nonce + created + password.
        byte[] nonce = Encoding.ASCII.GetBytes("0123456789abcdef");
        string wrongOrder = Convert.ToBase64String(
            System.Security.Cryptography.SHA1.HashData(
                Encoding.UTF8.GetBytes("hunter2").Concat(nonce).Concat(Encoding.UTF8.GetBytes("2020-01-02T03:04:05.678Z")).ToArray()));

        Assert.NotEqual(wrongOrder, OnvifClient.PasswordDigest("hunter2", nonce, "2020-01-02T03:04:05.678Z"));
    }

    [Fact]
    public void SecurityHeader_carries_username_digest_and_base64_nonce()
    {
        byte[] nonce = Encoding.ASCII.GetBytes("0123456789abcdef");
        var created = new DateTimeOffset(2020, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

        XElement header = OnvifClient.BuildSecurityHeader("admin", "hunter2", nonce, created);

        XElement token = header.Descendants(Wsse + "UsernameToken").Single();
        Assert.Equal("admin", token.Element(Wsse + "Username")!.Value);
        Assert.Equal("ttB++jzfr/Peg5UJ2ccegn6aXTw=", token.Element(Wsse + "Password")!.Value);
        Assert.Equal(Convert.ToBase64String(nonce), token.Element(Wsse + "Nonce")!.Value);

        // The password itself must never appear anywhere in the header.
        Assert.DoesNotContain("hunter2", header.ToString());
    }

    [Fact]
    public void SecurityHeader_marks_password_as_a_digest_not_cleartext()
    {
        XElement header = OnvifClient.BuildSecurityHeader("admin", "pw", new byte[16], DateTimeOffset.UtcNow);

        string type = header.Descendants(Wsse + "Password").Single().Attribute("Type")!.Value;
        Assert.EndsWith("#PasswordDigest", type);
    }
}
