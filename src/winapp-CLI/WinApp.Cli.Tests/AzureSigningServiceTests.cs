// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AzureSigningServiceTests
{
    private static AzureSigningService CreateService(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new AzureSigningService(NullLogger<AzureSigningService>.Instance, http);
    }

    [TestMethod]
    public async Task ListSubscriptionsAsync_ParsesAndFiltersByEnabledState()
    {
        const string json = """
        {
            "value": [
                { "subscriptionId": "sub-1", "displayName": "Enabled Sub", "state": "Enabled" },
                { "subscriptionId": "sub-2", "displayName": "Disabled Sub", "state": "Disabled" }
            ]
        }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, json);

        var service = CreateService(handler);
        var subscriptions = await service.ListSubscriptionsAsync("token");

        // Only the enabled subscription is returned.
        Assert.AreEqual(1, subscriptions.Count);
        Assert.AreEqual("sub-1", subscriptions[0].SubscriptionId);
        Assert.AreEqual("Enabled Sub", subscriptions[0].DisplayName);

        // The bearer token is attached and the subscriptions endpoint is targeted.
        Assert.AreEqual("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.AreEqual("token", handler.LastRequest.Headers.Authorization.Parameter);
        StringAssert.Contains(handler.LastRequestUri!, "https://management.azure.com/subscriptions?api-version=");
    }

    [TestMethod]
    public async Task ListSigningAccountsAsync_WithResourceGroup_BuildsScopedUrlAndParsesAccount()
    {
        const string json = """
        {
            "value": [
                {
                    "name": "myaccount",
                    "location": "eastus",
                    "id": "/subscriptions/sub-1/resourceGroups/my-rg/providers/Microsoft.CodeSigning/codeSigningAccounts/myaccount",
                    "properties": { "accountUri": "https://eus.codesigning.azure.net" }
                }
            ]
        }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, json);

        var service = CreateService(handler);
        var accounts = await service.ListSigningAccountsAsync("token", "sub-1", "my-rg");

        Assert.AreEqual(1, accounts.Count);
        Assert.AreEqual("myaccount", accounts[0].Name);
        Assert.AreEqual("my-rg", accounts[0].ResourceGroup);
        Assert.AreEqual("eastus", accounts[0].Location);
        Assert.AreEqual("https://eus.codesigning.azure.net", accounts[0].AccountUri);

        // Resource-group-scoped URL is used when a group is supplied.
        StringAssert.Contains(handler.LastRequestUri!, "/resourceGroups/my-rg/providers/Microsoft.CodeSigning/codeSigningAccounts");
    }

    [TestMethod]
    public async Task ListSigningAccountsAsync_WithoutResourceGroup_BuildsSubscriptionWideUrl()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "value": [] }""");

        var service = CreateService(handler);
        var accounts = await service.ListSigningAccountsAsync("token", "sub-1");

        Assert.AreEqual(0, accounts.Count);
        StringAssert.Contains(handler.LastRequestUri!, "/subscriptions/sub-1/providers/Microsoft.CodeSigning/codeSigningAccounts");
        Assert.IsFalse(handler.LastRequestUri!.Contains("/resourceGroups/"), "Should not include a resource group segment");
    }

    [TestMethod]
    public async Task ListCertificateProfilesAsync_ParsesProfiles()
    {
        const string json = """
        {
            "value": [
                { "name": "profile-a", "properties": { "profileType": "PublicTrust", "status": "Active" } }
            ]
        }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, json);

        var service = CreateService(handler);
        var profiles = await service.ListCertificateProfilesAsync("token", "sub-1", "my-rg", "myaccount");

        Assert.AreEqual(1, profiles.Count);
        Assert.AreEqual("profile-a", profiles[0].Name);
        Assert.AreEqual("PublicTrust", profiles[0].ProfileType);
        Assert.AreEqual("Active", profiles[0].Status);
        StringAssert.Contains(handler.LastRequestUri!, "/codeSigningAccounts/myaccount/certificateProfiles");
    }

    [TestMethod]
    public async Task GetArmResponse_OnErrorStatus_SurfacesParsedAzureError()
    {
        const string errorJson = """
        { "error": { "code": "AuthorizationFailed", "message": "The client does not have authorization." } }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.Forbidden, errorJson);

        var service = CreateService(handler);
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ListSubscriptionsAsync("token"));

        StringAssert.Contains(ex.Message, "AuthorizationFailed");
        StringAssert.Contains(ex.Message, "The client does not have authorization.");
    }

    [TestMethod]
    public async Task GetArmResponse_OnErrorStatusWithNonJsonBody_FallsBackToStatusCode()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "<html>boom</html>");

        var service = CreateService(handler);
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ListSubscriptionsAsync("token"));

        StringAssert.Contains(ex.Message, "500");
    }

    [TestMethod]
    public async Task ListSubscriptionsAsync_FollowsNextLinkAcrossPages()
    {
        const string page1 = """
        {
            "value": [ { "subscriptionId": "sub-1", "displayName": "Page 1 Sub", "state": "Enabled" } ],
            "nextLink": "https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=page2"
        }
        """;
        const string page2 = """
        {
            "value": [ { "subscriptionId": "sub-2", "displayName": "Page 2 Sub", "state": "Enabled" } ]
        }
        """;
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page1) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page2) });

        var service = new AzureSigningService(NullLogger<AzureSigningService>.Instance, new HttpClient(handler));
        var subscriptions = await service.ListSubscriptionsAsync("token");

        Assert.AreEqual(2, subscriptions.Count, "Both pages of results should be combined");
        Assert.AreEqual("sub-1", subscriptions[0].SubscriptionId);
        Assert.AreEqual("sub-2", subscriptions[1].SubscriptionId);
        Assert.AreEqual(2, handler.RequestUris.Count, "Both the initial URL and the nextLink should be requested");
        StringAssert.Contains(handler.RequestUris[1], "$skiptoken=page2");
    }

    [TestMethod]
    public async Task ListSubscriptionsAsync_RepeatedNextLink_ThrowsInsteadOfLoopingForever()
    {
        const string cyclic = """
        {
            "value": [ { "subscriptionId": "sub-1", "displayName": "Loop Sub", "state": "Enabled" } ],
            "nextLink": "https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=loop"
        }
        """;
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cyclic) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cyclic) });

        var service = new AzureSigningService(NullLogger<AzureSigningService>.Instance, new HttpClient(handler));
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ListSubscriptionsAsync("token"));

        StringAssert.Contains(ex.Message, "repeated nextLink");
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("QueueHttpMessageHandler received more requests than queued responses.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
