namespace JIM.Models.Exceptions;

public class InvalidSettingValuesException : OperationalException
{
    public InvalidSettingValuesException(string message) : base(message) { }
}