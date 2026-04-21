namespace CwsEditor.Core;

public sealed class CwsEditorException : Exception
{
    public CwsEditorException(string message)
        : base(message)
    {
    }

    public CwsEditorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
