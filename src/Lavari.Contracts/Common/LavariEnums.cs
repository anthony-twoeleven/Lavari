using System.Text.Json.Serialization;

namespace Lavari.Contracts.Common;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApplicationVariableValueType
{
    String = 0,
    Int = 1,
    Bool = 2,
    Email = 3,
    Url = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LockableRecordType
{
    Job = 0,
    Quote = 1,
    Invoice = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimeEntrySourceType
{
    Timer = 0,
    FlatDuration = 1,
    TimeRange = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageAssociationType
{
    Library = 0,
    Client = 1,
    Job = 2,
    Product = 3,
    MoodBoard = 4,
    Receipt = 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MoodBoardElementType
{
    Image = 0,
    Text = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FinancialDocumentType
{
    Quote = 0,
    Invoice = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FinancialLineSourceType
{
    Manual = 0,
    JobLineItem = 1,
    TimeEntry = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FinancialAdjustmentType
{
    Discount = 0,
    Credit = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactSourceType
{
    Quote = 0,
    Invoice = 1,
    MoodBoard = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ForecastSourceType
{
    Milestone = 0,
    Invoice = 1,
    Quote = 2,
    Procurement = 3,
    Time = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobDateLinkedEntityType
{
    TimeEntry = 0,
    Procurement = 1,
    Invoice = 2,
    PaymentInstallment = 3,
    Quote = 4
}
