namespace Lavari.Domain.Errors;

public static class ErrorCodes
{
    public const string ValidationFailed = "LAV-VAL-001";
    public const string UnsupportedCurrency = "LAV-VAL-002";
    public const string SubCentAmountNotSupported = "LAV-VAL-003";
    public const string CurrencyMismatch = "LAV-VAL-004";
    public const string ArithmeticOverflow = "LAV-VAL-005";
    public const string InvalidTimeZone = "LAV-VAL-006";
    public const string AmbiguousOrInvalidLocalTime = "LAV-VAL-007";
    public const string InvalidImageUpload = "LAV-VAL-008";

    public const string Unauthenticated = "LAV-AUTH-001";
    public const string Forbidden = "LAV-AUTH-002";
    public const string RoleEscalationBlocked = "LAV-AUTH-003";
    public const string LastFullAccessAdminProtected = "LAV-AUTH-004";

    public const string NotFound = "LAV-DATA-001";
    public const string ConcurrencyConflict = "LAV-DATA-002";
    public const string ActiveChildrenPreventDelete = "LAV-DATA-003";
    public const string ActiveReferencesPreventDelete = "LAV-DATA-004";
    public const string ParentInactive = "LAV-DATA-005";
    public const string RecordImmutable = "LAV-DATA-006";
    public const string DuplicateBillingAllocation = "LAV-DATA-007";

    public const string EditLockRequired = "LAV-LOCK-001";
    public const string EditLockHeldByAnother = "LAV-LOCK-002";
    public const string EditLockExpired = "LAV-LOCK-003";
    public const string ReservationExpired = "LAV-LOCK-004";

    public const string TotalChangeConfirmationRequired = "LAV-FIN-001";
    public const string RunningTimersChoiceRequired = "LAV-TIME-001";
    public const string TimerStateInvalid = "LAV-TIME-002";

    public const string TemplateValidationFailed = "LAV-DOC-001";
    public const string DocumentRenderFailed = "LAV-DOC-002";
    public const string StorageFailure = "LAV-IO-001";
    public const string ExternalIntegrationUnavailable = "LAV-EXT-001";
    public const string UnexpectedError = "LAV-SYS-500";
}

public abstract class ServiceException : Exception
{
    protected ServiceException(
        string errorCode,
        string userMessage,
        string possibleCause,
        int httpStatusCode,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? fieldErrors = null,
        string? technicalDetails = null,
        Exception? innerException = null)
        : base(technicalDetails ?? userMessage, innerException)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
        PossibleCause = possibleCause;
        HttpStatusCode = httpStatusCode;
        FieldErrors = fieldErrors;
        TechnicalDetails = technicalDetails;
    }

    public string ErrorCode { get; }
    public string UserMessage { get; }
    public string PossibleCause { get; }
    public int HttpStatusCode { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? FieldErrors { get; }
    public string? TechnicalDetails { get; }
}

public sealed class ValidationServiceException : ServiceException
{
    public ValidationServiceException(
        string userMessage,
        string errorCode = ErrorCodes.ValidationFailed,
        string possibleCause = "Check the highlighted fields and correct any invalid or missing values.",
        IReadOnlyDictionary<string, IReadOnlyList<string>>? fieldErrors = null,
        string? technicalDetails = null)
        : base(errorCode, userMessage, possibleCause, 400, fieldErrors, technicalDetails)
    {
    }
}

public sealed class UnauthorizedServiceException : ServiceException
{
    public UnauthorizedServiceException(
        string userMessage = "You must be signed in to perform this action.",
        string possibleCause = "Your session may have expired. Sign in again and retry.",
        string? technicalDetails = null)
        : base(ErrorCodes.Unauthenticated, userMessage, possibleCause, 401, technicalDetails: technicalDetails)
    {
    }
}

public sealed class ForbiddenServiceException : ServiceException
{
    public ForbiddenServiceException(
        string userMessage = "You do not have permission to perform this action.",
        string errorCode = ErrorCodes.Forbidden,
        string possibleCause = "Contact an administrator if you need access to this module.",
        string? technicalDetails = null)
        : base(errorCode, userMessage, possibleCause, 403, technicalDetails: technicalDetails)
    {
    }
}

public sealed class NotFoundServiceException : ServiceException
{
    public NotFoundServiceException(
        string userMessage = "The requested record could not be found.",
        string possibleCause = "The record may have been deleted or you may not have access to it.",
        string? technicalDetails = null)
        : base(ErrorCodes.NotFound, userMessage, possibleCause, 404, technicalDetails: technicalDetails)
    {
    }
}

public sealed class ConflictServiceException : ServiceException
{
    public ConflictServiceException(
        string userMessage,
        string errorCode = ErrorCodes.ConcurrencyConflict,
        string possibleCause = "The record was changed by another operation. Reload the latest version and try again.",
        IReadOnlyDictionary<string, IReadOnlyList<string>>? fieldErrors = null,
        string? technicalDetails = null)
        : base(errorCode, userMessage, possibleCause, 409, fieldErrors, technicalDetails)
    {
    }
}

public sealed class ExternalIntegrationServiceException : ServiceException
{
    public ExternalIntegrationServiceException(
        string userMessage,
        string errorCode = ErrorCodes.ExternalIntegrationUnavailable,
        string possibleCause = "Check the integration configuration or try again later.",
        string? technicalDetails = null,
        Exception? innerException = null)
        : base(errorCode, userMessage, possibleCause, 502, technicalDetails: technicalDetails, innerException: innerException)
    {
    }
}
