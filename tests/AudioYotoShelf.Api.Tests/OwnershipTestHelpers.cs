using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AudioYotoShelf.Api.Tests;

/// <summary>Small helpers shared by the controller tests that check who may see or change what.</summary>
internal static class OwnershipTestHelpers
{
    /// <summary>The body of an <see cref="OkObjectResult"/> (often an anonymous type) as JSON, so tests can read its fields.</summary>
    public static JsonElement AsJson(this object? body) => JsonSerializer.SerializeToElement(body);

    /// <summary>Titles of the transfers in a <c>GetTransfers</c> response, in the order returned.</summary>
    public static string[] ResultTitles(this JsonElement page) =>
        page.GetProperty("Results").EnumerateArray().Select(r => r.GetProperty("BookTitle").GetString()!).ToArray();

    /// <summary>Verifies exactly one Hangfire job for <typeparamref name="TService"/>.<paramref name="method"/> was enqueued.</summary>
    public static void VerifyEnqueued<TService>(
        this Mock<IBackgroundJobClient> jobs, string method, Func<object[], bool> argsMatch, Times? times = null) =>
        jobs.Verify(b => b.Create(
            It.Is<Job>(j => j.Type == typeof(TService) && j.Method.Name == method && argsMatch(j.Args.ToArray())),
            It.IsAny<IState>()), times ?? Times.Once());

    /// <summary>Verifies no Hangfire job at all was enqueued.</summary>
    public static void VerifyNothingEnqueued(this Mock<IBackgroundJobClient> jobs) =>
        jobs.Verify(b => b.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
}
