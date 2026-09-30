namespace ACCDualSenseFeedback.Runtime;

internal sealed class RuntimeDiagnosticException : Exception
{
    public string DiagnosticCode { get; }

    public RuntimeDiagnosticException(string diagnosticCode, string message)
        : base(message)
    {
        DiagnosticCode = diagnosticCode;
    }

    public RuntimeDiagnosticException(string diagnosticCode, string message, Exception innerException)
        : base(message, innerException)
    {
        DiagnosticCode = diagnosticCode;
    }
}
