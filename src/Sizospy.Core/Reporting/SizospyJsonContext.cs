using System.Text.Json.Serialization;

namespace Sizospy.Reporting;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(SummaryReport))]
[JsonSerializable(typeof(IReadOnlyList<MemberReportRow>))]
internal partial class SizospyJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebReportModel))]
internal partial class WebJsonContext : JsonSerializerContext;
