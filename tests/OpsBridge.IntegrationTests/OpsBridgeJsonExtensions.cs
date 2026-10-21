using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using OpsBridge.Api;

namespace OpsBridge.IntegrationTests;

/// <summary>
/// Reads an OpsBridge.Api response body back into a domain type, using the
/// SAME JSON contract the API serialises with.
///
/// WHY THIS EXISTS
/// ---------------
/// OpsBridge.Api serialises an enum as its NAME ("status":
/// "PendingReconciliation") - see OpsBridgeJsonContract for the full
/// reasoning. `HttpContent.ReadFromJsonAsync` without options uses
/// System.Text.Json's defaults, which cannot bind a name to an enum:
/// it throws "The JSON value could not be converted to
/// OpsBridge.Domain.Reconciliation.InvoiceStatus".
///
/// So every response the tests bind back into a domain type has to follow the
/// API's contract. The options come from OpsBridgeJsonContract itself - the
/// contract is defined once, in the API, and the test client follows it.
/// </summary>
internal static class OpsBridgeJsonExtensions
{
    private static readonly JsonSerializerOptions Options = OpsBridgeJsonContract.CreateSerializerOptions();

    /// <summary>
    /// Deserializes the response body with the API's JSON contract.
    /// </summary>
    public static Task<T?> ReadOpsBridgeJsonAsync<T>(this HttpContent content) =>
        content.ReadFromJsonAsync<T>(Options);
}
