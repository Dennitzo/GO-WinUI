using GoAi.Client;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GoAi.Server.Tests;

public sealed class GoAiClientFactoryTests
{
    [Fact]
    public void PinnedValidatorBuildsCaddyStyleIntermediateChainAndPreservesIpNameValidation()
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            "CN=GO AI Test Root",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 1, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        using var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest(
            "CN=GO AI Test Intermediate",
            intermediateKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        using var intermediatePublic = intermediateRequest.Create(
            root,
            now.AddHours(-1),
            now.AddYears(2),
            RandomNumberGenerator.GetBytes(16));
        using var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            "CN=GO AI Test Server",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddIpAddress(IPAddress.Parse("192.168.0.67"));
        leafRequest.CertificateExtensions.Add(subjectAlternativeNames.Build());
        using var leaf = leafRequest.Create(
            intermediate,
            now.AddMinutes(-5),
            now.AddMonths(3),
            RandomNumberGenerator.GetBytes(16));

        using var serverChain = new X509Chain();
        serverChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        serverChain.ChainPolicy.CustomTrustStore.Add(root);
        serverChain.ChainPolicy.ExtraStore.Add(intermediate);
        serverChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(serverChain.Build(leaf));

        var validator = GoAiClientFactory.CreatePinnedChainValidator(root);
        Assert.True(validator(leaf, serverChain, SslPolicyErrors.None));
        Assert.False(validator(leaf, serverChain, SslPolicyErrors.RemoteCertificateNameMismatch));
    }
}
