using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpsBridge.Api;

/// <summary>
/// The single source of truth for the JSON wire contract of OpsBridge.Api.
///
/// WHY THIS EXISTS
/// ---------------
/// The API and its clients disagreed about enums. System.Text.Json's default
/// WRITES an enum as its ordinal number ("status": 1) and REFUSES to bind a
/// string to an enum, but the web client declares every enum as a string
/// literal and SENDS enums as names
/// (src/OpsBridge.Web/src/types/index.ts, and the review endpoint's
/// `{"action":"ApprovedWithTolerance"}` body).
///
/// The consequence was a white screen, not a cosmetic difference: the invoice
/// list returned `"status": 1`, `StatusBadge` called `status?.toLowerCase()`
/// on the number, the component threw, React unmounted the tree, and
/// /invoices and /reconciliation rendered nothing.
///
/// The contract is "the enum name" (the usual JSON convention and the one the
/// client was written against), so the API is changed to match it:
///   * responses: "status": 1  ->  "status": "PendingReconciliation"
///   * requests : still accept the integer form, because
///                JsonStringEnumConverter reads numbers as well as names.
///
/// The same options object is used by the server (via
/// `ConfigureHttpJsonOptions`) and by any client that has to bind an
/// OpsBridge response back into a domain type - the integration tests do
/// exactly that - so the contract is defined once instead of twice.
/// </summary>
public static class OpsBridgeJsonContract
{
    /// <summary>
    /// The converter that makes an enum travel as its name. Reads both names
    /// and ordinals, writes names.
    /// </summary>
    public static JsonStringEnumConverter EnumConverter { get; } = new();

    /// <summary>
    /// The serializer options of the API, for a client that has to read an
    /// OpsBridge response back into a domain type. Mirrors the defaults of
    /// ASP.NET Core minimal APIs (camelCase, case-insensitive) plus the enum
    /// contract above.
    /// </summary>
    public static JsonSerializerOptions CreateSerializerOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { EnumConverter }
    };
}
